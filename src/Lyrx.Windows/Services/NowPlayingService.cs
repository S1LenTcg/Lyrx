using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        "firefox", "mozilla", "308046b0af4a39cb", "chrome", "chromium", "msedge", "microsoftedge",
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

    /// <summary>
    /// 视频站点/播放器名。B 站 UWP、AcFun 这类应用会把站点名填进艺人字段，
    /// 留着会让歌词搜索按一个根本不存在的"艺人"去查，直接查不到。
    /// （注意不要把 Spotify 列进来 —— 它在广告时段正是靠艺人名做判定。）
    /// </summary>
    private static readonly string[] SiteNameArtists =
    {
        "哔哩哔哩", "bilibili", "b站", "b 站", "acfun", "a站", "a 站", "youtube",
    };

    private static string SanitizeArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var trimmed = artist.Trim();
        foreach (var site in SiteNameArtists)
            if (string.Equals(trimmed, site, StringComparison.OrdinalIgnoreCase)) return "";
        return trimmed;
    }

    /// <summary>
    /// 视频站点（B 站 UWP、AcFun、YouTube 等）。这类应用给的是**视频标题**：
    /// `【Hi-Res】百万级装备试听`、`1080P`、`《歌名》` 之类的标记必须去掉，
    /// 否则既污染窗口标题，也污染歌词检索词。
    /// </summary>
    private static bool IsVideoSite(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        foreach (var token in new[] { "bilibili", "哔哩哔哩", "acfun", "youtube", "atelier39", "31846059f82c1", "bilibilipc" })
            if (appId.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly ConcurrentDictionary<string, string?> ShellDisplayNameCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Guid ShellItemIid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem? ppv);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    /// <summary>通过 Windows AppsFolder 解析不透明的 AppUserModelId（如 Firefox 的哈希串）对应的真实应用名称。</summary>
    private static string? ResolveShellAppDisplayName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        return ShellDisplayNameCache.GetOrAdd(appId, static id =>
        {
            try
            {
                if (SHCreateItemFromParsingName($"shell:AppsFolder\\{id}", IntPtr.Zero, ShellItemIid, out var item) == 0
                    && item is not null)
                {
                    try
                    {
                        item.GetDisplayName(0, out var name);
                        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            catch { }
            return null;
        });
    }

    /// <summary>为常见播放器（含浏览器）生成易读的友好显示名称。</summary>
    public static string FormatSourceDisplayName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var lower = id.ToLowerInvariant();
        if (lower.Contains("bilibilipc"))
            return "Bilibili PC";
        if (lower.Contains("bilibili") || lower.Contains("atelier39") || lower.Contains("31846059f82c1"))
            return "哔哩哔哩 (Bilibili)";
        if (lower.Contains("applemusic"))
            return "Apple Music";
        if (lower.Contains("spotify"))
            return "Spotify";
        if (lower.Contains("cloudmusic") || lower.Contains("netease"))
            return "网易云音乐";
        if (lower.Contains("qqmusic"))
            return "QQ音乐";
        if (lower.Contains("kugou"))
            return "酷狗音乐";
        if (lower.Contains("kuwo"))
            return "酷我音乐";
        if (lower.Contains("308046b0af4a39cb") || lower.Contains("firefox"))
            return "Firefox";
        if (lower.Contains("msedge") || lower.Contains("microsoftedge"))
            return "Microsoft Edge";
        if (lower.Contains("chrome"))
            return "Google Chrome";
        if (lower.Contains("brave"))
            return "Brave";
        if (lower.Contains("opera"))
            return "Opera";
        if (lower.Contains("vivaldi"))
            return "Vivaldi";
        if (lower.Contains("waterfox"))
            return "Waterfox";
        if (lower.Contains("librewolf"))
            return "LibreWolf";
        return ResolveShellAppDisplayName(id) ?? id;
    }

    /// <summary>视频站点标题 → 只留"歌手 - 歌名"这类有效信息；清洗后为空则保留原标题。</summary>
    private static string SanitizeTitle(string title, string? appId)
    {
        if (!IsVideoSite(appId) || string.IsNullOrWhiteSpace(title)) return title;
        var cleaned = Musebase.Core.Search.SearchTermCleaner.CleanTitle(title).Trim();
        return cleaned.Length > 0 ? cleaned : title;
    }

    private static bool IsBrowserSource(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        foreach (var token in BrowserTokens)
            if (appId.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        if (ResolveShellAppDisplayName(appId) is { } resolved)
        {
            foreach (var token in BrowserTokens)
                if (resolved.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>是否为浏览器或视频站点（标题通常包含视频修饰词，需按视频规则提取歌名与歌手）。</summary>
    public static bool IsBrowserOrVideoSource(string? appId) =>
        IsVideoSite(appId) || IsBrowserSource(appId);

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

    // ---- "播放器到底有没有在报位置"的观测窗口 ----
    // B 站 UWP、部分网页版只把位置在 0/1 之间抖一下，或干脆不动；
    // 这种情况必须改用本机时钟推进，否则进度条与歌词会钉死不动。
    private DateTimeOffset _positionWindowStart = DateTimeOffset.MinValue;
    private TimeSpan _positionWindowPosition;
    private bool _positionReliable = true;
    private TimeSpan _wallAnchorPosition;
    private DateTimeOffset _wallAnchorAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSeekAt = DateTimeOffset.MinValue;
    private bool _seekAnchorActive;
    private TimeSpan _seekBaselineReported = TimeSpan.MinValue;
    private long _seekSeq;

    /// <summary>本机开始计时的那首曲目（配上 <see cref="_trackPlayStartedAt"/> 使用）。</summary>
    private string? _timingTrackKey;

    /// <summary>本机看到这首歌"开始播放"的时刻。播放器不报位置时，它就是位置基准。</summary>
    private DateTimeOffset _trackPlayStartedAt = DateTimeOffset.MinValue;

    /// <summary>上一次算出的位置。暂停时沿用它，避免回落到播放器那个 0/1 的假值。</summary>
    private TimeSpan _lastResult;

    // 小幅回跳吸收：播放器两次上报之间位置偶尔会回退一两百毫秒，直接跟会让进度条倒退一格。
    private TimeSpan _smoothedPosition = TimeSpan.MinValue;
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private string? _smoothedTrackKey;
    private static readonly TimeSpan BackwardTolerance = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Apple Music 播放位置的固定补偿（默认 +1.4s）。
    /// Windows 版 Apple Music 的 SMTC 上报有约 1.4 秒固有时延；其他播放器不加此补偿。
    /// </summary>
    public TimeSpan LatencyCompensation { get; set; } = TimeSpan.FromMilliseconds(1400);

    /// <summary>判断是否为 Apple Music 客户端。</summary>
    public static bool IsAppleMusicSource(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        return appId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)
            || appId.Contains("AppleInc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 当前播放源生效的时延补偿：Apple Music 使用 <see cref="LatencyCompensation"/>，其他情况使用 0ms。
    /// </summary>
    public TimeSpan EffectiveLatencyCompensation
    {
        get
        {
            var appId = CurrentTrack?.SourceAppId ?? _session?.SourceAppUserModelId;
            return IsAppleMusicSource(appId) ? LatencyCompensation : TimeSpan.Zero;
        }
    }

    // ---- 时同步诊断（方案 0）----

    private volatile bool _timingDiagnostics;
    private readonly object _diagLock = new();

    /// <summary>是否记录播放器位置样本（<c>[timing]</c>），用于判断歌词时间误差属于哪种成因。默认关闭。</summary>
    public bool TimingDiagnostics
    {
        get => _timingDiagnostics;
        set
        {
            lock (_diagLock)
            {
                if (_timingDiagnostics == value) return;
                _timingDiagnostics = value;
                if (!value) ResetTimingDiagnosticsState();
            }
        }
    }

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

    /// <summary>跳转播放进度。若物理播放器支持则改变播放进度；不支持或仅校准时钟时对齐本地推算基准。</summary>
    public async Task<bool> SeekAsync(TimeSpan position)
    {
        Session? session;
        lock (_lock) session = _session;

        var now = DateTimeOffset.Now;
        var compensation = EffectiveLatencyCompensation;
        var physicalTicks = Math.Max(0, (position - compensation).Ticks);
        var physicalTarget = TimeSpan.FromTicks(physicalTicks);
        var displayTarget = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        var playing = session is not null && IsSessionPlaying(session);
        var appId = CurrentTrack?.SourceAppId ?? session?.SourceAppUserModelId;
        var allowLocalCalibrationFallback = IsVideoSite(appId);
        long seekSeq;

        lock (_lock)
        {
            seekSeq = Interlocked.Increment(ref _seekSeq);
            if (!_positionReliable) allowLocalCalibrationFallback = true;

            // 保存跳转前真实观测到的 SMTC 上报值；不要把 _lastReportedValue/_positionWindowPosition 改写成 target，
            // 否则 B 站 PC 等始终报 0:00 的播放器会在下一帧被 postSeekSnap 误判为"退回 0:00"，瞬间冲掉对准结果。
            _seekBaselineReported = _lastReportedValue;
            _seekAnchorActive = true;
            _trackPlayStartedAt = now - physicalTarget;
            _wallAnchorPosition = physicalTarget;
            _wallAnchorAt = playing ? now : DateTimeOffset.MinValue;
            _lastSeekAt = now;
            _lastResult = displayTarget;
            _smoothedPosition = displayTarget;
            _lastSampleAt = now;
        }

        if (session is null)
        {
            lock (_lock)
            {
                if (_seekSeq == seekSeq) _seekAnchorActive = false;
            }
            return false;
        }

        try
        {
            var success = await session.TryChangePlaybackPositionAsync(physicalTicks);
            var settledAt = DateTimeOffset.Now;
            var stillPlaying = IsSessionPlaying(session);

            if (!success && !allowLocalCalibrationFallback)
            {
                lock (_lock)
                {
                    // 正常报时长的音频播放器拒绝跳转时撤回超前锚点，避免歌词跑偏而音乐原地不动
                    if (_seekSeq == seekSeq) _seekAnchorActive = false;
                }
                Musebase.Windows.Log.Write("[sync] 播放器拒绝物理跳转，保持真实位置");
                return false;
            }

            lock (_lock)
            {
                if (_seekSeq == seekSeq)
                {
                    _lastSeekAt = settledAt;
                    if (stillPlaying) _wallAnchorAt = settledAt;
                }
            }
            Musebase.Windows.Log.Write(success
                ? $"[smtc] 物理播放器已跳转到: {physicalTarget:mm\\:ss} (歌词={displayTarget:mm\\:ss})"
                : $"[sync] 播放器未响应物理跳转，已自动校准本地时钟至: {displayTarget:mm\\:ss}");
            return true;
        }
        catch (Exception ex)
        {
            if (!allowLocalCalibrationFallback)
            {
                lock (_lock)
                {
                    if (_seekSeq == seekSeq) _seekAnchorActive = false;
                }
                Musebase.Windows.Log.Write($"[sync] 物理跳转异常: {ex.Message}");
                return false;
            }
            Musebase.Windows.Log.Write($"[sync] 物理跳转异常（已校准本地时钟）: {ex.Message}");
            return true;
        }
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

    private long _thumbnailVersion;
    private long _trackRefreshSeq;
    private string? _lastThumbnailSignature;
    private volatile bool _hasThumbnail;

    /// <summary>当前 SMTC 曲目封面变更计数；当同一首歌延后或更换推送 SMTC 封面时会递增。</summary>
    public long ThumbnailVersion => Volatile.Read(ref _thumbnailVersion);

    /// <summary>当前 SMTC 会话最近一次检测到的封面缩略图是否非空。</summary>
    public bool HasThumbnail => _hasThumbnail;

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
            return await ReadThumbnailBytesAsync(props.Thumbnail);
        }
        catch (Exception)
        {
            return null; // 会话消失的竞态等 — 按无封面处理
        }
    }

    private static async Task<byte[]?> ReadThumbnailBytesAsync(
        global::Windows.Storage.Streams.IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
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
            return null;
        }
    }

    private static string ComputeThumbnailSignature(byte[]? bytes) =>
        bytes is { Length: > 0 }
            ? $"{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}"
            : "";

    private static bool SameTrackIdentity(TrackInfo? a, TrackInfo? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.Title, b.Title, StringComparison.Ordinal)
            && string.Equals(a.Artist, b.Artist, StringComparison.Ordinal)
            && string.Equals(a.SourceAppId, b.SourceAppId, StringComparison.Ordinal);
    }

    private async Task RefreshTrackAsync()
    {
        var seq = Interlocked.Increment(ref _trackRefreshSeq);
        Session? session;
        lock (_lock) session = _session;

        TrackInfo? track = null;
        global::Windows.Storage.Streams.IRandomAccessStreamReference? thumbRef = null;
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
                // 注意：广告判定要用**原始艺人名**，清洗要在判定之后做。
                if (!string.IsNullOrEmpty(props.Title) && !AdSignals.LooksLikeAd(artist, album))
                {
                    string finalTitle;
                    string finalArtist;
                    if (IsBrowserOrVideoSource(session.SourceAppUserModelId))
                    {
                        Musebase.Core.Search.SearchTermCleaner.ExtractVideoSongInfo(
                            props.Title, artist, out finalTitle, out finalArtist);
                    }
                    else
                    {
                        finalTitle = SanitizeTitle(props.Title, session.SourceAppUserModelId);
                        finalArtist = SanitizeArtist(artist);
                    }

                    track = new TrackInfo(
                        finalTitle,
                        finalArtist,
                        album, duration, session.SourceAppUserModelId);
                    thumbRef = props.Thumbnail;
                }
            }
            catch
            {
                // 会话消失的时序竞态 — 按无曲目处理
            }
        }

        var trackChanged = !Equals(track, CurrentTrack);
        if (trackChanged)
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
                _positionWindowStart = DateTimeOffset.MinValue;
                _positionWindowPosition = TimeSpan.Zero;
                _positionReliable = true;
                _wallAnchorAt = DateTimeOffset.MinValue;
                _wallAnchorPosition = TimeSpan.Zero;
                _timingTrackKey = null;
                _trackPlayStartedAt = DateTimeOffset.MinValue;
                _seekAnchorActive = false;
                _seekBaselineReported = TimeSpan.MinValue;
                _lastThumbnailSignature = null;
                _hasThumbnail = thumbRef is not null;
            }
            TrackChanged?.Invoke(track);
        }

        if (track is null)
        {
            if (seq == Volatile.Read(ref _trackRefreshSeq))
            {
                lock (_lock)
                {
                    _lastThumbnailSignature = null;
                    _hasThumbnail = false;
                }
            }
            return;
        }

        var thumbBytes = await ReadThumbnailBytesAsync(thumbRef).ConfigureAwait(false);
        if (seq != Volatile.Read(ref _trackRefreshSeq)) return;

        var sig = ComputeThumbnailSignature(thumbBytes);
        lock (_lock)
        {
            if (!SameTrackIdentity(track, CurrentTrack)) return;
            _hasThumbnail = thumbBytes is { Length: > 0 };
            if (trackChanged)
            {
                _lastThumbnailSignature = sig;
            }
            else if (_lastThumbnailSignature is null || !string.Equals(sig, _lastThumbnailSignature, StringComparison.Ordinal))
            {
                _lastThumbnailSignature = sig;
                Interlocked.Increment(ref _thumbnailVersion);
            }
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
        var trackKey = CurrentTrack is { } t ? $"{t.Title}|{t.Artist}" : null;
        var compensation = EffectiveLatencyCompensation;
        var age = now - lastUpdated;
        if (age < TimeSpan.Zero || age > TimeSpan.FromMinutes(30)) age = TimeSpan.Zero; // 明显异常，不外推
        var timelineValid = reported > TimeSpan.Zero || lastUpdated > DateTimeOffset.UnixEpoch;

        TimeSpan result;
        lock (_lock)
        {
            // 换曲/首次播放时初始化本机推算锚点；若 SMTC 完全未更新时间戳且位置为 0（如 B 站 PC），直接进入本机推算模式
            if (playingNow && (!string.Equals(trackKey, _timingTrackKey, StringComparison.Ordinal)
                               || _trackPlayStartedAt == DateTimeOffset.MinValue))
            {
                var initialPos = reported + age;
                if (initialPos < TimeSpan.Zero) initialPos = TimeSpan.Zero;
                _timingTrackKey = trackKey;
                _trackPlayStartedAt = now - initialPos;
                _wallAnchorPosition = initialPos;
                _wallAnchorAt = now;
                _positionWindowStart = DateTimeOffset.MinValue;
                _positionReliable = timelineValid;
                _seekAnchorActive = false;
                _seekBaselineReported = TimeSpan.MinValue;
                _lastReportedValue = reported;
                _lastAdvanceAt = timelineValid ? now : DateTimeOffset.MinValue;
            }

            if (!playingNow)
            {
                var useLocalClock = !_positionReliable || _seekAnchorActive;
                if (_wallAnchorAt != DateTimeOffset.MinValue)
                {
                    // 刚从播放切到暂停：冻结当前锚点位置，避免恢复播放时把暂停时长算进去
                    var frozen = useLocalClock
                        ? (_lastResult > compensation ? _lastResult - compensation : _wallAnchorPosition)
                        : reported;
                    _wallAnchorPosition = frozen < TimeSpan.Zero ? TimeSpan.Zero : frozen;
                    _wallAnchorAt = DateTimeOffset.MinValue;
                }

                if (useLocalClock && IsSignificantJump(reported, _lastReportedValue))
                {
                    _lastReportedValue = reported;
                    _lastAdvanceAt = now;
                    _seekAnchorActive = false;
                    _wallAnchorPosition = reported < TimeSpan.Zero ? TimeSpan.Zero : reported;
                    _lastResult = _wallAnchorPosition + compensation;
                }

                // 暂停时若使用本地计时或刚完成人工对准，沿用本地冻结位置
                result = useLocalClock ? _lastResult : reported + compensation;
            }
            else
            {
                if (reported != _lastReportedValue)
                {
                    var isLowJitter = reported <= TimeSpan.FromSeconds(1.5)
                        && _lastReportedValue != TimeSpan.MinValue
                        && _lastReportedValue <= TimeSpan.FromSeconds(1.5);
                    var postSeekMoved = _seekAnchorActive
                        && _seekBaselineReported != TimeSpan.MinValue
                        && reported != _seekBaselineReported
                        && !isLowJitter;
                    var jumped = postSeekMoved || IsSignificantJump(reported, _lastReportedValue);
                    _lastReportedValue = reported;
                    _lastAdvanceAt = now;

                    if (jumped)
                    {
                        _positionWindowStart = now;
                        _positionWindowPosition = reported;
                    }

                    // 播放器自身确实移动了上报位置时重锚；不报位置导致始终停留在原值时不会误触发
                    if ((!_positionReliable || _seekAnchorActive) && jumped)
                    {
                        var reanchored = reported + age;
                        if (reanchored < TimeSpan.Zero) reanchored = TimeSpan.Zero;
                        _wallAnchorPosition = reanchored;
                        _wallAnchorAt = now;
                        _seekAnchorActive = false;
                    }
                }
                else if (_lastAdvanceAt != DateTimeOffset.MinValue && lastUpdated > _lastAdvanceAt)
                {
                    // 播放器只更新了 LastUpdatedTime 而位置值不动：用距上次位置真正变化的时间外推，避免进度卡死或倒退
                    var sinceAdvance = now - _lastAdvanceAt;
                    if (sinceAdvance > age && sinceAdvance <= TimeSpan.FromMinutes(30))
                        age = sinceAdvance;
                }

                // ---- 观测窗口：判断这个播放器到底有没有在报位置 ----
                // B 站 UWP/PC、部分网页播放器只把位置在 0/1 之间抖一下，或根本不更新 SMTC 位置。
                if (_positionWindowStart == DateTimeOffset.MinValue)
                {
                    _positionWindowStart = now;
                    _positionWindowPosition = reported;
                }
                else if ((now - _positionWindowStart).TotalSeconds >= 5)
                {
                    var advanced = reported - _positionWindowPosition;
                    var reliable = advanced >= TimeSpan.FromSeconds(2.5);
                    if (reliable != _positionReliable)
                    {
                        _positionReliable = reliable;
                        if (!reliable)
                        {
                            // 无缝接续当前已推进或刚对齐的位置，不回退覆盖成 0:00
                            var currentPos = _seekAnchorActive
                                ? _wallAnchorPosition + (_wallAnchorAt == DateTimeOffset.MinValue ? TimeSpan.Zero : now - _wallAnchorAt)
                                : (_lastResult > compensation ? _lastResult - compensation : reported + age);
                            _wallAnchorPosition = currentPos < TimeSpan.Zero ? TimeSpan.Zero : currentPos;
                            _wallAnchorAt = now;
                            _seekAnchorActive = false;
                        }
                        else
                        {
                            _wallAnchorPosition = reported + age;
                            _wallAnchorAt = now;
                            _seekAnchorActive = false;
                        }
                        Musebase.Windows.Log.Write(reliable
                            ? $"[sync] 播放器位置恢复正常（5 秒内前进 {advanced.TotalSeconds:0.0}s），恢复跟随上报值"
                            : $"[sync] 播放器 5 秒内只前进 {advanced.TotalSeconds:0.0}s，判定不报位置，改用本机计时推算");
                    }
                    _positionWindowStart = now;
                    _positionWindowPosition = reported;
                }

                if (_positionReliable && _seekAnchorActive && _lastSeekAt != DateTimeOffset.MinValue
                    && (now - _lastSeekAt).TotalSeconds > 3.0)
                {
                    _seekAnchorActive = false;
                }

                if (!_positionReliable || _seekAnchorActive)
                {
                    // 不报/间歇报位置的播放器或刚手工对准时：从锚点按本机流逝时间平滑推进
                    if (_wallAnchorAt == DateTimeOffset.MinValue)
                        _wallAnchorAt = now;
                    var elapsed = _wallAnchorPosition + (now - _wallAnchorAt);
                    if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                    if (CurrentTrack?.Duration is { } dur && elapsed > dur) elapsed = dur;
                    result = elapsed + compensation;
                }
                else
                {
                    result = reported + age + compensation;
                    _wallAnchorPosition = reported + age;
                    _wallAnchorAt = now;
                }

                // 上报位置偶尔回退一两百毫秒：在同曲播放中依本地时间平滑递增，避免进度条倒退
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

            if (result < TimeSpan.Zero) result = TimeSpan.Zero;
            _lastResult = result;
        }

        if (_timingDiagnostics)
            LogTimingSample(reported, lastUpdated, now, result);

        return result;
    }

    private static bool IsSignificantJump(TimeSpan current, TimeSpan previous)
    {
        if (previous == TimeSpan.MinValue) return false;
        // 忽略 0s <-> 1s 抖动，仅响应真实拖动/切段/重置
        if (current <= TimeSpan.FromSeconds(1.5) && previous <= TimeSpan.FromSeconds(1.5))
            return false;
        return Math.Abs((current - previous).TotalSeconds) > 1.5;
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
        lock (_diagLock)
        {
            if (!_timingDiagnostics) return;

            var trackKey = CurrentTrack is { } track ? $"{track.Title}|{track.Artist}" : "(无曲目)";
            var playerKey = CurrentTrack?.SourceAppId ?? "(未知)";
            var effectiveComp = EffectiveLatencyCompensation;
            if (!string.Equals(trackKey, _diagTrackKey, StringComparison.Ordinal)
                || !string.Equals(playerKey, _diagPlayerKey, StringComparison.Ordinal))
            {
                FlushTimingSummary();
                Musebase.Windows.Log.Write($"[timing] ---- 曲目切换 → {trackKey}"
                    + $"｜播放器={playerKey}｜补偿={effectiveComp.TotalMilliseconds:0}ms ----");
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
                + $" comp={effectiveComp.TotalMilliseconds:0}ms"
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
    }

    private void FlushTimingSummary()
    {
        if (_diagSamples == 0) return;
        Musebase.Windows.Log.Write($"[timing] 汇总 样本={_diagSamples}"
            + (_diagSkewCount > 0 ? $" 平均skew={_diagSkewSumMs / _diagSkewCount:+0;-0;0}ms" : "")
            + (_diagResidualCount > 0 ? $" 平均residual={_diagResidualSum / _diagResidualCount:+0.000;-0.000;0.000}s" : "")
            + (_diagStaleCount > 0 ? $" 平均stale={_diagStaleSum / _diagStaleCount:0.000}s" : ""));
        ResetTimingSummaryCounters();
    }

    private void ResetTimingSummaryCounters()
    {
        _diagSamples = 0;
        _diagSkewSumMs = 0;
        _diagSkewCount = 0;
        _diagResidualSum = 0;
        _diagResidualCount = 0;
        _diagStaleSum = 0;
        _diagStaleCount = 0;
    }

    private void ResetTimingDiagnosticsState()
    {
        _diagTrackKey = null;
        _diagPlayerKey = null;
        _diagLastUpdate = DateTimeOffset.MinValue;
        _diagLastSeenAt = DateTimeOffset.MinValue;
        _diagLastReported = TimeSpan.MinValue;
        ResetTimingSummaryCounters();
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
