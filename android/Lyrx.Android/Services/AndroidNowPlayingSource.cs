using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using MediaController = Android.Media.Session.MediaController;
using Android.OS;
using Android.Provider;
using Musebase.Core.Search;
using Musebase.Engine;

namespace Musebase.Android.Services;

/// <summary>
/// <see cref="INowPlayingSource"/> 的 Android 实现：
/// 通过 <see cref="MediaSessionManager"/> + <see cref="MediaController.Callback"/> 监听系统媒体会话，
/// 负责选源、广告过滤、视频标题清洗、播放位置外推及传输控制。
/// </summary>
public sealed class AndroidNowPlayingSource : Java.Lang.Object, INowPlayingSource, MediaSessionManager.IOnActiveSessionsChangedListener
{
    private static readonly string[] BrowserTokens =
    {
        "chrome", "firefox", "mozilla", "edge", "emmx", "opera", "brave", "vivaldi",
        "ucmobile", "quark", "via", "kiwi", "samsung.android.app.sbrowser", "webview",
    };

    private static readonly string[] VideoAppTokens =
    {
        "bilibili", "bili", "youtube", "youku", "iqiyi", "tencent.qqlive", "netflix",
        "disney", "primevideo", "twitch", "vlc", "mxtech", "podcast", "audible",
        "douyin", "tiktok", "kuaishou", "acfun",
    };

    private readonly Context _context;
    private readonly ComponentName _listenerComponent;
    private readonly MediaSessionManager? _sessionManager;
    private readonly object _lock = new();
    private readonly ControllerCallback _controllerCallback;

    private MediaController? _activeController;
    private string _sourceMode = "auto";
    private bool _includeBrowsers;
    private string[] _preferredSources = Array.Empty<string>();
    private bool _listenerRegistered;

    private TimeSpan _lastFrozenPosition = TimeSpan.Zero;
    private string? _lastTrackIdentity;
    private long _diagLastUpdateTime = -1;
    private long _diagLastReportedMs = -1;
    private string? _diagTrackKey;

    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool TimingDiagnostics { get; set; }

    public event Action<TrackInfo?>? TrackChanged;
    public event Action<bool>? IsPlayingChanged;
    public event Action? ArtworkChanged;

    public AndroidNowPlayingSource(Context context)
    {
        _context = context.ApplicationContext ?? context;
        _listenerComponent = new ComponentName(_context, Java.Lang.Class.FromType(typeof(MediaListenerService)));
        _sessionManager = _context.GetSystemService(Context.MediaSessionService) as MediaSessionManager;
        _controllerCallback = new ControllerCallback(this);
        MediaListenerService.ListenerConnectionChanged += OnListenerConnectionChanged;
    }

    public bool HasNotificationAccess()
    {
        try
        {
            var enabled = Settings.Secure.GetString(_context.ContentResolver, "enabled_notification_listeners");
            if (string.IsNullOrWhiteSpace(enabled)) return false;
            var flattened = _listenerComponent.FlattenToString();
            var shortName = _listenerComponent.FlattenToShortString();
            return enabled.Contains(flattened, StringComparison.OrdinalIgnoreCase)
                || enabled.Contains(shortName, StringComparison.OrdinalIgnoreCase)
                || enabled.Contains(_context.PackageName ?? "", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Start()
    {
        TryRegisterSessionsListener();
        RefreshSessions();
    }

    public void SetSource(string? mode, bool includeBrowsers, IEnumerable<string>? preferredSources = null)
    {
        lock (_lock)
        {
            _sourceMode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim();
            _includeBrowsers = includeBrowsers;
            _preferredSources = preferredSources?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToArray() ?? Array.Empty<string>();
        }
        RefreshSessions();
    }

    public IReadOnlyList<string> GetAvailableSources()
    {
        if (_sessionManager is null || !HasNotificationAccess()) return Array.Empty<string>();
        try
        {
            var sessions = _sessionManager.GetActiveSessions(_listenerComponent);
            return sessions
                .Select(c => c.PackageName)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public Bitmap? GetCurrentArtworkBitmap()
    {
        MediaController? controller;
        lock (_lock) controller = _activeController;
        var meta = controller?.Metadata;
        if (meta is null) return null;
        return meta.GetBitmap(MediaMetadata.MetadataKeyAlbumArt)
            ?? meta.GetBitmap(MediaMetadata.MetadataKeyArt);
    }

    public string? GetCurrentArtworkUri()
    {
        MediaController? controller;
        lock (_lock) controller = _activeController;
        var meta = controller?.Metadata;
        if (meta is null) return null;
        return meta.GetString(MediaMetadata.MetadataKeyAlbumArtUri)
            ?? meta.GetString(MediaMetadata.MetadataKeyArtUri);
    }

    public TimeSpan? GetEstimatedPosition()
    {
        MediaController? controller;
        lock (_lock) controller = _activeController;
        if (controller is null) return null;

        PlaybackState? state;
        try { state = controller.PlaybackState; }
        catch { return null; }
        if (state is null) return null;

        var reportedMs = Math.Max(0L, state.Position);
        var playing = state.State == PlaybackStateCode.Playing;
        if (!playing)
        {
            var frozen = TimeSpan.FromMilliseconds(reportedMs);
            _lastFrozenPosition = frozen;
            return frozen;
        }

        var updatedAt = state.LastPositionUpdateTime;
        var deltaMs = updatedAt > 0
            ? Math.Max(0L, SystemClock.ElapsedRealtime() - updatedAt)
            : 0L;
        if (deltaMs > 30 * 60 * 1000L) deltaMs = 0L; // 异常时间戳不做外推

        var speed = state.PlaybackSpeed > 0.05f ? state.PlaybackSpeed : 1.0f;
        var estimatedMs = reportedMs + (long)Math.Round(deltaMs * speed);
        if (CurrentTrack?.Duration is { } dur)
            estimatedMs = Math.Min(estimatedMs, (long)dur.TotalMilliseconds);

        var result = TimeSpan.FromMilliseconds(Math.Max(0L, estimatedMs));
        _lastFrozenPosition = result;

        if (TimingDiagnostics && (updatedAt != _diagLastUpdateTime || reportedMs != _diagLastReportedMs))
        {
            var tKey = CurrentTrack is { } t ? $"{t.Title}|{t.Artist}" : "(无曲目)";
            if (!string.Equals(tKey, _diagTrackKey, StringComparison.Ordinal))
            {
                AndroidLog.Write($"[timing] ---- 曲目切换 → {tKey} | 来源={CurrentTrack?.SourceAppId ?? "未知"} ----");
                _diagTrackKey = tKey;
            }
            AndroidLog.Write($"[timing] reported={reportedMs}ms stale={deltaMs}ms speed={speed:0.00} est={estimatedMs}ms");
            _diagLastUpdateTime = updatedAt;
            _diagLastReportedMs = reportedMs;
        }

        return result;
    }

    public PlaybackControls GetControls()
    {
        MediaController? controller;
        lock (_lock) controller = _activeController;
        var state = controller?.PlaybackState;
        if (state is null) return PlaybackControls.None;

        var actions = state.Actions;
        var canPrev = (actions & PlaybackState.ActionSkipToPrevious) != 0;
        var canNext = (actions & PlaybackState.ActionSkipToNext) != 0;
        var canPlayPause = (actions & (PlaybackState.ActionPlayPause | PlaybackState.ActionPlay | PlaybackState.ActionPause)) != 0;
        return new PlaybackControls(canPrev, canPlayPause, canNext);
    }

    public Task<bool> TogglePlayPauseAsync()
    {
        MediaController? controller;
        lock (_lock) controller = _activeController;
        if (controller is null) return Task.FromResult(false);

        try
        {
            var controls = controller.GetTransportControls();
            if (IsPlaying) controls.Pause();
            else controls.Play();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> SkipNextAsync()
    {
        MediaController? controller;
        lock (_lock) controller = _activeController;
        if (controller is null) return Task.FromResult(false);

        try
        {
            controller.GetTransportControls().SkipToNext();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> SkipPreviousAsync()
    {
        MediaController? controller;
        lock (_lock) controller = _activeController;
        if (controller is null) return Task.FromResult(false);

        try
        {
            controller.GetTransportControls().SkipToPrevious();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> SeekAsync(TimeSpan position)
    {
        MediaController? controller;
        lock (_lock) controller = _activeController;
        if (controller is null) return Task.FromResult(false);

        try
        {
            var targetMs = Math.Max(0L, (long)position.TotalMilliseconds);
            controller.GetTransportControls().SeekTo(targetMs);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public void OnActiveSessionsChanged(IList<MediaController>? controllers) =>
        AttachBestController(controllers);

    private void OnListenerConnectionChanged(bool connected)
    {
        if (!connected) return;
        TryRegisterSessionsListener();
        RefreshSessions();
    }

    private void TryRegisterSessionsListener()
    {
        if (_sessionManager is null || !HasNotificationAccess()) return;
        lock (_lock)
        {
            if (_listenerRegistered) return;
            try
            {
                _sessionManager.AddOnActiveSessionsChangedListener(this, _listenerComponent);
                _listenerRegistered = true;
            }
            catch (Exception ex)
            {
                AndroidLog.Write($"[media] 注册会话监听失败: {ex.Message}");
            }
        }
    }

    private void RefreshSessions()
    {
        if (_sessionManager is null || !HasNotificationAccess())
        {
            AttachBestController(null);
            return;
        }
        try
        {
            var sessions = _sessionManager.GetActiveSessions(_listenerComponent);
            AttachBestController(sessions);
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[media] 读取活跃会话失败: {ex.Message}");
        }
    }

    private void AttachBestController(IList<MediaController>? controllers)
    {
        var picked = PickBestController(controllers);
        bool changed;
        string? pickedPackage;

        lock (_lock)
        {
            if (SameController(_activeController, picked))
            {
                RefreshFromActiveController();
                return;
            }

            if (_activeController is not null)
            {
                try { _activeController.UnregisterCallback(_controllerCallback); } catch { }
            }

            _activeController = picked;
            if (_activeController is not null)
            {
                try { _activeController.RegisterCallback(_controllerCallback); } catch { }
            }

            changed = true;
            pickedPackage = _activeController?.PackageName;
        }

        if (changed)
            AndroidLog.Write($"[media] 选中会话: {pickedPackage ?? "(无)"} (模式={_sourceMode})");

        RefreshFromActiveController();
    }

    private MediaController? PickBestController(IList<MediaController>? controllers)
    {
        if (controllers is null || controllers.Count == 0) return null;

        if (!string.Equals(_sourceMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return controllers.FirstOrDefault(c =>
                string.Equals(c.PackageName, _sourceMode, StringComparison.OrdinalIgnoreCase));
        }

        bool Eligible(MediaController c)
        {
            var pkg = c.PackageName ?? "";
            if (_preferredSources.Length > 0)
                return _preferredSources.Any(p => string.Equals(p, pkg, StringComparison.OrdinalIgnoreCase));
            if (!_includeBrowsers && IsBrowserPackage(pkg)) return false;
            return _includeBrowsers || !IsVideoPackage(pkg);
        }

        MediaController? firstEligible = null;
        foreach (var controller in controllers)
        {
            if (!Eligible(controller)) continue;
            firstEligible ??= controller;
            if (controller.PlaybackState?.State == PlaybackStateCode.Playing)
                return controller;
        }
        return firstEligible;
    }

    private void RefreshFromActiveController()
    {
        MediaController? controller;
        lock (_lock) controller = _activeController;

        TrackInfo? nextTrack = null;
        var nextPlaying = false;

        if (controller is not null)
        {
            try
            {
                nextPlaying = controller.PlaybackState?.State == PlaybackStateCode.Playing;
                var meta = controller.Metadata;
                if (meta is not null)
                {
                    var rawTitle = meta.GetString(MediaMetadata.MetadataKeyTitle) ?? "";
                    var rawArtist = meta.GetString(MediaMetadata.MetadataKeyArtist)
                                    ?? meta.GetString(MediaMetadata.MetadataKeyAlbumArtist)
                                    ?? "";
                    var album = meta.GetString(MediaMetadata.MetadataKeyAlbum) ?? "";
                    var durationMs = meta.GetLong(MediaMetadata.MetadataKeyDuration);
                    var duration = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : (TimeSpan?)null;
                    var adFlag = meta.GetLong(AdSignals.AdvertisementMetadataKey);
                    var mediaId = meta.GetString(MediaMetadata.MetadataKeyMediaId);
                    var packageName = controller.PackageName ?? "";

                    if (!string.IsNullOrWhiteSpace(rawTitle)
                        && !AdSignals.LooksLikeAd(adFlag, mediaId, rawArtist, album))
                    {
                        string title;
                        string artist;
                        if (IsBrowserPackage(packageName) || IsVideoPackage(packageName))
                        {
                            SearchTermCleaner.ExtractVideoSongInfo(rawTitle, rawArtist, out title, out artist);
                        }
                        else
                        {
                            title = rawTitle.Trim();
                            artist = rawArtist.Trim();
                        }

                        nextTrack = new TrackInfo(title, artist, album.Trim(), duration, packageName);
                    }
                }
            }
            catch
            {
                // 会话可能已销毁
            }
        }

        var trackIdentity = nextTrack is null
            ? null
            : $"{nextTrack.SourceAppId}|{nextTrack.Title}|{nextTrack.Artist}";
        var trackChanged = !Equals(nextTrack, CurrentTrack);
        var identityChanged = !string.Equals(trackIdentity, _lastTrackIdentity, StringComparison.Ordinal);

        if (trackChanged)
        {
            CurrentTrack = nextTrack;
            _lastTrackIdentity = trackIdentity;
            TrackChanged?.Invoke(nextTrack);
        }

        if (identityChanged)
            ArtworkChanged?.Invoke();

        if (nextPlaying != IsPlaying)
        {
            IsPlaying = nextPlaying;
            IsPlayingChanged?.Invoke(nextPlaying);
        }
    }

    private static bool SameController(MediaController? a, MediaController? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return Equals(a.SessionToken, b.SessionToken);
    }

    private static bool IsBrowserPackage(string packageName) =>
        ContainsAnyToken(packageName, BrowserTokens);

    private static bool IsVideoPackage(string packageName) =>
        ContainsAnyToken(packageName, VideoAppTokens);

    private static bool ContainsAnyToken(string value, string[] tokens)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var token in tokens)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            MediaListenerService.ListenerConnectionChanged -= OnListenerConnectionChanged;
            lock (_lock)
            {
                if (_listenerRegistered && _sessionManager is not null)
                {
                    try { _sessionManager.RemoveOnActiveSessionsChangedListener(this); } catch { }
                    _listenerRegistered = false;
                }
                if (_activeController is not null)
                {
                    try { _activeController.UnregisterCallback(_controllerCallback); } catch { }
                    _activeController = null;
                }
            }
        }
        base.Dispose(disposing);
    }

    private sealed class ControllerCallback(AndroidNowPlayingSource owner) : MediaController.Callback
    {
        public override void OnMetadataChanged(MediaMetadata? metadata)
        {
            owner.RefreshFromActiveController();
            owner.ArtworkChanged?.Invoke();
        }

        public override void OnPlaybackStateChanged(PlaybackState? state) =>
            owner.RefreshFromActiveController();

        public override void OnSessionDestroyed() =>
            owner.RefreshSessions();
    }
}
