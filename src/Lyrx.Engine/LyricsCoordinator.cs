using Musebase.Core;
using Musebase.Core.Search;
using Musebase.Core.Translation;

namespace Musebase.Engine;

/// <summary>
/// 현재 표시할 가사 한 줄 (원문 + 번역).
/// Karaoke: 글자 단위 타임태그(있으면 글자 채움), LineSpanSeconds: 라인 표시 구간(초, 라인 단위 폴백용).
/// </summary>
public sealed record DisplayLine(
    string? Content,
    string? Translation,
    InlineTimeTags? Karaoke = null,
    double LineSpanSeconds = 0);

/// <summary>
/// 원본 AppController의 핵심 역할 포팅:
/// 트랙 변경 → 가사 검색 → 재생 위치 틱 → 현재 라인 이벤트.
/// 플랫폼 무관: 재생 소스는 <see cref="INowPlayingSource"/>, 스레드 마샬링·타이머는
/// <see cref="IEngineDispatcher"/>로 추상화되어 Windows/Android/macOS/서버가 공유한다.
/// 이벤트는 IEngineDispatcher가 게시하는 스레드(WPF=UI 스레드)에서 발생한다.
/// </summary>
public sealed class LyricsCoordinator : IDisposable
{
    private readonly INowPlayingSource _nowPlaying;
    private readonly IEngineDispatcher _dispatcher;
    private LyricsSearchService _search;
    private readonly IEngineTimer _timer;

    public void SetSearchService(LyricsSearchService search) => _search = search;

    private CancellationTokenSource? _searchCts;
    private int _lastLineIndex = int.MinValue;

    // 마지막으로 검색 파이프라인을 돌린 곡("제목|아티스트"). 같은 곡이 다시 통지되면 건너뛴다.
    private string? _searchedTrackKey;

    /// <summary>번역 양보를 포기하기까지의 시간(ms). 이 안에 안 오면 직접 번역한다.</summary>
    private const int YieldBudgetMs = 8000;
    private int _yieldRetryMs = 3000; // 서버가 제안한 재조회 간격(clamp된 값)

    /// <summary>
    /// 대상 언어 번역 없이 새 곡을 올린 뒤(번역기 없음·실패·한도) 서버 번역을 기다리는 시간(ms). 서버는 올라온 곡을
    /// 곧바로 번역하므로(자동 번역) 보통 몇 초 안에 붙는다. 원문은 이미 화면에 있어 표시는 늦지 않는다.
    /// </summary>
    public int ServerTranslationWaitMs { get; set; } = 10_000;

    /// <summary>위 대기 동안 서버에 다시 묻는 간격(ms).</summary>
    public int ServerTranslationPollMs { get; set; } = 2_500;

    // 텔레메트리 발화 제어: playback_source는 같은 트랙 반복 발화 방지, translation은 곡당 1회
    private string? _lastPlaybackSourceKey;
    private bool _translationReported;

    // 직렬화 표시 상태(StateChanged)용 현재 라인 스냅샷
    private DisplayLine? _currentLine;
    private DateTimeOffset? _currentLineStartedAt;

    public Lyrics? CurrentLyrics { get; private set; }
    public TrackInfo? CurrentTrack => _nowPlaying.CurrentTrack;

    /// <summary>직렬화 가능한 현재 표시 상태(원격 디스플레이·바인딩용).</summary>
    public PlaybackViewState CurrentState { get; private set; } = PlaybackViewState.Empty;

    /// <summary>표시 상태 변경(라인/재생상태). 원격 브로드캐스트·VM 바인딩 대상.</summary>
    public event Action<PlaybackViewState>? StateChanged;

    /// <summary>수동 싱크 오프셋(초). +면 가사가 빨라진다.</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>진단 로그 싱크(선택). 소비자가 Log.Write 등을 주입.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// 익명 텔레메트리 싱크(ADR-0004, 기본 Noop=무수집). 이벤트 type/props의 단일 진실은
    /// contracts/telemetry-events.md. ② 이벤트(곡 정보 포함)의 동의 필터링은 구현(플랫폼) 책임.
    /// </summary>
    public ITelemetry Telemetry { get; set; } = NoopTelemetry.Instance;

    /// <summary>기계번역 서비스 (키 미설정 시 IsEnabled=false로 무동작)</summary>
    public LyricsTranslationService? Translation { get; set; }

    /// <summary>곡 단위 가사 캐시 (히트 시 네트워크 검색 생략)</summary>
    public LyricsCacheStore? Cache { get; set; }

    /// <summary>
    /// 원격 가사 캐시(개인 서버). null이면 사용하지 않는다. 로컬 캐시 미스와 제공자 검색 **사이**에
    /// 조회하고, 새로 찾은 가사는 여기에도 올린다. 실패·미접속은 조용히 무시되므로 서버가 없어도
    /// 동작이 달라지지 않는다(가변 속성 — 설정에서 주소를 바꾸면 재시작 없이 반영된다).
    /// </summary>
    public IRemoteLyricsCache? RemoteCache { get; set; }

    /// <summary>DeepL target_lang (예: KO). 표시 우선순위 tr:{lang} → tr에도 사용.</summary>
    public string TargetLanguage { get; set; } = "KO";

    /// <summary>대상 언어 번역만 표시(제공자의 다른 언어 번역 숨김). 대상=중국어면 제외(제공자 번역이 곧 중국어).</summary>
    public bool ShowOnlyTargetTranslation { get; set; } = true;

    private string TargetLangLower => TargetLanguage.ToLowerInvariant();

    /// <summary>대상 언어가 중국어(zh/zh-hans/zh-hant)인가 — 제공자 번역(중국어)을 그대로 쓴다.</summary>
    private bool TargetIsChinese => TargetLangLower.StartsWith("zh", StringComparison.Ordinal);

    /// <summary>
    /// 표시할 번역 결정.
    /// - 대상=중국어: 제공자 번역(중국어)을 그대로 우선(없으면 tr:{target}).
    /// - "대상 언어만" 켬: tr:{target}(기계번역)만, 제공자의 다른 언어 번역은 숨김.
    /// - 끔: tr:{target} → 제공자 tr 폴백(기존 동작).
    /// </summary>
    private string? ResolveDisplayTranslation(LineAttachments att)
    {
        if (TargetIsChinese) return att.Translation(null, TargetLangLower);
        if (ShowOnlyTargetTranslation) return att.Translation(TargetLangLower);
        return att.Translation(TargetLangLower, null);
    }

    /// <summary>표시 정책 변경 등으로 현재 라인을 즉시 다시 발행하도록 한다.</summary>
    public void RefreshCurrentLine() => _lastLineIndex = int.MinValue;

    /// <summary>현재 라인 변경 (null = 가사 없음/재생 없음)</summary>
    public event Action<DisplayLine?>? CurrentLineChanged;

    /// <summary>현재 라인 시작 이후 경과 시간(초, 매 틱). 글자/라인 단위 카라오케 채움에 사용.</summary>
    public event Action<double>? LineProgressChanged;

    /// <summary>가사 검색 상태(구조화). 소비자가 현지화한다.</summary>
    public event Action<LyricsStatus>? StatusChanged;

    /// <summary>마지막으로 발행된 검색 상태(늦게 구독하는 소비자의 초기 복원용). CurrentLineChanged와 대칭.</summary>
    public LyricsStatus? CurrentStatus { get; private set; }

    private void RaiseStatus(LyricsStatus status)
    {
        CurrentStatus = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>대상 언어 번역의 표시 상태(정상/캐시/한도초과 등). UI가 소스 옆에 표기.</summary>
    public TranslationDisplayStatus CurrentTranslationStatus { get; private set; } = TranslationDisplayStatus.None;

    /// <summary>번역 표시 상태 변경 알림.</summary>
    public event Action<TranslationDisplayStatus>? TranslationStatusChanged;

    // 이번 번역 실행에서 마지막으로 보고된 실패(팩토리가 콜백으로 주입). 한도초과 판정용.
    private TranslatorFailure? _lastTranslationFailure;

    /// <summary>팩토리가 번역기 실패 콜백을 이리로 라우팅한다(한도초과 등 상태 판정용). App 콜백과 별개.</summary>
    internal void RecordTranslationFailure(TranslatorFailure failure) => _lastTranslationFailure = failure;

    private void SetTranslationStatus(TranslationDisplayStatus status)
    {
        if (CurrentTranslationStatus == status) return;
        CurrentTranslationStatus = status;
        TranslationStatusChanged?.Invoke(status);
    }

    /// <summary>"틀린 가사"로 표시된 트랙 키 집합(검색·표시 억제). 소비자가 설정과 동기화.</summary>
    public HashSet<string> SuppressedTrackKeys { get; } = new();

    /// <summary>억제 목록 변경 알림(설정 영속화용)</summary>
    public event Action? SuppressedTracksChanged;

    public LyricsCoordinator(INowPlayingSource nowPlaying, IEngineDispatcher dispatcher, LyricsSearchService? search = null)
    {
        _nowPlaying = nowPlaying;
        _dispatcher = dispatcher;
        _search = search ?? new LyricsSearchService();

        _timer = _dispatcher.CreateTimer(TimeSpan.FromMilliseconds(100), Tick);

        _nowPlaying.TrackChanged += track => _dispatcher.Post(() => OnTrackChanged(track));
        _nowPlaying.IsPlayingChanged += playing => _dispatcher.Post(() =>
        {
            if (playing) _timer.Start();
            else _timer.Stop();
            EmitState(); // 재생상태 변화를 표시 상태에 반영
        });
    }

    /// <summary>
    /// 이벤트 구독·속성(Cache/Translation) 배선이 끝난 뒤 호출.
    /// 생성자에서 시작하면 이니셜라이저 속성이 아직 null이라 캐시/번역이 무시된다.
    /// </summary>
    public void Start()
    {
        if (_nowPlaying.CurrentTrack is { } current) OnTrackChanged(current);
        if (_nowPlaying.IsPlaying) _timer.Start();
    }

    private async void OnTrackChanged(TrackInfo? track)
    {
        // 제목·아티스트가 그대로면 다시 검색하지 않는다. 재생 소스는 길이·앨범 같은 메타데이터를
        // 뒤늦게 채워 넣으며 트랙 변경을 한 번 더 통지하는데(TrackInfo는 record라 그 필드까지
        // 같아야 같은 값이다), 그때마다 파이프라인을 다시 돌리면 **가사 서버·제공자에 같은 요청이
        // 두 번** 나간다(실측: 안드로이드에서 1~8초 간격 중복 조회). 표시 상태는 갱신해 준다.
        var trackKey = track is null ? null : LyricsCacheStore.MakeKey(track.Title, track.Artist);
        if (trackKey is not null && trackKey == _searchedTrackKey)
        {
            EmitState(); // 길이·앨범 등 바뀐 메타데이터는 반영
            return;
        }
        _searchedTrackKey = trackKey;

        _searchCts?.Cancel();
        CurrentLyrics = null;
        _lastLineIndex = int.MinValue;
        _currentLine = null;
        _currentLineStartedAt = null;
        _translationReported = false; // translation 이벤트는 곡당 1회
        SetTranslationStatus(TranslationDisplayStatus.None); // 새 트랙 — 아직 번역 전
        CurrentLineChanged?.Invoke(null);
        EmitState(); // 새 트랙(제목) 반영, 라인은 아직 없음

        if (track is null)
        {
            RaiseStatus(new LyricsStatus(LyricsStatusKind.NoTrack));
            return;
        }

        // playback_source: 재생 소스 앱 id(곡 정보 없음). 같은 트랙의 반복 통지(SMTC 메타 재발화 등)는
        // 억제하고, 하루 앱별 1회 디바운스는 클라이언트(플랫폼 ITelemetry 구현) 책임.
        var playbackKey = $"{track.SourceAppId}|{LyricsCacheStore.MakeKey(track.Title, track.Artist)}";
        if (playbackKey != _lastPlaybackSourceKey)
        {
            _lastPlaybackSourceKey = playbackKey;
            Telemetry.Track(TelemetryEvents.PlaybackSource, new Dictionary<string, object?>
            {
                ["appId"] = track.SourceAppId,
            });
        }

        // "틀린 가사"로 표시된 곡은 검색·표시하지 않는다
        if (SuppressedTrackKeys.Contains(LyricsCacheStore.MakeKey(track.Title, track.Artist)))
        {
            RaiseStatus(new LyricsStatus(LyricsStatusKind.HiddenByUser, track.ToString()));
            return;
        }

        // 1) 캐시 히트면 네트워크 검색 생략 (단, 중국어 대상인데 번역 없는 구 캐시(LRCLIB 등)면 재검색해 번역본 확보)
        if (Cache?.Get(track.Title, track.Artist) is { } cached && (!TargetIsChinese || cached.HasTranslation()))
        {
            CurrentLyrics = cached;
            _lastLineIndex = int.MinValue;
            RaiseStatus(new LyricsStatus(LyricsStatusKind.Cache, track.ToString(), cached.Metadata.ServiceName ?? ""));
            // lyrics_search(캐시 적중): 네트워크 검색이 없었으므로 perSource는 빈 객체
            Telemetry.Track(TelemetryEvents.LyricsSearch, new Dictionary<string, object?>
            {
                ["winner"] = SourceIdOf(cached.Metadata.ServiceName),
                ["perSource"] = new Dictionary<string, object?>(),
                ["cached"] = true,
                ["cleanedQueryUsed"] = false,
            });
            var cacheCts = new CancellationTokenSource();
            _searchCts = cacheCts;
            // 보충 번역이 실제로 채워지면 캐시·서버에 되돌려 준다(persistAfter) — 저장 당시
            // 번역이 없던 곡이 영원히 번역 없이 남는 것을 막는다.
            await TranslateAsync(cached, cacheCts.Token, persistAfter: true);
            // 보충 번역 뒤에도 대상 언어가 없으면(번역기 없음·실패·한도) 서버가 그 뒤 채웠을 수 있다 —
            // 로컬 캐시에는 재검증이 없어, 여기서 묻지 않으면 서버 번역이 이 기기엔 영영 오지 않는다.
            if (CanReceiveServerTranslation && !HasTargetTranslation(cached))
                await TryTakeServerTranslationAsync(track, cached, pollMs: 0, budgetMs: 0, cacheCts.Token);
            return;
        }

        // 1-b) 가사 서버(개인 서버)에 물어본다 — 다른 기기가 이미 찾아 둔 가사·번역을 그대로 받는다.
        //      실패·미접속·미스는 모두 미스라 아래 제공자 검색으로 자연히 흘러간다.
        var yielding = false;
        if (RemoteCache is { } remoteCache)
        {
            var remoteCts = new CancellationTokenSource();
            _searchCts = remoteCts;
            var result = await remoteCache.GetAsync(track.Title, track.Artist, remoteCts.Token);
            if (remoteCts.Token.IsCancellationRequested) return; // 트랙이 또 바뀜

            // 미스인데 "다른 기기도 방금 이 곡을 물었다"면 번역을 양보한다 — 저쪽이 곧 번역본을
            // 올릴 테니 우리는 제공자 검색만 하고(원문 표시는 그대로) 번역은 미뤘다가 받아 쓴다.
            yielding = result is { Pending: true, Lyrics: null } && CanShareTranslation;
            if (yielding) _yieldRetryMs = Math.Clamp(result.RetryAfterMs, 1000, 5000);

            if (result.Lyrics is { } remote)
            {
                CurrentLyrics = remote;
                _lastLineIndex = int.MinValue;
                // 로컬 캐시로 승격 — 다음부터는 서버 없이(오프라인에서도) 즉시 뜬다.
                try { Cache?.Set(track.Title, track.Artist, remote); }
                catch (Exception e) { Log?.Invoke($"[server] 로컬 승격 실패: {e.Message}"); }

                RaiseStatus(new LyricsStatus(
                    LyricsStatusKind.Cache, track.ToString(), remote.Metadata.ServiceName ?? ""));
                Telemetry.Track(TelemetryEvents.LyricsSearch, new Dictionary<string, object?>
                {
                    ["winner"] = SourceIdOf(remote.Metadata.ServiceName),
                    ["perSource"] = new Dictionary<string, object?>(),
                    ["cached"] = true,
                    ["cleanedQueryUsed"] = false,
                });
                // 서버 저장본에 대상 언어 번역이 없으면 여기서 채우고, 채운 결과를 서버에 되돌린다.
                // 이게 없으면 기기마다 같은 곡을 각자 번역하게 된다(공유의 핵심 경로).
                await TranslateAsync(remote, remoteCts.Token, persistAfter: true);
                return;
            }

            // 서버가 이 제목을 광고로 표시해 뒀다면 여기서 끝낸다. 제공자 검색은 늘 헛돌고,
            // 어쩌다 뭔가 맞으면 광고 위에 엉뚱한 가사가 뜬다. 자동 판정(AdSignals)이 놓친
            // 경로 — 재생 메타데이터에 광고 플래그가 없는 경우 — 를 사람이 표시해 메운다.
            if (result.IsAd)
            {
                Log?.Invoke($"[ad] 서버가 광고로 표시한 제목 — 검색하지 않습니다: {track.Title}");
                RaiseStatus(new LyricsStatus(LyricsStatusKind.NotFound, track.ToString()));
                return;
            }
        }

        RaiseStatus(new LyricsStatus(LyricsStatusKind.Searching, track.ToString()));
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            var request = LyricsSearchRequest.ByInfo(
                track.Title, track.Artist, track.Duration?.TotalSeconds ?? 0, limit: 3);
            var diagnostics = new SearchDiagnostics();

            // 첫 결과 우선 표시 후 더 좋은 후보로 교체 (지연 체감 최소화)
            await foreach (var lyrics in _search.SearchAsync(request, cts.Token, diagnostics))
            {
                if (cts.Token.IsCancellationRequested) return;
                if (CurrentLyrics is null || lyrics.Quality() > CurrentLyrics.Quality())
                {
                    CurrentLyrics = lyrics;
                    _lastLineIndex = int.MinValue; // 라인 재계산 강제
                    RaiseStatus(new LyricsStatus(
                        LyricsStatusKind.Found, track.ToString(), lyrics.Metadata.ServiceName ?? "", lyrics.Quality()));
                    // 양보 중이면 후보마다 번역하지 않는다 — 루프가 끝난 뒤 최종 채택본에 대해
                    // 한 번만, 그것도 서버를 먼저 기다려 보고 번역한다.
                    if (!yielding) await TranslateAsync(lyrics, cts.Token);
                }
            }

            // 검색 1회 완료 — 트랙이 교체됐으면(취소) 발화하지 않는다
            if (!cts.Token.IsCancellationRequested)
                TrackLyricsSearch(request, diagnostics);

            // 양보: 저쪽 기기가 번역본을 올릴 시간을 잠깐 준다. 원문은 이미 화면에 있으므로
            // 여기서 기다려도 표시가 늦어지지 않는다(번역은 원래 나중에 붙는다).
            var tookShared = false;
            if (yielding && CurrentLyrics is { } adopted && !cts.Token.IsCancellationRequested)
            {
                tookShared = await TryTakeServerTranslationAsync(track, adopted, _yieldRetryMs, YieldBudgetMs, cts.Token);
                if (!tookShared)
                {
                    Log?.Invoke($"[server] 양보 시간 초과 — 직접 번역합니다: {track}");
                    await TranslateAsync(adopted, cts.Token);
                }
            }

            if (CurrentLyrics is null)
            {
                RaiseStatus(new LyricsStatus(LyricsStatusKind.NotFound, track.ToString()));
                if (!cts.Token.IsCancellationRequested)
                {
                    // ② 품질 리포트 — 곡 정보 포함. 동의 필터링은 ITelemetry 구현 책임.
                    Telemetry.Track(TelemetryEvents.LyricsNotFound, new Dictionary<string, object?>
                    {
                        ["title"] = track.Title,
                        ["artist"] = track.Artist,
                    });
                }
            }
            else if (!cts.Token.IsCancellationRequested)
            {
                // 2) 최종 선택본(번역 포함) 캐시 저장
                Task? upload = null;
                try
                {
                    Cache?.Set(track.Title, track.Artist, CurrentLyrics);
                    Log?.Invoke($"[cache] 저장: {track}");
                    // 가사 서버에도 올려 다른 기기가 재검색·재번역하지 않게 한다(실패는 무시).
                    // 방금 서버에서 받아 쓴 것(양보)은 되돌려 올리지 않는다 — 같은 내용으로
                    // revision만 올라간다.
                    if (!tookShared) upload = RemoteCache?.SetAsync(track.Title, track.Artist, CurrentLyrics);
                }
                catch (Exception e)
                {
                    Log?.Invoke($"[cache] 저장 실패: {e.Message}");
                }

                // 3) 대상 언어 없이 올렸으면(번역기 없음·실패·한도) 서버가 번역해 줄 때까지 잠깐 기다린다.
                if (upload is not null && CanReceiveServerTranslation &&
                    CurrentLyrics is { } uploaded && !HasTargetTranslation(uploaded))
                {
                    try { await upload; } catch { /* 업로드 실패는 구현이 삼킨다 — 기다려도 소용없을 뿐 */ }
                    if (!await TryTakeServerTranslationAsync(
                            track, uploaded, ServerTranslationPollMs, ServerTranslationWaitMs, cts.Token))
                        Log?.Invoke($"[server] 서버 번역이 아직 없습니다 — 다음 재생 때 받습니다: {track}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 다음 트랙으로 교체됨
        }
        catch (Exception e)
        {
            Log?.Invoke($"[search] 예외: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 현재 곡을 "틀린 가사"로 표시한다: 캐시에서 제거하고, 표시를 지우고,
    /// 트랙이 유지되는 동안(및 재생 복귀 시) 재검색·표시를 억제한다.
    /// </summary>
    public void MarkWrongLyrics()
    {
        if (CurrentTrack is not { } track) return;

        // ② 품질 리포트 — 채택됐던 소스 id는 지우기 전에 확보. 동의 필터링은 ITelemetry 구현 책임.
        Telemetry.Track(TelemetryEvents.WrongLyrics, new Dictionary<string, object?>
        {
            ["title"] = track.Title,
            ["artist"] = track.Artist,
            ["source"] = SourceIdOf(CurrentLyrics?.Metadata.ServiceName),
        });

        SuppressedTrackKeys.Add(LyricsCacheStore.MakeKey(track.Title, track.Artist));
        try { Cache?.Remove(track.Title, track.Artist); }
        catch (Exception e) { Log?.Invoke($"[wrong] 캐시 제거 실패: {e.Message}"); }

        _searchCts?.Cancel();
        CurrentLyrics = null;
        _lastLineIndex = int.MinValue;
        _currentLine = null;
        _currentLineStartedAt = null;
        CurrentLineChanged?.Invoke(null);
        EmitState();
        RaiseStatus(new LyricsStatus(LyricsStatusKind.Wrong, track.ToString()));
        SuppressedTracksChanged?.Invoke();
    }

    private void Unsuppress(TrackInfo? track)
    {
        if (track is null) return;
        if (SuppressedTrackKeys.Remove(LyricsCacheStore.MakeKey(track.Title, track.Artist)))
            SuppressedTracksChanged?.Invoke();
    }

    /// <summary>현재 곡의 로컬 캐시와 억제를 풀고 온라인 가사 소스에서 다시 검색한다.</summary>
    public void ReloadCurrentTrackLyrics()
    {
        if (CurrentTrack is not { } track) return;
        Unsuppress(track);
        try { Cache?.Remove(track.Title, track.Artist); } catch { }
        _searchedTrackKey = null;
        OnTrackChanged(track);
    }

    /// <summary>수동 검색 등 외부에서 선택한 가사를 적용하고 캐시를 갱신한다.</summary>
    public async Task UseLyricsAsync(Lyrics lyrics)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        Unsuppress(CurrentTrack); // 사용자가 직접 고른 가사이므로 억제 해제

        CurrentLyrics = lyrics;
        _lastLineIndex = int.MinValue;
        RaiseStatus(new LyricsStatus(
            LyricsStatusKind.Manual, CurrentTrack?.ToString() ?? "", lyrics.Metadata.ServiceName ?? ""));
        await TranslateAsync(lyrics, cts.Token);
        if (CurrentTrack is { } track && !cts.Token.IsCancellationRequested)
        {
            Cache?.Set(track.Title, track.Artist, lyrics);
            _ = RemoteCache?.SetAsync(track.Title, track.Artist, lyrics); // 수동 선택본도 공유
        }
    }

    /// <summary>
    /// 편집된 가사를 캐시에 저장하고(출처=사용자 편집), 여전히 같은 곡이면 즉시 반영한다.
    /// 사용자가 고친 번역을 덮어쓰지 않도록 기계번역(MT)은 실행하지 않는다.
    /// </summary>
    public void SaveEditedLyrics(TrackInfo track, Lyrics lyrics)
    {
        Unsuppress(track); // 편집·저장한 곡은 억제 해제
        lyrics.Metadata.ServiceName = "사용자 편집";
        try
        {
            Cache?.Set(track.Title, track.Artist, lyrics);
            Log?.Invoke($"[edit] 저장: {track}");
            // 편집본은 서버에서 origin=user로 보호되어 다른 기기의 자동 검색이 덮어쓰지 못한다.
            _ = RemoteCache?.SetAsync(track.Title, track.Artist, lyrics);
        }
        catch (Exception e)
        {
            Log?.Invoke($"[edit] 저장 실패: {e.Message}");
        }

        if (CurrentTrack is { } cur && cur.Title == track.Title && cur.Artist == track.Artist)
        {
            _searchCts?.Cancel(); // 진행 중 검색이 편집본을 덮어쓰지 않도록
            CurrentLyrics = lyrics;
            _lastLineIndex = int.MinValue; // 현재 라인 재발행
            RaiseStatus(new LyricsStatus(LyricsStatusKind.Edited, track.ToString()));
        }
    }

    /// <summary>
    /// 현재 가사의 번역을 다시 실행한다 — 번역 구성(엔진/키/API 사용 토글)을 바꾼 직후 호출하면
    /// 다음 곡을 기다리지 않고 지금 곡에 바로 반영된다. 가사가 없으면 아무것도 하지 않는다.
    /// </summary>
    public async Task RetranslateCurrentAsync()
    {
        if (CurrentLyrics is not { } lyrics) return;
        _searchCts?.Cancel(); // 진행 중 검색/번역과 겹치지 않게
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            if (!lyrics.HasTranslation() && CurrentTrack is { } track)
            {
                var request = LyricsSearchRequest.ByInfo(
                    track.Title, track.Artist, track.Duration?.TotalSeconds ?? 0, limit: 3);
                await foreach (var candidate in _search.SearchAsync(request, cts.Token))
                {
                    if (cts.Token.IsCancellationRequested) return;
                    if (candidate.HasTranslation() && candidate.IsMatched())
                    {
                        CurrentLyrics = candidate;
                        lyrics = candidate;
                        _lastLineIndex = int.MinValue;
                        try { Cache?.Set(track.Title, track.Artist, candidate); } catch { }
                        RaiseStatus(new LyricsStatus(
                            LyricsStatusKind.Found, track.ToString(), candidate.Metadata.ServiceName ?? "", candidate.Quality()));
                        break;
                    }
                }
            }
            await TranslateAsync(lyrics, cts.Token, persistAfter: true);
        }
        catch (OperationCanceledException) { /* 트랙 교체 등 — 무시 */ }
    }

    /// <summary>
    /// 대상 언어 MT 보장 후 현재 라인 갱신 (캐시 히트면 즉시, 미스면 API 1회).
    /// <paramref name="persistAfter"/>가 true이고 번역이 실제로 채워지면 로컬 캐시와 가사 서버에
    /// 되돌려 저장한다 — 호출자가 곧바로 저장하는 경로(제공자 검색·수동 선택)에서는 false로 두어
    /// 같은 내용을 두 번 올리지 않는다.
    /// </summary>
    private async Task TranslateAsync(Lyrics lyrics, CancellationToken ct, bool persistAfter = false)
    {
        // 대상=중국어이고 이미 제공자 번역(중국어)이 들어 있으면 기계번역을 거치지 않는다.
        if (TargetIsChinese && lyrics.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Attachments[LineAttachments.TagTranslationPrefix])))
        {
            CurrentTranslationStatus = TranslationDisplayStatus.Cache;
            TranslationStatusChanged?.Invoke(CurrentTranslationStatus);
            return;
        }
        if (Translation is not { IsEnabled: true } service) { SetTranslationStatus(TranslationDisplayStatus.None); return; }
        _lastTranslationFailure = null;
        // API 번역을 껐으면 "번역 중"으로 깜빡이지 않고 처음부터 꺼짐으로 표시한다.
        SetTranslationStatus(service.CacheOnly
            ? TranslationDisplayStatus.Disabled : TranslationDisplayStatus.Translating);
        try
        {
            var stats = new TranslationRunStats();
            var changed = await service.EnsureTranslatedAsync(lyrics, TargetLanguage, ct, stats);
            if (changed > 0 && ReferenceEquals(CurrentLyrics, lyrics))
            {
                _lastLineIndex = int.MinValue; // 번역 반영 위해 현재 라인 재발행
                TranslationStatusChanged?.Invoke(CurrentTranslationStatus);
            }

            // 번역 표시 상태 판정: API로 채워야 했는데 실패로 못 채운 라인이 남으면 실패(한도초과 등),
            // 이번에 API로 채웠으면 정상(Live), 그 외(전부 캐시/이미 번역됨)는 캐시.
            var apiFilled = changed - stats.CacheHits;          // 이번에 API로 채운 라인 수
            var apiNeeded = stats.LinesNeeded - stats.CacheHits; // API로 채워야 했던 라인 수
            // 사용자가 끔 — 캐시로만 채웠다. 캐시로 전부 덮였으면(=API가 필요한 줄이 남지 않으면)
            // 번역이 정상 표시되는 상태이므로 "꺼짐"만 보여 오해를 주지 않도록 구분한다.
            if (service.CacheOnly)
                SetTranslationStatus(apiNeeded > 0
                    ? TranslationDisplayStatus.Disabled : TranslationDisplayStatus.DisabledCached);
            else if (_lastTranslationFailure is { } f && apiFilled < apiNeeded)
                SetTranslationStatus(f.Kind is TranslatorFailureKind.Quota or TranslatorFailureKind.RateLimit
                    ? TranslationDisplayStatus.Quota : TranslationDisplayStatus.Failed);
            else if (apiFilled > 0)
                SetTranslationStatus(TranslationDisplayStatus.Live);
            else
                SetTranslationStatus(TranslationDisplayStatus.Cache); // 필요 없음(이미 번역됨) 또는 전부 캐시

            // 보충 번역분을 캐시·서버에 반영한다. changed == 0이면 이미 다 번역돼 있었다는 뜻이라
            // 아무것도 하지 않는다(같은 곡을 다시 틀 때마다 올리지 않는다).
            if (persistAfter && changed > 0 && !ct.IsCancellationRequested)
                PersistTranslated(lyrics);

            // translation: 번역이 실제로 필요했던 첫 완료 시점에 곡당 1회
            if (!_translationReported && stats.LinesNeeded > 0)
            {
                _translationReported = true;
                Telemetry.Track(TelemetryEvents.Translation, new Dictionary<string, object?>
                {
                    ["engine"] = service.EngineId,
                    ["cacheHitPct"] = stats.CacheHitPct,
                    ["linesBucket"] = LinesBucket(stats.LinesNeeded),
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 트랙 교체됨
        }
    }

    /// <summary>
    /// 번역을 남과 나눌 수 있는 상태인가 — 번역기가 켜져 있고 대상이 중국어가 아닐 때만
    /// "양보"에 의미가 있다(중국어는 제공자 번역을 그대로 쓰므로 API를 아예 거치지 않는다).
    /// </summary>
    private bool CanShareTranslation =>
        !TargetIsChinese && Translation is { IsEnabled: true, CacheOnly: false };

    /// <summary>
    /// 서버 번역을 받아 쓸 수 있는가. 번역기 유무가 아니라 <b>자기 번역을 해 본 뒤에도 대상 언어가
    /// 비었는가</b>로 판단한다(호출자가 <see cref="HasTargetTranslation"/>로 확인) — 번역기가 켜져
    /// 있어도 한도·실패로 비는 일이 흔하고(MyMemory 무키 하루 한도), 그런 기기가 서버에 이미 있는
    /// 번역을 영영 못 받던 문제가 실제로 있었다. 번역에 성공한 기기는 추가 요청이 없다.
    /// </summary>
    private bool CanReceiveServerTranslation => RemoteCache is not null && !TargetIsChinese;

    private bool HasTargetTranslation(Lyrics lyrics) =>
        lyrics.Lines.Any(l => l.Attachments.Translation(TargetLangLower) is not null);

    /// <summary>
    /// 서버에 대상 언어 번역본이 생기면 받아 쓴다. 받았으면 true(직접 번역하지 않는다).
    /// 다른 기기가 올릴 번역을 기다리는 양보, 서버의 자동 번역 대기, 캐시 히트 뒤 한 번 확인이
    /// 모두 이것이다 — <paramref name="pollMs"/>마다 다시 묻고 <paramref name="budgetMs"/>가 지나면
    /// 그만둔다(0/0이면 곧바로 한 번만 묻는다).
    ///
    /// 원문 가사는 이미 화면에 있으므로 이 대기는 표시를 늦추지 않는다 — 번역이 몇 초 뒤에
    /// 붙는 것은 원래 동작이다.
    /// </summary>
    private async Task<bool> TryTakeServerTranslationAsync(
        TrackInfo track, Lyrics current, int pollMs, int budgetMs, CancellationToken ct)
    {
        if (RemoteCache is not { } remoteCache) return false;

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(budgetMs);
        do
        {
            if (pollMs > 0)
            {
                try { await Task.Delay(pollMs, ct); }
                catch (OperationCanceledException) { return false; }
            }
            if (ct.IsCancellationRequested || !ReferenceEquals(CurrentLyrics, current)) return false;

            var result = await remoteCache.GetAsync(track.Title, track.Artist, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(CurrentLyrics, current)) return false;
            if (result.Lyrics is not { } shared || !result.HasLanguage(TargetLangLower)) continue;

            CurrentLyrics = shared;
            _lastLineIndex = int.MinValue; // 번역이 붙은 줄로 다시 발행
            try { Cache?.Set(track.Title, track.Artist, shared); }
            catch (Exception e) { Log?.Invoke($"[server] 서버 번역분 캐시 저장 실패: {e.Message}"); }
            SetTranslationStatus(TranslationDisplayStatus.Cache);
            Log?.Invoke($"[server] 서버의 번역을 받아 썼습니다 — {track}");
            return true;
        }
        while (DateTimeOffset.UtcNow < deadline);

        return false;
    }

    /// <summary>
    /// 보충 번역이 채워진 가사를 로컬 캐시에 다시 저장하고 가사 서버에도 올린다.
    /// 여전히 같은 곡을 표시 중일 때만 한다(트랙이 바뀌었으면 남의 곡 키로 저장될 수 있다).
    /// 서버 병합 정책은 번역 언어가 늘어난 갱신을 항상 채택하므로 안전하게 덮인다.
    /// </summary>
    private void PersistTranslated(Lyrics lyrics)
    {
        if (!ReferenceEquals(CurrentLyrics, lyrics)) return;
        if (CurrentTrack is not { } track) return;

        try
        {
            Cache?.Set(track.Title, track.Artist, lyrics);
            _ = RemoteCache?.SetAsync(track.Title, track.Artist, lyrics);
            Log?.Invoke($"[translate] 보충 번역 반영: {track}");
        }
        catch (Exception e)
        {
            Log?.Invoke($"[translate] 보충 번역 저장 실패: {e.Message}");
        }
    }

    /// <summary>lyrics_search 발화(계약 ① — 곡 정보 없음): 소스별 히트/지연 + 채택 소스 + 정제 검색어 사용 여부.</summary>
    private void TrackLyricsSearch(LyricsSearchRequest request, SearchDiagnostics diagnostics)
    {
        var winner = CurrentLyrics;

        var perSource = new Dictionary<string, object?>();
        foreach (var (serviceName, stat) in diagnostics.PerSource)
        {
            perSource[SourceIdOf(serviceName)] = new Dictionary<string, object?>
            {
                ["hit"] = stat.Hit,
                ["latencyMs"] = stat.LatencyMs,
            };
        }

        // Metadata.Request는 실제 사용된 요청 — 원본과 검색어가 다르면 정제 변형에서 얻은 결과
        var cleanedQueryUsed = winner?.Metadata.Request is { } used && !Equals(used.Term, request.Term);

        Telemetry.Track(TelemetryEvents.LyricsSearch, new Dictionary<string, object?>
        {
            ["winner"] = winner is null ? "none" : SourceIdOf(winner.Metadata.ServiceName),
            ["perSource"] = perSource,
            ["cached"] = false,
            ["cleanedQueryUsed"] = cleanedQueryUsed,
        });
    }

    /// <summary>제공자 ServiceName → 레지스트리 소스 id. 미등록(사용자 편집 등)은 소문자 이름, 비면 "none".</summary>
    private static string SourceIdOf(string? serviceName) =>
        string.IsNullOrEmpty(serviceName) ? "none"
        : LyricsSourceRegistry.Find(serviceName)?.Id ?? serviceName.ToLowerInvariant();

    /// <summary>translation.linesBucket 버킷팅(contracts/telemetry-events.md).</summary>
    private static string LinesBucket(int lines) =>
        lines <= 10 ? "1-10" : lines <= 50 ? "11-50" : "51+";

    private void Tick()
    {
        var lyrics = CurrentLyrics;
        if (lyrics is null) return;

        var position = _nowPlaying.GetEstimatedPosition();
        if (position is null) return;

        var adjusted = position.Value.TotalSeconds + lyrics.TimeDelay + ManualOffsetSeconds;
        var (current, next) = lyrics.LineIndexesAt(adjusted);
        var index = current ?? -1;

        // 라인 표시 구간: 현재 라인 시작 ~ 다음 라인 시작(없으면 곡 끝/+5초)
        double start = 0, span = 0;
        if (index >= 0)
        {
            start = lyrics.Lines[index].Position;
            var end = next is { } n ? lyrics.Lines[n].Position : lyrics.Length ?? start + 5.0;
            span = end - start;
        }

        if (index != _lastLineIndex)
        {
            _lastLineIndex = index;
            if (index < 0)
            {
                _currentLine = null;
                _currentLineStartedAt = null;
                CurrentLineChanged?.Invoke(null);
            }
            else
            {
                var line = lyrics.Lines[index];
                var display = new DisplayLine(
                    line.Content,
                    ResolveDisplayTranslation(line.Attachments),
                    line.Attachments.GetInlineTimeTags(),
                    span);
                _currentLine = display;
                // 라인이 재생상 시작된 절대 시각(표시측 보간 앵커): 현재 - 라인 내 경과
                _currentLineStartedAt = DateTimeOffset.Now - TimeSpan.FromSeconds(Math.Max(0, adjusted - start));
                CurrentLineChanged?.Invoke(display);
            }
            EmitState();
        }

        if (index >= 0)
            LineProgressChanged?.Invoke(adjusted - start); // 라인 시작 이후 경과(초)
    }

    /// <summary>현재 스냅샷으로 직렬화 표시 상태를 갱신·발행한다.</summary>
    private void EmitState()
    {
        var track = _nowPlaying.CurrentTrack;
        var controls = _nowPlaying.GetControls();
        var line = _currentLine;

        IReadOnlyList<KaraokeMark>? karaoke = null;
        double? karaokeDuration = null;
        if (line?.Karaoke is { } k)
        {
            karaoke = k.Tags.Select(t => new KaraokeMark(t.Index, t.Time)).ToList();
            karaokeDuration = k.Duration;
        }

        CurrentState = new PlaybackViewState(
            _nowPlaying.IsPlaying,
            track?.Title, track?.Artist,
            line?.Content, line?.Translation,
            karaoke, karaokeDuration,
            _currentLineStartedAt,
            line?.LineSpanSeconds ?? 0,
            controls.CanPrevious, controls.CanPlayPause, controls.CanNext);

        StateChanged?.Invoke(CurrentState);
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _timer.Stop();
    }
}
