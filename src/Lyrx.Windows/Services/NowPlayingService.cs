using System.Threading;
using Windows.Media;
using Windows.Media.Control;
using Musebase.Engine;

using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace Musebase.Windows.Services;

internal readonly record struct TransportState(
    PlaybackControls Controls, bool IsPlaying, bool CanSeek, bool? Shuffle, MediaPlaybackAutoRepeatMode? Repeat);

/// <summary>
/// SMTC(GlobalSystemMediaTransportControls) 래퍼 — <see cref="INowPlayingSource"/>의 Windows 구현.
/// 트랙/재생상태 변경 이벤트와 보간된 재생 위치를 제공한다.
/// 이벤트는 임의 스레드에서 발생하므로 구독자가 UI 마샬링을 책임진다.
/// (TrackInfo/PlaybackControls 계약은 Musebase.Engine에 있다)
/// </summary>
public sealed class NowPlayingService : INowPlayingSource, IDisposable
{
    private SessionManager? _manager;
    private Session? _session;
    private readonly object _lock = new();
    private Timer? _pollTimer;

    // 재생 소스 선택: "auto" = 자동 감지, 그 외 = 특정 SourceAppUserModelId로 고정.
    private string _sourceMode = "auto";
    // 자동 모드에서 브라우저(Firefox/Chrome 등)를 음악 소스로 포함할지. 기본 제외.
    private bool _includeBrowsers;

    /// <summary>SourceAppUserModelId에 포함되면 브라우저로 간주할 토큰(소문자).</summary>
    private static readonly string[] BrowserTokens =
    {
        "firefox", "mozilla", "chrome", "chromium", "msedge", "microsoftedge",
        "opera", "brave", "vivaldi", "iexplore", "internetexplorer", "waterfox", "librewolf",
    };

    /// <summary>
    /// 영상·팟캐스트로 간주해 자동 모드에서 제외할 토큰(소문자). 브라우저와 같은 이유 —
    /// 영상·팟캐스트에 엉뚱한 곡의 가사가 뜨는 것을 막는다(Android의 VideoAppTokens와 대칭).
    /// </summary>
    private static readonly string[] NonMusicTokens =
    {
        "youtube", "netflix", "disneyplus", "primevideo", "twitch", "vlc", "potplayer",
        "kmplayer", "gomplayer", "mpc-hc", "kodi", "plex", "wavve", "tving", "coupangplay",
        "podcast", "pocketcasts", "overcast", "castbox", "audible", "spreaker", "stitcher",
    };

    /// <summary>
    /// 선호 음악 앱(SourceAppUserModelId). 하나라도 지정되면 자동 모드에서 이 앱들만 후보가 된다.
    /// 비어 있으면(기본) 종전대로 브라우저·영상 앱 제외 규칙만 적용한다.
    /// </summary>
    private string[] _preferredSources = Array.Empty<string>();

    /// <summary>자동 소스 감지 모드 여부.</summary>
    private bool IsAuto => string.Equals(_sourceMode, "auto", StringComparison.OrdinalIgnoreCase);

    private static bool IsBrowserSource(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        foreach (var token in BrowserTokens)
            if (appId.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>영상·팟캐스트 앱인지(YouTube Music은 음악이므로 예외).</summary>
    private static bool IsNonMusicSource(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        if (appId!.Contains("youtubemusic", StringComparison.OrdinalIgnoreCase)
            || appId.Contains("youtube music", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var token in NonMusicTokens)
            if (appId.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // 위치 떨림 완화용 상태 (같은 곡 재생 중 작은 역행 흡수)
    private TimeSpan _smoothedPosition = TimeSpan.MinValue;
    private string? _smoothedTrackKey;
    private static readonly TimeSpan BackwardTolerance = TimeSpan.FromSeconds(1.0);

    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying { get; private set; }

    public event Action<TrackInfo?>? TrackChanged;
    public event Action<bool>? IsPlayingChanged;

    public static async Task<NowPlayingService> CreateAsync()
    {
        var service = new NowPlayingService();
        service._manager = await SessionManager.RequestAsync();
        service._manager.CurrentSessionChanged += (_, _) => service.SelectBestSession();
        service._manager.SessionsChanged += (_, _) => { service.LogSessions(); service.SelectBestSession(); };
        service.LogSessions();
        service.SelectBestSession();

        // SMTC의 PlaybackInfoChanged 이벤트가 앱(예: Spotify)에 따라 지연되어
        // 정지 후에도 가사가 남는 문제 방지 — 재생 상태를 주기적으로 폴링해 즉시 반영.
        // 아울러 매 폴링마다 최적 세션을 재선택한다: Windows 10에서 Spotify가
        // "현재 세션"으로 잡히지 않아(다른 앱이 current) 인식이 안 되던 문제를 해결.
        service._pollTimer = new Timer(
            _ => { service.SelectBestSession(); service.RefreshPlayback(); }, null,
            TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        return service;
    }

    /// <summary>
    /// 부착할 SMTC 세션을 재선택한다. GetCurrentSession()만 믿지 않고 전체 세션을
    /// 열거해 '재생 중'인 세션을 우선한다(Win10에서 Spotify가 current로 안 잡히는 문제 대응).
    /// 선택이 바뀔 때만 재구독한다.
    /// </summary>
    private void SelectBestSession()
    {
        bool changed;
        string? pickedId;
        lock (_lock)
        {
            var manager = _manager;
            if (manager is null) return;

            var best = PickBestSession(manager);
            if (SameSession(best, _session)) return;

            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            _session = best;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }
            changed = true;
            pickedId = best?.SourceAppUserModelId;
        }

        if (changed)
        {
            Musebase.Windows.Log.Write($"[smtc] 선택: {pickedId ?? "(없음)"} (모드={_sourceMode}, 브라우저포함={_includeBrowsers})");
            _ = RefreshTrackAsync();
            RefreshPlayback();
        }
    }

    /// <summary>
    /// 부착할 세션을 결정한다.
    /// - 특정 소스로 고정된 경우: 그 SourceAppUserModelId 세션만 사용(없으면 표시 안 함).
    /// - 자동: current가 재생 중이면 그대로, 아니면 재생 중인 세션, 그래도 없으면 current/첫 세션.
    ///   단, 브라우저 세션은 (옵션이 꺼져 있으면) 후보에서 제외한다 —
    ///   Firefox의 YouTube 영상 등을 음악으로 오인식하지 않도록.
    /// </summary>
    private Session? PickBestSession(SessionManager manager)
    {
        IReadOnlyList<Session> sessions;
        try { sessions = manager.GetSessions(); }
        catch { return null; }

        // 특정 플레이어로 고정
        if (!IsAuto)
        {
            foreach (var s in sessions)
                if (string.Equals(s.SourceAppUserModelId, _sourceMode, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null; // 지정 플레이어가 실행 중이 아니면 표시하지 않음
        }

        // 선호 앱을 지정했으면 그 앱들만, 아니면 브라우저·영상/팟캐스트 앱 제외.
        bool Eligible(Session s)
        {
            var id = s.SourceAppUserModelId;
            if (_preferredSources.Length > 0)
            {
                foreach (var preferred in _preferredSources)
                    if (string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            if (!_includeBrowsers && IsBrowserSource(id)) return false;
            return _includeBrowsers || !IsNonMusicSource(id);
        }

        Session? current = null;
        try { current = manager.GetCurrentSession(); } catch { /* 레이스 */ }
        if (current is not null && Eligible(current) && IsSessionPlaying(current)) return current;

        Session? firstEligible = null;
        foreach (var s in sessions)
        {
            if (!Eligible(s)) continue;
            firstEligible ??= s;
            if (IsSessionPlaying(s)) return s;
        }

        if (current is not null && Eligible(current)) return current;
        return firstEligible;
    }

    /// <summary>SMTC 세션 목록을 로그에 남긴다(Win10 Spotify 미인식 등 진단용).</summary>
    private void LogSessions()
    {
        var manager = _manager;
        if (manager is null) return;
        try
        {
            var items = manager.GetSessions()
                .Select(s => $"{s.SourceAppUserModelId}[{SafeStatus(s)}]");
            Musebase.Windows.Log.Write($"[smtc] 세션 목록: {string.Join(", ", items)}");
        }
        catch (Exception e)
        {
            Musebase.Windows.Log.Write($"[smtc] 세션 목록 조회 실패: {e.Message}");
        }
    }

    private static string SafeStatus(Session s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus.ToString(); }
        catch { return "?"; }
    }

    // ---- 소스 선택 / 재생 컨트롤 (외부 API) ----

    /// <summary>현재 소스 모드("auto" 또는 특정 SourceAppUserModelId).</summary>
    public string SourceMode { get { lock (_lock) return _sourceMode; } }

    /// <summary>자동 모드에서 브라우저를 포함하는지.</summary>
    public bool IncludeBrowsers { get { lock (_lock) return _includeBrowsers; } }

    /// <summary>재생 소스 선택을 변경하고 즉시 세션을 재선택한다.</summary>
    public void SetSource(string? mode, bool includeBrowsers, IEnumerable<string>? preferredSources = null)
    {
        lock (_lock)
        {
            _sourceMode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode;
            _includeBrowsers = includeBrowsers;
            _preferredSources = preferredSources?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToArray() ?? Array.Empty<string>();
        }
        SelectBestSession();
        RefreshPlayback();
    }

    /// <summary>현재 열려 있는 SMTC 세션들의 SourceAppUserModelId 목록(소스 선택 UI용).</summary>
    public IReadOnlyList<string> GetAvailableSources()
    {
        var manager = _manager;
        if (manager is null) return Array.Empty<string>();
        try
        {
            return manager.GetSessions()
                .Select(s => s.SourceAppUserModelId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>지정 소스가 브라우저인지(자동 모드 판단용 UI 힌트).</summary>
    public static bool IsBrowser(string? appId) => IsBrowserSource(appId);

    /// <summary>현재 세션의 컨트롤 가용 여부.</summary>
    public PlaybackControls GetControls() => GetTransportState().Controls;

    internal TransportState GetTransportState()
    {
        Session? session;
        lock (_lock) session = _session;
        try
        {
            if (session is not null)
            {
                var info = session.GetPlaybackInfo();
                var c = info.Controls;
                var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var controls = new PlaybackControls(c.IsPreviousEnabled,
                    c.IsPlayPauseToggleEnabled || (playing ? c.IsPauseEnabled : c.IsPlayEnabled), c.IsNextEnabled);
                return new TransportState(controls, playing, c.IsPlaybackPositionEnabled,
                    c.IsShuffleEnabled ? info.IsShuffleActive : null,
                    c.IsRepeatEnabled ? info.AutoRepeatMode : null);
            }
        }
        catch { /* Session went away. */ }
        return new TransportState(PlaybackControls.None, false, false, null, null);
    }

    /// <summary>재생/일시정지 토글.</summary>
    public async Task<bool> TogglePlayPauseAsync()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return false;
        try
        {
            var info = session.GetPlaybackInfo();
            var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing && info.Controls.IsPauseEnabled) return await session.TryPauseAsync();
            if (!playing && info.Controls.IsPlayEnabled) return await session.TryPlayAsync();
            return info.Controls.IsPlayPauseToggleEnabled && await session.TryTogglePlayPauseAsync();
        }
        catch { return false; }
    }

    internal async Task<bool> ToggleShuffleAsync()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return false;
        try
        {
            var info = session.GetPlaybackInfo();
            return info.Controls.IsShuffleEnabled && info.IsShuffleActive is bool enabled
                && await session.TryChangeShuffleActiveAsync(!enabled);
        }
        catch { return false; }
    }

    internal async Task<bool> ToggleRepeatOneAsync()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return false;
        try
        {
            var info = session.GetPlaybackInfo();
            if (!info.Controls.IsRepeatEnabled || info.AutoRepeatMode is not { } mode) return false;
            var next = mode == MediaPlaybackAutoRepeatMode.Track
                ? MediaPlaybackAutoRepeatMode.None : MediaPlaybackAutoRepeatMode.Track;
            return await session.TryChangeAutoRepeatModeAsync(next);
        }
        catch { return false; }
    }

    /// <summary>다음 곡.</summary>
    public async Task<bool> SkipNextAsync()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return false;
        try { return await session.TrySkipNextAsync(); }
        catch { return false; }
    }

    /// <summary>이전 곡.</summary>
    public async Task<bool> SkipPreviousAsync()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return false;
        try { return await session.TrySkipPreviousAsync(); }
        catch { return false; }
    }

    public bool CanSeek
    {
        get
        {
            Session? session;
            lock (_lock) session = _session;
            try { return session?.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled == true; }
            catch { return false; }
        }
    }

    public async Task<bool> SeekAsync(TrackInfo track, TimeSpan position)
    {
        Session? session;
        lock (_lock)
        {
            if (!Equals(track, CurrentTrack)) return false;
            session = _session;
        }
        if (session is null) return false;
        try
        {
            if (!string.Equals(session.SourceAppUserModelId, track.SourceAppId, StringComparison.OrdinalIgnoreCase)
                || !session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled) return false;
            var accepted = await session.TryChangePlaybackPositionAsync(position.Ticks);
            if (accepted)
            {
                lock (_lock)
                {
                    if (ReferenceEquals(session, _session)) _smoothedPosition = TimeSpan.MinValue;
                }
            }
            return accepted;
        }
        catch { return false; }
    }

    private static bool IsSessionPlaying(Session session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameSession(Session? a, Session? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.SourceAppUserModelId, b.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args) =>
        _ = RefreshTrackAsync();

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args) =>
        RefreshPlayback();

    /// <summary>
    /// 지금 재생 중인 곡의 앨범 표지를 <b>재생 앱이 준 그대로</b> 가져온다.
    ///
    /// Spotify·Apple Music 등은 SMTC에 표지를 함께 실어 보낸다. 외부 검색(iTunes·Deezer)보다
    /// 이게 나은 이유는 분명하다 — 네트워크도 키도 필요 없고, <b>검색으로 다른 앨범을 집을 일이
    /// 없다</b>(리마스터·싱글 버전 구분, 한국 인디처럼 iTunes에 아예 없는 곡까지 맞는다).
    /// 표지를 안 주는 앱도 있으니 못 받으면 null이고, 그때 서버 쪽 커버로 물러난다.
    /// </summary>
    public async Task<byte[]?> GetThumbnailAsync()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return null;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props.Thumbnail is not { } reference) return null;

            using var stream = await reference.OpenReadAsync();
            if (stream.Size is 0 or > 8 * 1024 * 1024) return null; // 비정상 크기는 건너뛴다

            var bytes = new byte[stream.Size];
            using var reader = new global::Windows.Storage.Streams.DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception)
        {
            return null; // 세션이 사라지는 레이스 등 — 표지 없음으로 처리
        }
    }

    private async Task RefreshTrackAsync()
    {
        Session? session;
        lock (_lock) session = _session;

        TrackInfo? track = null;
        if (session is not null)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var timeline = session.GetTimelineProperties();
                var duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : (TimeSpan?)null;
                var artist = props.Artist ?? "";
                var album = props.AlbumTitle ?? "";

                // 광고 구간은 곡이 아니다 — 트랙을 만들지 않아 가사 검색·서버 조회가 아예 돌지 않는다.
                // SMTC에는 광고 플래그·mediaId가 없으므로 실측 검증된 신호 ③(아티스트=Spotify +
                // 앨범 비어 있음)만 본다. 판정 규칙은 Android와 같은 코드(Engine의 AdSignals)다.
                if (!string.IsNullOrEmpty(props.Title) && !AdSignals.LooksLikeAd(artist, album))
                    track = new TrackInfo(props.Title, artist, album, duration, session.SourceAppUserModelId);
            }
            catch
            {
                // 세션이 사라지는 타이밍 레이스 — 트랙 없음으로 처리
            }
        }

        if (!Equals(track, CurrentTrack))
        {
            CurrentTrack = track;
            TrackChanged?.Invoke(track);
        }
    }

    private void RefreshPlayback()
    {
        Session? session;
        lock (_lock) session = _session;

        var playing = false;
        try
        {
            playing = session?.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            // 세션 소멸 레이스
        }

        if (playing != IsPlaying)
        {
            IsPlaying = playing;
            IsPlayingChanged?.Invoke(playing);
        }
    }

    /// <summary>
    /// 보간된 현재 재생 위치. SMTC 타임라인은 앱별로 갱신이 드물어
    /// LastUpdatedTime 이후 경과분을 더한다 (Spike.Smtc에서 검증).
    /// 새 타임라인 갱신 때 값이 뒤로 튀는 떨림(특히 Apple Music)을 막기 위해
    /// 같은 곡 재생 중 1초 미만의 역행은 흡수한다(큰 변화=시킹은 그대로 반영).
    /// </summary>
    public TimeSpan? GetEstimatedPosition()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return null;

        TimeSpan position;
        bool playing = IsPlaying;
        try
        {
            var timeline = session.GetTimelineProperties();
            position = timeline.Position;
            if (playing)
            {
                var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(30))
                    position += elapsed;
            }
        }
        catch
        {
            return null;
        }

        var trackKey = CurrentTrack is { } t ? $"{t.Title}|{t.Artist}" : null;
        if (playing && trackKey == _smoothedTrackKey && _smoothedPosition != TimeSpan.MinValue)
        {
            var delta = position - _smoothedPosition;
            if (delta < TimeSpan.Zero && delta > -BackwardTolerance)
                position = _smoothedPosition; // 작은 역행은 유지
        }
        _smoothedPosition = position;
        _smoothedTrackKey = trackKey;
        return position;
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        lock (_lock)
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session = null;
            }
        }
    }
}
