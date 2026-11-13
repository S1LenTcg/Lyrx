using Musebase.Core;
using Musebase.Core.Search;
using Musebase.Core.Translation;

namespace Musebase.Engine;

/// <summary>
/// 当前要显示的一行歌词（原文 + 译文）。
/// Karaoke：逐字时间标签（有则按字填充）；LineSpanSeconds：行显示区间（秒，按行回退用）。
/// </summary>
public sealed record DisplayLine(
    string? Content,
    string? Translation,
    InlineTimeTags? Karaoke = null,
    double LineSpanSeconds = 0);

/// <summary>
/// 移植自原 AppController 的核心职责：
/// 曲目变更 → 歌词搜索 → 播放位置心跳 → 当前行事件。
/// 与平台无关：播放源为 <see cref="INowPlayingSource"/>，线程封送与定时器通过
/// <see cref="IEngineDispatcher"/> 抽象，由 Windows/Android/macOS/服务端共享。
/// 事件在 IEngineDispatcher 投递的线程（WPF=UI 线程）上触发。
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

    // 最后一次跑完搜索管线的曲目（"标题|歌手"）。同一曲目再次被通知时跳过。
    private string? _searchedTrackKey;

    /// <summary>放弃等待翻译让渡之前的时长（毫秒）。这段时间内没等到就自行翻译。</summary>
    private const int YieldBudgetMs = 8000;
    private int _yieldRetryMs = 3000; // 服务器建议的重新查询间隔（已 clamp 的值）

    /// <summary>
    /// 在没有目标语言译文的情况下上传新曲目后（无翻译器·失败·额度）等待服务器译文的时间（毫秒）。服务器会
    /// 立即翻译上传的曲目（自动翻译），通常几秒内就会挂上。原文已在屏幕上，显示不会延迟。
    /// </summary>
    public int ServerTranslationWaitMs { get; set; } = 10_000;

    /// <summary>上述等待期间向服务器重新询问的间隔（毫秒）。</summary>
    public int ServerTranslationPollMs { get; set; } = 2_500;

    // 遥测触发控制：playback_source 防止同一曲目重复触发，translation 每曲一次
    private string? _lastPlaybackSourceKey;
    private bool _translationReported;

    // 用于序列化显示状态（StateChanged）的当前行快照
    private DisplayLine? _currentLine;
    private DateTimeOffset? _currentLineStartedAt;

    public Lyrics? CurrentLyrics { get; private set; }
    public TrackInfo? CurrentTrack => _nowPlaying.CurrentTrack;

    /// <summary>可序列化的当前显示状态（远程显示·绑定用）。</summary>
    public PlaybackViewState CurrentState { get; private set; } = PlaybackViewState.Empty;

    /// <summary>显示状态变化（行/播放状态）。远程广播·VM 绑定对象。</summary>
    public event Action<PlaybackViewState>? StateChanged;

    /// <summary>手动同步偏移（秒）。为 + 时歌词变快。</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>诊断日志接收器（可选）。由消费方注入 Log.Write 等。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// 匿名遥测接收器（ADR-0004，默认 Noop=不采集）。事件 type/props 的唯一事实来源是
    /// contracts/telemetry-events.md。② 事件（含曲目信息）的同意过滤由实现（平台）负责。
    /// </summary>
    public ITelemetry Telemetry { get; set; } = NoopTelemetry.Instance;

    /// <summary>机器翻译服务（密钥未配置时 IsEnabled=false，不执行任何动作）</summary>
    public LyricsTranslationService? Translation { get; set; }

    /// <summary>曲目级歌词缓存（命中时跳过网络搜索）</summary>
    public LyricsCacheStore? Cache { get; set; }

    /// <summary>
    /// 远程歌词缓存（个人服务器）。为 null 时不使用。在本地缓存未命中与提供者搜索**之间**
    /// 查询，新找到的歌词也会上传到这里。失败与无法连接都会被静默忽略，因此没有服务器时
    /// 行为也不会改变（可变属性——在设置中改地址无需重启即可生效）。
    /// </summary>
    public IRemoteLyricsCache? RemoteCache { get; set; }

    /// <summary>DeepL target_lang（例如 KO）。显示优先级 tr:{lang} → 也用于 tr。</summary>
    public string TargetLanguage { get; set; } = "KO";

    /// <summary>只显示目标语言译文（隐藏提供者的其他语言译文）。目标=中文时除外（提供者译文本身就是中文）。</summary>
    public bool ShowOnlyTargetTranslation { get; set; } = true;

    private string TargetLangLower => TargetLanguage.ToLowerInvariant();

    /// <summary>目标语言是否为中文（zh/zh-hans/zh-hant）——直接使用提供者译文（中文）。</summary>
    private bool TargetIsChinese => TargetLangLower.StartsWith("zh", StringComparison.Ordinal);

    /// <summary>
    /// 决定要显示的译文。
    /// - 目标=中文：优先直接使用提供者译文（中文）（没有则用 tr:{target}）。
    /// - 开启“仅目标语言”：只用 tr:{target}（机器翻译），隐藏提供者的其他语言译文。
    /// - 关闭：tr:{target} → 回退到提供者 tr（原有行为）。
    /// </summary>
    private string? ResolveDisplayTranslation(LineAttachments att)
    {
        if (TargetIsChinese) return att.Translation(null, TargetLangLower);
        if (ShowOnlyTargetTranslation) return att.Translation(TargetLangLower);
        return att.Translation(TargetLangLower, null);
    }

    /// <summary>显示策略变更等导致需要立即重新发布当前行。</summary>
    public void RefreshCurrentLine() => _lastLineIndex = int.MinValue;

    /// <summary>当前行变更（null = 无歌词/未播放）</summary>
    public event Action<DisplayLine?>? CurrentLineChanged;

    /// <summary>当前行开始后经过的时间（秒，每 tick）。用于按字/按行的卡拉 OK 填充。</summary>
    public event Action<double>? LineProgressChanged;

    /// <summary>歌词搜索状态（结构化）。由消费方本地化。</summary>
    public event Action<LyricsStatus>? StatusChanged;

    /// <summary>最后发布过的搜索状态（供延迟订阅者做初始恢复）。与 CurrentLineChanged 对称。</summary>
    public LyricsStatus? CurrentStatus { get; private set; }

    private void RaiseStatus(LyricsStatus status)
    {
        CurrentStatus = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>目标语言译文的显示状态（正常/缓存/超额等）。UI 在源名称旁标注。</summary>
    public TranslationDisplayStatus CurrentTranslationStatus { get; private set; } = TranslationDisplayStatus.None;

    /// <summary>译文显示状态变更通知。</summary>
    public event Action<TranslationDisplayStatus>? TranslationStatusChanged;

    // 本次翻译执行中最后上报的失败（由工厂以回调注入）。用于超额判定。
    private TranslatorFailure? _lastTranslationFailure;

    /// <summary>工厂把翻译器失败回调路由到这里（用于超额等状态判定）。与 App 回调独立。</summary>
    internal void RecordTranslationFailure(TranslatorFailure failure) => _lastTranslationFailure = failure;

    private void SetTranslationStatus(TranslationDisplayStatus status)
    {
        if (CurrentTranslationStatus == status) return;
        CurrentTranslationStatus = status;
        TranslationStatusChanged?.Invoke(status);
    }

    /// <summary>标记为“错误歌词”的曲目键集合（抑制搜索与显示）。由消费方与设置同步。</summary>
    public HashSet<string> SuppressedTrackKeys { get; } = new();

    /// <summary>抑制列表变更通知（用于设置持久化）</summary>
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
            EmitState(); // 把播放状态变化反映到显示状态
        });
    }

    /// <summary>
    /// 在事件订阅与属性（Cache/Translation）接线完成后调用。
    /// 在构造函数中启动时，初始化器属性仍为 null，缓存/翻译会被忽略。
    /// </summary>
    public void Start()
    {
        if (_nowPlaying.CurrentTrack is { } current) OnTrackChanged(current);
        if (_nowPlaying.IsPlaying) _timer.Start();
    }

    private async void OnTrackChanged(TrackInfo? track)
    {
        // 标题、歌手不变就不再搜索。播放源会补上时长、专辑等元数据，
        // 并因此再通知一次曲目变更（TrackInfo 是 record，那些字段也必须相同才算同一个值），
        // 每次都重跑管线的话，**对歌词服务器与提供者会发出两次相同请求**
        // （实测：Android 上间隔 1~8 秒的重复查询）。显示状态仍然照常刷新。
        var trackKey = track is null ? null : LyricsCacheStore.MakeKey(track.Title, track.Artist);
        if (trackKey is not null && trackKey == _searchedTrackKey)
        {
            EmitState(); // 反映时长、专辑等变更过的元数据
            return;
        }
        _searchedTrackKey = trackKey;

        _searchCts?.Cancel();
        CurrentLyrics = null;
        _lastLineIndex = int.MinValue;
        _currentLine = null;
        _currentLineStartedAt = null;
        _translationReported = false; // translation 事件每曲一次
        SetTranslationStatus(TranslationDisplayStatus.None); // 新曲目——尚未翻译
        CurrentLineChanged?.Invoke(null);
        EmitState(); // 反映新曲目（标题），此时还没有行

        if (track is null)
        {
            RaiseStatus(new LyricsStatus(LyricsStatusKind.NoTrack));
            return;
        }

        // playback_source：播放源应用 id（不含曲目信息）。同一曲目的重复通知（SMTC 元数据再次触发等）
        // 会被抑制，每应用每天一次的防抖由客户端（平台 ITelemetry 实现）负责。
        var playbackKey = $"{track.SourceAppId}|{LyricsCacheStore.MakeKey(track.Title, track.Artist)}";
        if (playbackKey != _lastPlaybackSourceKey)
        {
            _lastPlaybackSourceKey = playbackKey;
            Telemetry.Track(TelemetryEvents.PlaybackSource, new Dictionary<string, object?>
            {
                ["appId"] = track.SourceAppId,
            });
        }

        // 标记为“错误歌词”的曲目不搜索、不显示
        if (SuppressedTrackKeys.Contains(LyricsCacheStore.MakeKey(track.Title, track.Artist)))
        {
            RaiseStatus(new LyricsStatus(LyricsStatusKind.HiddenByUser, track.ToString()));
            return;
        }

        // 1) 缓存命中就跳过网络搜索（但目标为中文且旧缓存无译文时（LRCLIB 等），重新搜索以取得译本）
        if (Cache?.Get(track.Title, track.Artist) is { } cached && (!TargetIsChinese || cached.HasTranslation()))
        {
            CurrentLyrics = cached;
            _lastLineIndex = int.MinValue;
            RaiseStatus(new LyricsStatus(LyricsStatusKind.Cache, track.ToString(), cached.Metadata.ServiceName ?? ""));
            // lyrics_search（缓存命中）：没有网络搜索，因此 perSource 为空对象
            Telemetry.Track(TelemetryEvents.LyricsSearch, new Dictionary<string, object?>
            {
                ["winner"] = SourceIdOf(cached.Metadata.ServiceName),
                ["perSource"] = new Dictionary<string, object?>(),
                ["cached"] = true,
                ["cleanedQueryUsed"] = false,
            });
            var cacheCts = new CancellationTokenSource();
            _searchCts = cacheCts;
            // 补充翻译实际填入后回写到缓存与服务器（persistAfter）——避免保存时
            // 没有译文的曲目永远停留在无译文状态。
            await TranslateAsync(cached, cacheCts.Token, persistAfter: true);
            // 补充翻译后仍无目标语言（无翻译器·失败·额度）时，服务器可能其后已补上——
            // 本地缓存不会重新校验，不在这里询问的话，服务器译文永远不会到达这台设备。
            if (CanReceiveServerTranslation && !HasTargetTranslation(cached))
                await TryTakeServerTranslationAsync(track, cached, pollMs: 0, budgetMs: 0, cacheCts.Token);
            return;
        }

        // 1-b) 询问歌词服务器（个人服务器）——直接接收其他设备已找到的歌词与译文。
        //      失败·无法连接·未命中都算未命中，自然流入下面的提供者搜索。
        var yielding = false;
        if (RemoteCache is { } remoteCache)
        {
            var remoteCts = new CancellationTokenSource();
            _searchCts = remoteCts;
            var result = await remoteCache.GetAsync(track.Title, track.Artist, remoteCts.Token);
            if (remoteCts.Token.IsCancellationRequested) return; // 曲目又变了

            // 未命中但“另一台设备刚问过这首曲目”时让出翻译——对方马上会上传译本，
            // 我们只做提供者搜索（原文照常显示），翻译先推后、稍后直接取用。
            yielding = result is { Pending: true, Lyrics: null } && CanShareTranslation;
            if (yielding) _yieldRetryMs = Math.Clamp(result.RetryAfterMs, 1000, 5000);

            if (result.Lyrics is { } remote)
            {
                CurrentLyrics = remote;
                _lastLineIndex = int.MinValue;
                // 提升为本地缓存——此后无需服务器（离线也行）即可立即显示。
                try { Cache?.Set(track.Title, track.Artist, remote); }
                catch (Exception e) { Log?.Invoke($"[server] 本地提升失败: {e.Message}"); }

                RaiseStatus(new LyricsStatus(
                    LyricsStatusKind.Cache, track.ToString(), remote.Metadata.ServiceName ?? ""));
                Telemetry.Track(TelemetryEvents.LyricsSearch, new Dictionary<string, object?>
                {
                    ["winner"] = SourceIdOf(remote.Metadata.ServiceName),
                    ["perSource"] = new Dictionary<string, object?>(),
                    ["cached"] = true,
                    ["cleanedQueryUsed"] = false,
                });
                // 服务器保存的版本没有目标语言译文时在此补齐，并把结果回写到服务器。
                // 没有这一步，每台设备都会各自翻译同一首曲目（共享的关键路径）。
                await TranslateAsync(remote, remoteCts.Token, persistAfter: true);
                return;
            }

            // 服务器已把该标题标记为广告就在这里结束。提供者搜索总是空转，
            // 偶尔碰巧匹配上还会在广告上显示风马牛不相及的歌词。这里由人工标注来补上
            // 自动判定（AdSignals）漏掉的路径——播放元数据中没有广告标志的情况。
            if (result.IsAd)
            {
                Log?.Invoke($"[ad] 服务器标记为广告的标题——不搜索: {track.Title}");
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

            // 先显示首个结果，再用更好的候选替换（把延迟感降到最低）
            await foreach (var lyrics in _search.SearchAsync(request, cts.Token, diagnostics))
            {
                if (cts.Token.IsCancellationRequested) return;
                if (CurrentLyrics is null || lyrics.Quality() > CurrentLyrics.Quality())
                {
                    CurrentLyrics = lyrics;
                    _lastLineIndex = int.MinValue; // 强制重新计算行
                    RaiseStatus(new LyricsStatus(
                        LyricsStatusKind.Found, track.ToString(), lyrics.Metadata.ServiceName ?? "", lyrics.Quality()));
                    // 让出期间不对每个候选都翻译——循环结束后只对最终采用的版本
                    // 翻译一次，而且先等服务器。
                    if (!yielding) await TranslateAsync(lyrics, cts.Token);
                }
            }

            // 一次搜索完成——若曲目已被替换（取消）则不触发
            if (!cts.Token.IsCancellationRequested)
                TrackLyricsSearch(request, diagnostics);

            // 让出：稍微给对方设备上传译本的时间。原文已经在屏幕上，
            // 在这里等待也不会让显示变慢（译文本来就是后补的）。
            var tookShared = false;
            if (yielding && CurrentLyrics is { } adopted && !cts.Token.IsCancellationRequested)
            {
                tookShared = await TryTakeServerTranslationAsync(track, adopted, _yieldRetryMs, YieldBudgetMs, cts.Token);
                if (!tookShared)
                {
                    Log?.Invoke($"[server] 让出时间超时——自行翻译: {track}");
                    await TranslateAsync(adopted, cts.Token);
                }
            }

            if (CurrentLyrics is null)
            {
                RaiseStatus(new LyricsStatus(LyricsStatusKind.NotFound, track.ToString()));
                if (!cts.Token.IsCancellationRequested)
                {
                    // ② 质量报告——包含曲目信息。同意过滤由 ITelemetry 实现负责。
                    Telemetry.Track(TelemetryEvents.LyricsNotFound, new Dictionary<string, object?>
                    {
                        ["title"] = track.Title,
                        ["artist"] = track.Artist,
                    });
                }
            }
            else if (!cts.Token.IsCancellationRequested)
            {
                // 2) 把最终选定版本（含译文）保存到缓存
                Task? upload = null;
                try
                {
                    Cache?.Set(track.Title, track.Artist, CurrentLyrics);
                    Log?.Invoke($"[cache] 保存: {track}");
                    // 同时上传到歌词服务器，避免其他设备重新搜索、重新翻译（失败忽略）。
                    // 刚刚从服务器接收并使用的（让出）不再回传——否则同样内容
                    // 只会让 revision 空转上升。
                    if (!tookShared) upload = RemoteCache?.SetAsync(track.Title, track.Artist, CurrentLyrics);
                }
                catch (Exception e)
                {
                    Log?.Invoke($"[cache] 保存失败: {e.Message}");
                }

                // 3) 在无目标语言的情况下上传后（无翻译器·失败·额度），稍等服务器完成翻译。
                if (upload is not null && CanReceiveServerTranslation &&
                    CurrentLyrics is { } uploaded && !HasTargetTranslation(uploaded))
                {
                    try { await upload; } catch { /* 上传失败由实现吞掉——等下去也没有用 */ }
                    if (!await TryTakeServerTranslationAsync(
                            track, uploaded, ServerTranslationPollMs, ServerTranslationWaitMs, cts.Token))
                        Log?.Invoke($"[server] 服务器还没有译文——下次播放时再取: {track}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 已切换到下一曲目
        }
        catch (Exception e)
        {
            Log?.Invoke($"[search] 异常: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 把当前曲目标记为“错误歌词”：从缓存中移除、清除显示，
    /// 并在曲目持续期间（以及恢复播放时）抑制重新搜索与显示。
    /// </summary>
    public void MarkWrongLyrics()
    {
        if (CurrentTrack is not { } track) return;

        // ② 质量报告——在删除前先取得曾被采用的源 id。同意过滤由 ITelemetry 实现负责。
        Telemetry.Track(TelemetryEvents.WrongLyrics, new Dictionary<string, object?>
        {
            ["title"] = track.Title,
            ["artist"] = track.Artist,
            ["source"] = SourceIdOf(CurrentLyrics?.Metadata.ServiceName),
        });

        SuppressedTrackKeys.Add(LyricsCacheStore.MakeKey(track.Title, track.Artist));
        try { Cache?.Remove(track.Title, track.Artist); }
        catch (Exception e) { Log?.Invoke($"[wrong] 缓存移除失败: {e.Message}"); }

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

    /// <summary>解除当前曲目的本地缓存与抑制，并重新从在线歌词源搜索。</summary>
    public void ReloadCurrentTrackLyrics()
    {
        if (CurrentTrack is not { } track) return;
        Unsuppress(track);
        try { Cache?.Remove(track.Title, track.Artist); } catch { }
        _searchedTrackKey = null;
        OnTrackChanged(track);
    }

    /// <summary>应用外部（如手动搜索）选定的歌词并刷新缓存。</summary>
    public async Task UseLyricsAsync(Lyrics lyrics)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        Unsuppress(CurrentTrack); // 用户亲自选择的歌词，解除抑制

        CurrentLyrics = lyrics;
        _lastLineIndex = int.MinValue;
        RaiseStatus(new LyricsStatus(
            LyricsStatusKind.Manual, CurrentTrack?.ToString() ?? "", lyrics.Metadata.ServiceName ?? ""));
        await TranslateAsync(lyrics, cts.Token);
        if (CurrentTrack is { } track && !cts.Token.IsCancellationRequested)
        {
            Cache?.Set(track.Title, track.Artist, lyrics);
            _ = RemoteCache?.SetAsync(track.Title, track.Artist, lyrics); // 手动选定的版本也共享
        }
    }

    /// <summary>
    /// 把编辑后的歌词存入缓存（来源=用户编辑），若仍是同一曲目则立即生效。
    /// 不运行机器翻译（MT），以免覆盖用户修正过的译文。
    /// </summary>
    public void SaveEditedLyrics(TrackInfo track, Lyrics lyrics)
    {
        Unsuppress(track); // 编辑并保存过的曲目，解除抑制
        lyrics.Metadata.ServiceName = "사용자 편집";
        try
        {
            Cache?.Set(track.Title, track.Artist, lyrics);
            Log?.Invoke($"[edit] 保存: {track}");
            // 编辑版本在服务器上以 origin=user 保护，其他设备的自动搜索无法覆盖。
            _ = RemoteCache?.SetAsync(track.Title, track.Artist, lyrics);
        }
        catch (Exception e)
        {
            Log?.Invoke($"[edit] 保存失败: {e.Message}");
        }

        if (CurrentTrack is { } cur && cur.Title == track.Title && cur.Artist == track.Artist)
        {
            _searchCts?.Cancel(); // 避免进行中的搜索覆盖编辑版本
            CurrentLyrics = lyrics;
            _lastLineIndex = int.MinValue; // 重新发布当前行
            RaiseStatus(new LyricsStatus(LyricsStatusKind.Edited, track.ToString()));
        }
    }

    /// <summary>
    /// 重新执行当前歌词的翻译——在改动翻译配置（引擎/密钥/API 使用开关）后立即调用，
    /// 无需等待下一曲即可对当前曲目生效。没有歌词时不做任何事。
    /// </summary>
    public async Task RetranslateCurrentAsync()
    {
        if (CurrentLyrics is not { } lyrics) return;
        _searchCts?.Cancel(); // 避免与进行中的搜索/翻译重叠
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
        catch (OperationCanceledException) { /* 曲目切换等——忽略 */ }
    }

    /// <summary>
    /// 确保目标语言 MT 后刷新当前行（缓存命中则立即，未命中则调用 API 一次）。
    /// 当 <paramref name="persistAfter"/> 为 true 且译文确实被填入时，回写到本地缓存与歌词服务器
    /// ——在调用方随后会立即保存的路径（提供者搜索·手动选择）中置为 false，
    /// 以免同一内容上传两次。
    /// </summary>
    private async Task TranslateAsync(Lyrics lyrics, CancellationToken ct, bool persistAfter = false)
    {
        // 目标=中文且已含提供者译文（中文）时不走机器翻译。
        if (TargetIsChinese && lyrics.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Attachments[LineAttachments.TagTranslationPrefix])))
        {
            CurrentTranslationStatus = TranslationDisplayStatus.Cache;
            TranslationStatusChanged?.Invoke(CurrentTranslationStatus);
            return;
        }
        if (Translation is not { IsEnabled: true } service) { SetTranslationStatus(TranslationDisplayStatus.None); return; }
        _lastTranslationFailure = null;
        // 关闭 API 翻译时不闪烁“翻译中”，而是从一开始就显示为关闭。
        SetTranslationStatus(service.CacheOnly
            ? TranslationDisplayStatus.Disabled : TranslationDisplayStatus.Translating);
        try
        {
            var stats = new TranslationRunStats();
            var changed = await service.EnsureTranslatedAsync(lyrics, TargetLanguage, ct, stats);
            if (changed > 0 && ReferenceEquals(CurrentLyrics, lyrics))
            {
                _lastLineIndex = int.MinValue; // 为反映译文重新发布当前行
                TranslationStatusChanged?.Invoke(CurrentTranslationStatus);
            }

            // 译文显示状态判定：需要 API 填充却因失败留下未填充的行时判为失败（超额等），
            // 本次由 API 填充时判为正常（Live），其余（全部来自缓存/早已翻译）为缓存。
            var apiFilled = changed - stats.CacheHits;          // 本次由 API 填充的行数
            var apiNeeded = stats.LinesNeeded - stats.CacheHits; // 需要由 API 填充的行数
            // 用户关闭——只用缓存填充。若已全部由缓存覆盖（=不再有需要 API 的行）
            // 译文其实处于正常显示状态，因此要与“关闭”区分，避免造成误解。
            if (service.CacheOnly)
                SetTranslationStatus(apiNeeded > 0
                    ? TranslationDisplayStatus.Disabled : TranslationDisplayStatus.DisabledCached);
            else if (_lastTranslationFailure is { } f && apiFilled < apiNeeded)
                SetTranslationStatus(f.Kind is TranslatorFailureKind.Quota or TranslatorFailureKind.RateLimit
                    ? TranslationDisplayStatus.Quota : TranslationDisplayStatus.Failed);
            else if (apiFilled > 0)
                SetTranslationStatus(TranslationDisplayStatus.Live);
            else
                SetTranslationStatus(TranslationDisplayStatus.Cache); // 无需翻译（早已翻译）或全部来自缓存

            // 把补充翻译部分反映到缓存与服务器。changed == 0 表示早已全部翻译，
            // 因此不做任何事（每次重放同一曲目都不要上传）。
            if (persistAfter && changed > 0 && !ct.IsCancellationRequested)
                PersistTranslated(lyrics);

            // translation：在确实需要翻译的首次完成时点，每曲一次
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
            // 曲目已切换
        }
    }

    /// <summary>
    /// 是否处于能与人共享翻译的状态——只有翻译器开启且目标不是中文时，
    /// “让出”才有意义（中文直接使用提供者译文，根本不经过 API）。
    /// </summary>
    private bool CanShareTranslation =>
        !TargetIsChinese && Translation is { IsEnabled: true, CacheOnly: false };

    /// <summary>
    /// 能否接收并使用服务器译文。判断依据不是有无翻译器，而是<b>自己翻译过一次之后目标语言
    /// 是否仍为空</b>（调用方用 <see cref="HasTargetTranslation"/> 确认）——即使翻译器已开启，
    /// 因额度、失败而留空的情形也很常见（MyMemory 无密钥时每日额度），确实出现过那样的设备
    /// 始终拿不到服务器上已有译文的问题。翻译成功的设备不会发出额外请求。
    /// </summary>
    private bool CanReceiveServerTranslation => RemoteCache is not null && !TargetIsChinese;

    private bool HasTargetTranslation(Lyrics lyrics) =>
        lyrics.Lines.Any(l => l.Attachments.Translation(TargetLangLower) is not null);

    /// <summary>
    /// 服务器上出现目标语言译本时就取来使用。取到了返回 true（不再自行翻译）。
    /// 等待其他设备上传译文的让出、等待服务器自动翻译、缓存命中后的再次确认，
    /// 全都是这件事——每 <paramref name="pollMs"/> 再问一次，超过 <paramref name="budgetMs"/> 就停止
    /// （0/0 则立即只问一次）。
    ///
    /// 原文歌词已在屏幕上，因此这段等待不会拖慢显示——译文在几秒后
    /// 补上本来就是原有行为。
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
            _lastLineIndex = int.MinValue; // 带着已补上的译文重新发布
            try { Cache?.Set(track.Title, track.Artist, shared); }
            catch (Exception e) { Log?.Invoke($"[server] 服务器译文缓存保存失败: {e.Message}"); }
            SetTranslationStatus(TranslationDisplayStatus.Cache);
            Log?.Invoke($"[server] 已取用服务器译文——{track}");
            return true;
        }
        while (DateTimeOffset.UtcNow < deadline);

        return false;
    }

    /// <summary>
    /// 把已补齐补充翻译的歌词重新存入本地缓存，并上传到歌词服务器。
    /// 仅在仍在显示同一曲目时执行（曲目已变则可能被存到别的曲目键下）。
    /// 服务器合并策略总是采纳译文语言增多的更新，因此覆盖是安全的。
    /// </summary>
    private void PersistTranslated(Lyrics lyrics)
    {
        if (!ReferenceEquals(CurrentLyrics, lyrics)) return;
        if (CurrentTrack is not { } track) return;

        try
        {
            Cache?.Set(track.Title, track.Artist, lyrics);
            _ = RemoteCache?.SetAsync(track.Title, track.Artist, lyrics);
            Log?.Invoke($"[translate] 已应用补充翻译: {track}");
        }
        catch (Exception e)
        {
            Log?.Invoke($"[translate] 补充翻译保存失败: {e.Message}");
        }
    }

    /// <summary>lyrics_search 触发（契约 ①——不含曲目信息）：各源命中/延迟 + 采用的源 + 是否使用净化后的搜索词。</summary>
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

        // Metadata.Request 是实际使用的请求——与原始搜索词不同即表示结果来自净化变体
        var cleanedQueryUsed = winner?.Metadata.Request is { } used && !Equals(used.Term, request.Term);

        Telemetry.Track(TelemetryEvents.LyricsSearch, new Dictionary<string, object?>
        {
            ["winner"] = winner is null ? "none" : SourceIdOf(winner.Metadata.ServiceName),
            ["perSource"] = perSource,
            ["cached"] = false,
            ["cleanedQueryUsed"] = cleanedQueryUsed,
        });
    }

    /// <summary>提供者 ServiceName → 注册表源 id。未注册（如用户编辑）用其小写名称，为空时用 "none"。</summary>
    private static string SourceIdOf(string? serviceName) =>
        string.IsNullOrEmpty(serviceName) ? "none"
        : LyricsSourceRegistry.Find(serviceName)?.Id ?? serviceName.ToLowerInvariant();

    /// <summary>translation.linesBucket 分桶（contracts/telemetry-events.md）。</summary>
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

        // 行显示区间：当前行开始 ~ 下一行开始（没有则曲末/+5 秒）
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
                // 该行在播放上开始的绝对时刻（显示侧插值锚点）：当前 - 行内已过时间
                _currentLineStartedAt = DateTimeOffset.Now - TimeSpan.FromSeconds(Math.Max(0, adjusted - start));
                CurrentLineChanged?.Invoke(display);
            }
            EmitState();
        }

        if (index >= 0)
            LineProgressChanged?.Invoke(adjusted - start); // 该行开始后经过的时间（秒）
    }

    /// <summary>用当前快照更新并发布序列化显示状态。</summary>
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
