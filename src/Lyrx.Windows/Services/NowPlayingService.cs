using System.Diagnostics;
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
/// SMTC（GlobalSystemMediaTransportControls）包装器 —— <see cref="INowPlayingSource"/> 的 Windows 实现。
/// 提供曲目/播放状态变更事件，以及插值后的播放位置。
/// 事件会在任意线程上触发，因此订阅方负责做 UI 线程封送。
/// （TrackInfo/PlaybackControls 契约位于 Musebase.Engine）
/// </summary>
public sealed class NowPlayingService : INowPlayingSource, IDisposable
{
    private SessionManager? _manager;
    private Session? _session;
    private readonly object _lock = new();
    private Timer? _pollTimer;

    // 播放源选择："auto" = 自动检测，其他值 = 固定到指定的 SourceAppUserModelId。
    private string _sourceMode = "auto";
    // 自动模式下是否把浏览器（Firefox/Chrome 等）也算作音乐源。默认排除。
    private bool _includeBrowsers;

    /// <summary>SourceAppUserModelId 中出现该词就视为浏览器（小写）。</summary>
    private static readonly string[] BrowserTokens =
    {
        "firefox", "mozilla", "chrome", "chromium", "msedge", "microsoftedge",
        "opera", "brave", "vivaldi", "iexplore", "internetexplorer", "waterfox", "librewolf",
    };

    /// <summary>
    /// 视为视频·播客而在自动模式下排除的词（小写）。理由与浏览器相同 ——
    /// 避免视频、播客上冒出无关歌曲的歌词（与 Android 的 VideoAppTokens 对称）。
    /// </summary>
    private static readonly string[] NonMusicTokens =
    {
        "youtube", "netflix", "disneyplus", "primevideo", "twitch", "vlc", "potplayer",
        "kmplayer", "gomplayer", "mpc-hc", "kodi", "plex", "wavve", "tving", "coupangplay",
        "podcast", "pocketcasts", "overcast", "castbox", "audible", "spreaker", "stitcher",
    };

    /// <summary>
    /// 偏好的音乐应用（SourceAppUserModelId）。只要指定了任意一项，自动模式下就只有这些应用成为候选。
    /// 为空（默认）时，仍按原有规则只排除浏览器与视频应用。
    /// </summary>
    private string[] _preferredSources = Array.Empty<string>();

    /// <summary>当前是否处于自动源检测模式。</summary>
    private bool IsAuto => string.Equals(_sourceMode, "auto", StringComparison.OrdinalIgnoreCase);

    private static bool IsBrowserSource(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        foreach (var token in BrowserTokens)
            if (appId.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>是否为视频·播客应用（YouTube Music 属于音乐，例外）。</summary>
    private static bool IsNonMusicSource(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        if (appId!.Contains("youtubemusic", StringComparison.OrdinalIgnoreCase)
            || appId.Contains("youtube music", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var token in NonMusicTokens)
            if (appId.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---- 播放位置外推 ----
    //
    // 实测：Apple Music 上报的位置更新很及时（滞后约 20ms），但它自己的时钟速率并不等于 1.0
    // （实测 0.96~0.99），所以**必须以它上报的位置为基准**，只在两次上报之间按真实间隔外推；
    // 用墙钟"从零推算"会每秒多走 20~40ms，一分钟就偏 1~2 秒。
    private TimeSpan _lastReportedValue = TimeSpan.MinValue;
    private DateTimeOffset _lastAdvanceAt = DateTimeOffset.MinValue;

    // 小幅回跳吸收：播放器两次上报之间位置偶尔会回退一两百毫秒，直接跟会让进度条倒退一格。
    private TimeSpan _smoothedPosition = TimeSpan.MinValue;
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private string? _smoothedTrackKey;
    private static readonly TimeSpan BackwardTolerance = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 播放位置的固定补偿。这一项回到了之前验证过的口径（默认 1400ms）：
    /// 用户实测 100ms 时歌词偏晚、1400ms 只是"略有差异"，所以以实测手感为准。
    /// 值越大歌词显示得越早；可按个人环境微调。
    /// </summary>
    public TimeSpan LatencyCompensation { get; set; } = TimeSpan.FromMilliseconds(1400);

    // ---- 时同步诊断（方案 0）----

    /// <summary>是否记录播放器位置样本，用于判断歌词时间误差属于哪种成因。诊断完改成 false 即可安静。</summary>
    public bool TimingDiagnostics { get; set; } = true;

    private DateTimeOffset _diagLastUpdate = DateTimeOffset.MinValue;
    private DateTimeOffset _diagLastSeenAt = DateTimeOffset.MinValue;
    private TimeSpan _diagLastReported = TimeSpan.MinValue;
    private string? _diagTrackKey;
    private string? _diagPlayerKey;
    private int _diagSamples;
    private double _diagSkewSumMs;
    private int _diagSkewCount;
    private double _diagResidualSum;
    private int _diagResidualCount;
    private double _diagStaleSum;
    private int _diagStaleCount;

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

        // SMTC 的 PlaybackInfoChanged 事件在部分应用(如 Spotify)中会延迟，
        // 导致停止后歌词仍然残留 — 因此周期轮询播放状态以立即反映。
        // 同时每次轮询都会重新选择最佳会话: 解决 Windows 10 上 Spotify 未被
        // 判定为"当前会话"(current 被其他应用占用)而无法识别的问题。
        service._pollTimer = new Timer(
            _ => { service.SelectBestSession(); service.RefreshPlayback(); }, null,
            TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        return service;
    }

    /// <summary>
    /// 重新选择要附加的 SMTC 会话。不单纯依赖 GetCurrentSession()，而是枚举全部会话，
    /// 优先选择处于"播放中"的会话(应对 Win10 上 Spotify 未被判定为 current 的问题)。
    /// 仅在选择发生变化时才重新订阅。
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
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
            _session = best;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
            changed = true;
            pickedId = best?.SourceAppUserModelId;
        }

        if (changed)
        {
            Musebase.Windows.Log.Write($"[smtc] 选择: {pickedId ?? "(无)"} (模式={_sourceMode}, 含浏览器={_includeBrowsers})");
            _ = RefreshTrackAsync();
            RefreshPlayback();
        }
    }

    /// <summary>
    /// 决定要附加的会话。
    /// - 固定到特定来源时: 仅使用该 SourceAppUserModelId 的会话(没有则不显示)。
    /// - 自动: current 正在播放则直接使用，否则取正在播放的会话，仍没有则用 current/第一个会话。
    ///   但浏览器会话(在选项关闭时)从候选中排除 —
    ///   以免把 Firefox 的 YouTube 视频等误识别为音乐。
    /// </summary>
    private Session? PickBestSession(SessionManager manager)
    {
        IReadOnlyList<Session> sessions;
        try { sessions = manager.GetSessions(); }
        catch { return null; }

        // 固定到特定播放器
        if (!IsAuto)
        {
            foreach (var s in sessions)
                if (string.Equals(s.SourceAppUserModelId, _sourceMode, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null; // 指定的播放器未运行时不显示
        }

        // 若指定了偏好应用则仅限这些应用，否则排除浏览器与视频/播客应用。
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
        try { current = manager.GetCurrentSession(); } catch { /* 竞态 */ }
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

    /// <summary>将 SMTC 会话列表记录到日志(用于诊断 Win10 上 Spotify 未被识别等问题)。</summary>
    private void LogSessions()
    {
        var manager = _manager;
        if (manager is null) return;
        try
        {
            var items = manager.GetSessions()
                .Select(s => $"{s.SourceAppUserModelId}[{SafeStatus(s)}]");
            Musebase.Windows.Log.Write($"[smtc] 会话列表: {string.Join(", ", items)}");
        }
        catch (Exception e)
        {
            Musebase.Windows.Log.Write($"[smtc] 会话列表查询失败: {e.Message}");
        }
    }

    private static string SafeStatus(Session s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus.ToString(); }
        catch { return "?"; }
    }

    // ---- 来源选择 / 播放控制 (外部 API) ----

    /// <summary>当前来源模式("auto" 或特定的 SourceAppUserModelId).</summary>
    public string SourceMode { get { lock (_lock) return _sourceMode; } }

    /// <summary>自动模式下是否包含浏览器.</summary>
    public bool IncludeBrowsers { get { lock (_lock) return _includeBrowsers; } }

    /// <summary>更改播放来源选择并立即重新选择会话.</summary>
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

    /// <summary>当前打开的 SMTC 会话的 SourceAppUserModelId 列表(供来源选择 UI 使用).</summary>
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

    /// <summary>指定来源是否为浏览器(自动模式判断用的 UI 提示).</summary>
    public static bool IsBrowser(string? appId) => IsBrowserSource(appId);

    /// <summary>当前会话的控件可用性.</summary>
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

    /// <summary>播放/暂停切换.</summary>
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

    /// <summary>下一曲.</summary>
    public async Task<bool> SkipNextAsync()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return false;
        try { return await session.TrySkipNextAsync(); }
        catch { return false; }
    }

    /// <summary>上一曲.</summary>
    public async Task<bool> SkipPreviousAsync()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return false;
        try { return await session.TrySkipPreviousAsync(); }
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

    private void OnTimelinePropertiesChanged(Session sender, TimelinePropertiesChangedEventArgs args)
    {
        lock (_lock)
        {
            if (ReferenceEquals(sender, _session))
            {
                // 注意：这里**不能**重锚。播放器每秒都会推一次 Timeline，
                // 每次都重锚就等于又回到"信上报值"的老路，锯齿照样会反映到歌词上。
                try
                {
                    var timeline = sender.GetTimelineProperties();
                    if (timeline.EndTime > TimeSpan.Zero && CurrentTrack is { } cur && cur.Duration != timeline.EndTime)
                    {
                        CurrentTrack = cur with { Duration = timeline.EndTime };
                        TrackChanged?.Invoke(CurrentTrack);
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 取来正在播放曲目的专辑封面，且<b>完全按照播放应用提供的原样</b>。
    ///
    /// Spotify、Apple Music 等会把封面一并放进 SMTC 发送。相比外部检索(iTunes·Deezer)
    /// 这样做的好处很明确 — 既不需要网络也不需要密钥，而且<b>不会因检索而拿到别的专辑</b>
    /// (可区分重制版·单曲版，连韩国独立音乐这类 iTunes 上完全没有的曲目也能对上)。
    /// 也有不提供封面的应用，取不到时为 null，此时回退到服务端封面。
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
            if (stream.Size is 0 or > 8 * 1024 * 1024) return null; // 跳过异常尺寸

            var bytes = new byte[stream.Size];
            using var reader = new global::Windows.Storage.Streams.DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception)
        {
            return null; // 会话消失的竞态等 — 按无封面处理
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

                // 广告时段不是曲目 — 不创建曲目，因此歌词检索与服务端查询完全不会发生。
                // SMTC 没有广告标志与 mediaId，因此只看经实测验证的信号 ③(艺人=Spotify +
                // 专辑为空)。判定规则与 Android 使用同一套代码(Engine 的 AdSignals)。
                if (!string.IsNullOrEmpty(props.Title) && !AdSignals.LooksLikeAd(artist, album))
                    track = new TrackInfo(props.Title, artist, album, duration, session.SourceAppUserModelId);
            }
            catch
            {
                // 会话消失的时序竞态 — 按无曲目处理
            }
        }

        if (!Equals(track, CurrentTrack))
        {
            CurrentTrack = track;
            lock (_lock)
            {
                // 换曲后"上次位置"与平滑状态都作废
                _lastReportedValue = TimeSpan.MinValue;
                _lastAdvanceAt = DateTimeOffset.MinValue;
                _smoothedPosition = TimeSpan.MinValue;
                _smoothedTrackKey = null;
                _lastSampleAt = DateTimeOffset.MinValue;
            }
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
            // 会话销毁竞态
        }

        if (playing != IsPlaying)
        {
            IsPlaying = playing;
            IsPlayingChanged?.Invoke(playing);
        }
    }

    /// <summary>
    /// 当前播放位置的估计值。
    ///
    /// <b>以本机单调时钟为主时钟</b>：播放时位置 = 锚点 + Stopwatch 流逝，
    /// SMTC 上报的位置只当"测量值"，用于重锚（切歌 / 暂停恢复 / 明显 seek / 持续偏离）。
    /// 这样做的原因是实测发现 Apple Music 上报的位置呈锯齿状（每 ~81 秒一次性前跳约 2 秒，
    /// 期间最多落后真实时间 2 秒），直接采信会让歌词随之摆动，而一个固定补偿值
    /// 只能在平均意义上正确。详见 <see cref="LogTimingSample"/> 的日志字段说明。
    /// </summary>
    /// <summary>
    /// 当前播放位置的估计值。
    ///
    /// 以播放器上报的位置为基准：`上报值 + (现在 − 该值的发布时间)`，再加
    /// <see cref="LatencyCompensation"/> 那几十毫秒。这是唯一可靠的基准，
    /// 因为播放器自己的时钟速率未必是 1.0（实测 Apple Music 约 0.96~0.99），
    /// 用墙钟从零推算会持续领先、越放越偏。
    ///
    /// 额外处理一种异常：有的播放器只更新 LastUpdatedTime 而位置值不动，
    /// 此时按"距上次位置真正变化"的时长外推，避免进度卡死。
    /// </summary>
    public TimeSpan? GetEstimatedPosition()
    {
        Session? session;
        lock (_lock) session = _session;
        if (session is null) return null;

        TimeSpan reported;
        DateTimeOffset lastUpdated;
        bool playingNow;
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline.EndTime > TimeSpan.Zero && CurrentTrack is { } cur && cur.Duration != timeline.EndTime)
            {
                CurrentTrack = cur with { Duration = timeline.EndTime };
                TrackChanged?.Invoke(CurrentTrack);
            }

            reported = timeline.Position;
            lastUpdated = timeline.LastUpdatedTime;
            // 直接问会话，不用缓存字段 IsPlaying：冷启动时它最多晚 250ms，
            // 会让首帧误判成"暂停"、跳过外推，整首歌滞后。
            playingNow = session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        TimeSpan result;
        if (!playingNow)
        {
            result = reported + LatencyCompensation;
        }
        else
        {
            // 记录"位置值上一次真正变化"的时刻，用来识别"位置停滞但仍在播放"的播放器
            if (reported != _lastReportedValue)
            {
                _lastReportedValue = reported;
                _lastAdvanceAt = now;
            }

            var age = now - lastUpdated;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;

            var frozenFor = _lastAdvanceAt == DateTimeOffset.MinValue ? TimeSpan.Zero : now - _lastAdvanceAt;
            if (frozenFor > TimeSpan.FromSeconds(2) && frozenFor > age) age = frozenFor;

            if (age > TimeSpan.FromMinutes(30)) age = TimeSpan.Zero; // 明显异常，不外推
            result = reported + age + LatencyCompensation;

            // 上报位置偶尔回退一两百毫秒：同曲播放中依本地时间平滑递增，避免进度条倒退/抖动
            var trackKey = CurrentTrack is { } t ? $"{t.Title}|{t.Artist}" : null;
            if (trackKey == _smoothedTrackKey
                && _smoothedPosition != TimeSpan.MinValue
                && _lastSampleAt != DateTimeOffset.MinValue
                && result < _smoothedPosition
                && _smoothedPosition - result <= BackwardTolerance)
            {
                var localElapsed = now - _lastSampleAt;
                if (localElapsed > TimeSpan.Zero && localElapsed < TimeSpan.FromSeconds(0.5))
                    result = _smoothedPosition + localElapsed;
            }

            _smoothedPosition = result;
            _smoothedTrackKey = trackKey;
            _lastSampleAt = now;
        }

        if (TimingDiagnostics)
            LogTimingSample(reported, lastUpdated, now, result);

        return result;
    }

    /// 时同步诊断（方案 0）：只在播放器真的给出**新**位置样本时记一行，不会刷屏。
    ///
    /// 字段判读：
    /// - <c>reported</c>：播放器上报的播放位置；
    /// - <c>gap</c>：距上一次上报经过的真实时间；<c>advanced</c>：上报位置实际前进了多少；
    /// - <c>skew</c> = advanced − gap。持续不为 0 → 播放器位置与墙钟速率不同，
    ///   误差会随播放时长累积（这就是"越到后面越偏"）；
    /// - <c>stale</c> = now − LastUpdatedTime：播放器有多"懒"，越大说明外推越久、误差上界越大；
    /// - <c>residual</c> = 本次上报 − 按上次样本外推的预测值。≈0 表示外推准；
    ///   恒定偏置 = 固定延迟（补偿值问题）；秒级跳变 = seek / 切歌 / 播放器重新计时；
    /// - <c>comp</c>：当前生效的固定补偿；
    /// - <c>est</c>：我们最终给出的位置；<c>d</c> = est − reported，正数表示我们比播放器报告更靠前；
    /// - <c>cor</c>：自校准已施加的毫秒数；<c>max</c>：本曲观测到的最大残差（校准上限）。
    /// 每 30 个样本再打一行汇总（平均 skew / residual / stale），方便一眼定性。
    /// </summary>
    private void LogTimingSample(TimeSpan reported, DateTimeOffset lastUpdated, DateTimeOffset now, TimeSpan estimate)
    {
        var trackKey = CurrentTrack is { } track ? $"{track.Title}|{track.Artist}" : "(无曲目)";
        var playerKey = CurrentTrack?.SourceAppId ?? "(未知)";
        if (!string.Equals(trackKey, _diagTrackKey, StringComparison.Ordinal)
            || !string.Equals(playerKey, _diagPlayerKey, StringComparison.Ordinal))
        {
            FlushTimingSummary();
            Musebase.Windows.Log.Write($"[timing] ---- 曲目切换 → {trackKey}"
                + $"｜播放器={playerKey}｜补偿={LatencyCompensation.TotalMilliseconds:0}ms ----");
            _diagTrackKey = trackKey;
            _diagPlayerKey = playerKey;
            _diagLastUpdate = DateTimeOffset.MinValue;
            _diagLastSeenAt = DateTimeOffset.MinValue;
            _diagLastReported = TimeSpan.MinValue;
        }

        if (lastUpdated == _diagLastUpdate && reported == _diagLastReported) return; // 不是新样本
        if (!IsPlaying) return; // 暂停时位置本就不前进，记下来只会污染统计

        var gap = _diagLastSeenAt == DateTimeOffset.MinValue ? double.NaN : (now - _diagLastSeenAt).TotalSeconds;
        var advanced = _diagLastReported == TimeSpan.MinValue ? double.NaN : (reported - _diagLastReported).TotalSeconds;
        var skewMs = double.IsNaN(gap) ? double.NaN : (advanced - gap) * 1000;
        var residual = double.IsNaN(gap) ? double.NaN : reported.TotalSeconds - (_diagLastReported.TotalSeconds + gap);
        var stale = (now - lastUpdated).TotalSeconds;

        var line = $"[timing] t={reported.TotalSeconds:0.000}s stale={stale:0.000}s"
            + $" comp={LatencyCompensation.TotalMilliseconds:0}ms"
            + $" est={estimate.TotalSeconds:0.000}s d={(estimate - reported).TotalSeconds:+0.000;-0.000;0.000}s";
        if (!double.IsNaN(gap))
            line += $" gap={gap:0.000}s advanced={advanced:0.000}s"
                + $" skew={skewMs:+0;-0;0}ms residual={residual:+0.000;-0.000;0.000}s";
        Musebase.Windows.Log.Write(line);

        _diagSamples++;
        // 汇总只统计"正常播放"的样本：seek/切歌造成的秒级跳变不参与平均
        var normal = !double.IsNaN(residual) && Math.Abs(residual) <= 1.0 && Math.Abs(skewMs) <= 500;
        if (normal)
        {
            _diagSkewSumMs += skewMs;
            _diagSkewCount++;
            _diagResidualSum += residual;
            _diagResidualCount++;
            _diagStaleSum += stale;
            _diagStaleCount++;
        }
        if (_diagSamples % 30 == 0) FlushTimingSummary();

        _diagLastUpdate = lastUpdated;
        _diagLastSeenAt = now;
        _diagLastReported = reported;
    }

    private void FlushTimingSummary()
    {
        if (_diagSamples == 0) return;
        Musebase.Windows.Log.Write($"[timing] 汇总 样本={_diagSamples}"
            + (_diagSkewCount > 0 ? $" 平均skew={_diagSkewSumMs / _diagSkewCount:+0;-0;0}ms" : "")
            + (_diagResidualCount > 0 ? $" 平均residual={_diagResidualSum / _diagResidualCount:+0.000;-0.000;0.000}s" : "")
            + (_diagStaleCount > 0 ? $" 平均stale={_diagStaleSum / _diagStaleCount:0.000}s" : ""));
        _diagSamples = 0;
        _diagSkewSumMs = 0;
        _diagSkewCount = 0;
        _diagResidualSum = 0;
        _diagResidualCount = 0;
        _diagStaleSum = 0;
        _diagStaleCount = 0;
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
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                _session = null;
            }
        }
    }
}
