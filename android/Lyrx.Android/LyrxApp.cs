using Android.App;
using Android.Content;
using Android.Runtime;
using Musebase.Android.Services;
using Musebase.Core.Search;
using Musebase.Core.Translation;
using Musebase.Engine;

namespace Musebase.Android;

/// <summary>
/// Android 应用入口：初始化日志、设置、安全密钥存储、<see cref="AndroidNowPlayingSource"/>
/// 以及跨平台核心引擎 <see cref="LyricsCoordinator"/>。
/// </summary>
[Application(Label = "@string/app_name", Theme = "@style/LyrxTheme")]
public sealed class LyrxApp : Application
{
    public static LyrxApp Instance { get; private set; } = null!;

    public AndroidAppSettings Settings { get; private set; } = null!;
    public AndroidNowPlayingSource NowPlaying { get; private set; } = null!;
    public AndroidEngineDispatcher Dispatcher { get; private set; } = null!;
    public ITranslationCache TranslationCache { get; private set; } = null!;
    public LyricsCoordinator Coordinator { get; private set; } = null!;
    public string DataDirectory { get; private set; } = "";

    public LyrxApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;

        DataDirectory = FilesDir?.AbsolutePath
            ?? GetDir("lyrx", FileCreationMode.Private)?.AbsolutePath
            ?? global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.Personal);

        AndroidLog.Initialize(DataDirectory);
        AndroidLog.Write("[app] Lyrx Android starting");

        Settings = AndroidAppSettings.Load(DataDirectory);
        Dispatcher = new AndroidEngineDispatcher();
        NowPlaying = new AndroidNowPlayingSource(this)
        {
            TimingDiagnostics = Settings.TimingDiagnosticsEnabled,
        };
        NowPlaying.SetSource(Settings.PlaybackSource, Settings.IncludeBrowsers, Settings.PreferredSources);

        var translationsDb = Path.Combine(DataDirectory, "translations.db");
        TranslationCache = new SqliteTranslationCache(translationsDb);

        Coordinator = LyricsEngineFactory.Create(
            NowPlaying,
            Dispatcher,
            CurrentConfig(),
            TranslationCache,
            log: AndroidLog.Write);

        Coordinator.ManualOffsetSeconds = Settings.ManualOffsetSeconds;
        if (NowPlaying.CurrentTrack is { } initTrack)
            Coordinator.TrackOffsetSeconds = Settings.GetTrackOffset(TrackOffsetKey(initTrack));
        foreach (var key in Settings.SuppressedTracks)
            Coordinator.SuppressedTrackKeys.Add(key);

        Coordinator.SuppressedTracksChanged += () =>
        {
            Settings.SuppressedTracks = Coordinator.SuppressedTrackKeys.ToList();
            Settings.Save();
        };

        NowPlaying.Start();
        Coordinator.Start();
    }

    public EngineConfig CurrentConfig()
    {
        var cacheDb = Path.Combine(DataDirectory, "lyrics_cache.db");
        var options = new TranslatorOptions(
            DeeplApiKey: Settings.DeeplApiKey,
            LibreEndpoint: Settings.LibreTranslateEndpoint,
            LibreApiKey: Settings.LibreTranslateApiKey,
            GoogleApiKey: Settings.GoogleApiKey,
            OpenRouterApiKey: Settings.OpenRouterApiKey,
            OpenRouterModel: Settings.OpenRouterModel);

        return new EngineConfig(
            EnabledLyricsSources: Settings.EnabledLyricsSources,
            TranslationEngineId: Settings.EffectiveTranslationEngine,
            TranslatorOptions: options,
            TargetLanguage: Settings.EffectiveTargetLanguage,
            ShowOnlyTargetTranslation: Settings.ShowOnlyTargetTranslation,
            ManualOffsetSeconds: Settings.ManualOffsetSeconds,
            CacheDbPath: cacheDb,
            ApiTranslationEnabled: Settings.ApiTranslationEnabled);
    }

    public void EnsureNowPlayingStarted() => NowPlaying.Start();

    public void SaveSettings() => Settings.Save();

    public void SyncTranslationEngine()
    {
        LyricsEngineFactory.ApplyTranslation(Coordinator, CurrentConfig(), TranslationCache);
        Coordinator.TargetLanguage = Settings.EffectiveTargetLanguage;
        Coordinator.ShowOnlyTargetTranslation = Settings.ShowOnlyTargetTranslation;
        Coordinator.RefreshCurrentLine();
    }

    public void SyncLyricsSources()
    {
        Coordinator.SetSearchService(new LyricsSearchService(LyricsSourceRegistry.Build(Settings.EnabledLyricsSources)));
    }

    public string GetTrackStatusSummary()
    {
        var track = NowPlaying.CurrentTrack;
        var status = Coordinator.CurrentStatus;
        if (status is null)
            return track is null ? "等待音乐播放" : $"{track.Title} — {track.Artist}";
        return status.Kind switch
        {
            LyricsStatusKind.NoTrack => "等待音乐播放",
            LyricsStatusKind.HiddenByUser => $"已隐藏歌词：{status.Track}",
            LyricsStatusKind.Cache => $"来源：本地缓存 ({status.Service})",
            LyricsStatusKind.Searching => $"正在搜索：{status.Track}",
            LyricsStatusKind.Found => $"来源：{status.Service}" + (status.Quality is { } q ? $"（匹配度 {q:0.00}）" : ""),
            LyricsStatusKind.NotFound => $"未找到同步歌词：{status.Track}",
            LyricsStatusKind.Wrong => $"已标记为错误歌词：{status.Track}",
            LyricsStatusKind.Manual => $"手动选择：{status.Service}",
            LyricsStatusKind.Edited => $"用户已编辑：{status.Track}",
            _ => status.Track ?? "",
        };
    }

    public static string? TrackOffsetKey(TrackInfo? track) =>
        track is null ? null : LyricsCacheStore.MakeKey(track.Title, track.Artist);

    public void ApplyTrackOffsetFor(TrackInfo? track) =>
        Coordinator.TrackOffsetSeconds = Settings.GetTrackOffset(TrackOffsetKey(track));

    public void AdjustGlobalOffsetDelta(double? deltaSeconds)
    {
        var next = deltaSeconds is null
            ? 0
            : Math.Clamp(Math.Round(Settings.ManualOffsetSeconds + deltaSeconds.Value, 2), -60d, 60d);
        if (Math.Abs(next) < 0.005) next = 0;
        Settings.ManualOffsetSeconds = next;
        Coordinator.ManualOffsetSeconds = next;
        Coordinator.RefreshCurrentLine();
        Settings.Save();
    }

    public void AdjustTrackOffsetDelta(TrackInfo? track, double? deltaSeconds)
    {
        var key = TrackOffsetKey(track ?? NowPlaying.CurrentTrack);
        var next = deltaSeconds is null
            ? 0
            : Math.Clamp(Math.Round(Coordinator.TrackOffsetSeconds + deltaSeconds.Value, 2), -60d, 60d);
        if (Math.Abs(next) < 0.005) next = 0;
        Coordinator.TrackOffsetSeconds = next;
        Settings.SetTrackOffset(key, next);
        Settings.Save();
        Coordinator.RefreshCurrentLine();
    }

    public void AdjustOffset(double? newOffsetSeconds)
    {
        Settings.ManualOffsetSeconds = newOffsetSeconds is { } v ? Math.Round(v, 2) : 0;
        Coordinator.ManualOffsetSeconds = Settings.ManualOffsetSeconds;
        Coordinator.RefreshCurrentLine();
        Settings.Save();
    }

    public override void OnTerminate()
    {
        Coordinator?.Dispose();
        NowPlaying?.Dispose();
        base.OnTerminate();
    }
}
