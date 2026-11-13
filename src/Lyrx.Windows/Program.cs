using System.IO;
using System.Windows;
using System.Windows.Controls;
using Musebase.Windows.Services;
using Musebase.Engine;

namespace Musebase.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!TaskbarCommands.ClaimInstance()) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Startup += async (_, _) =>
        {
            var settings = AppSettings.Load();
            Loc.Initialize(settings.UiLanguage);
            var nowPlaying = await NowPlayingService.CreateAsync();

            var cacheDb = Path.Combine(AppSettings.DataDirectory, "translations.db");
            var translationCache = new Musebase.Core.Translation.SqliteTranslationCache(cacheDb);

            EngineConfig CurrentConfig() => new(
                settings.EnabledLyricsSources,
                settings.ClassicShowTranslation ? settings.EffectiveTranslationEngine : Musebase.Core.Translation.TranslatorRegistry.None,
                new Musebase.Core.Translation.TranslatorOptions(
                    DeeplApiKey: settings.DeeplApiKey,
                    LibreEndpoint: settings.LibreTranslateEndpoint,
                    LibreApiKey: settings.LibreTranslateApiKey,
                    GoogleApiKey: settings.GoogleApiKey,
                    OpenRouterApiKey: settings.OpenRouterApiKey,
                    OpenRouterModel: settings.OpenRouterModel),
                settings.EffectiveTargetLanguage,
                settings.ShowOnlyTargetTranslation,
                settings.ManualOffsetSeconds,
                cacheDb,
                TranslationFallbackEngineId: null,
                ApiTranslationEnabled: settings.ClassicShowTranslation && settings.ApiTranslationEnabled,
                LyricsServerEndpoint: null,
                LyricsServerToken: null);

            var coordinator = LyricsEngineFactory.Create(
                nowPlaying, new WpfEngineDispatcher(app.Dispatcher), CurrentConfig(), translationCache, Log.Write);

            foreach (var key in settings.SuppressedTracks) coordinator.SuppressedTrackKeys.Add(key);
            coordinator.SuppressedTracksChanged += () =>
            {
                settings.SuppressedTracks = coordinator.SuppressedTrackKeys.ToList();
                settings.Save();
            };

            nowPlaying.SetSource(settings.PlaybackSource, settings.IncludeBrowsers, settings.PreferredSources);

            var trackItem = new MenuItem { Header = Loc.T("status.noTrack"), IsEnabled = false };
            var sourceMenu = new MenuItem { Header = Loc.T("tray.source") };

            void ApplySource(string mode, bool includeBrowsers)
            {
                settings.PlaybackSource = mode;
                settings.IncludeBrowsers = includeBrowsers;
                settings.Save();
                nowPlaying.SetSource(mode, includeBrowsers, settings.PreferredSources);
            }

            void RebuildSourceMenu()
            {
                sourceMenu.Items.Clear();
                var mode = nowPlaying.SourceMode;

                var autoItem = new MenuItem
                {
                    Header = Loc.T("tray.source.auto"),
                    IsCheckable = true,
                    IsChecked = string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase),
                };
                autoItem.Click += (_, _) => ApplySource("auto", settings.IncludeBrowsers);
                sourceMenu.Items.Add(autoItem);

                var browsersItem = new MenuItem
                {
                    Header = Loc.T("tray.source.includeBrowsers"),
                    IsCheckable = true,
                    IsChecked = settings.IncludeBrowsers,
                };
                browsersItem.Click += (_, _) => ApplySource(nowPlaying.SourceMode, browsersItem.IsChecked);
                sourceMenu.Items.Add(browsersItem);

                sourceMenu.Items.Add(new Separator());

                var sources = nowPlaying.GetAvailableSources();
                if (sources.Count == 0)
                {
                    sourceMenu.Items.Add(new MenuItem { Header = Loc.T("tray.source.none"), IsEnabled = false });
                }
                else
                {
                    foreach (var id in sources)
                    {
                        var captured = id;
                        var item = new MenuItem
                        {
                            Header = NowPlayingService.IsBrowser(id) ? $"{id}  🌐" : id,
                            IsCheckable = true,
                            IsChecked = string.Equals(mode, id, StringComparison.OrdinalIgnoreCase),
                        };
                        item.Click += (_, _) => ApplySource(captured, settings.IncludeBrowsers);
                        sourceMenu.Items.Add(item);
                    }
                }
            }

            LyricsEditorWindow? editorWindow = null;
            ClassicSettingsWindow? settingsWindow = null;
            ClassicLyricsWindow? lyricsWindow = null;

            void OpenSearch() => new SearchWindow(coordinator).Show();
            void OpenLyricsEditor()
            {
                if (coordinator.CurrentLyrics is not { } lyrics || coordinator.CurrentTrack is not { } track) return;
                if (editorWindow is { IsLoaded: true })
                {
                    editorWindow.Activate();
                    return;
                }
                editorWindow = new LyricsEditorWindow(
                    track.ToString(), lyrics,
                    edited => coordinator.SaveEditedLyrics(track, edited));
                editorWindow.Show();
            }

            void ExportLrc()
            {
                if (coordinator.CurrentLyrics is not { } lyrics || coordinator.CurrentTrack is not { } track) return;
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = Loc.T("export.filter"),
                    DefaultExt = ".lrc",
                    FileName = SanitizeFileName($"{track.Artist} - {track.Title}") + ".lrc",
                };
                if (dialog.ShowDialog() != true) return;
                try
                {
                    var lrc = lyrics.ToLegacyString(coordinator.TargetLanguage?.ToLowerInvariant());
                    File.WriteAllText(dialog.FileName, lrc, new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Loc.T("export.fail", ("error", ex.Message)), "Lyrics", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            var offsetLabel = new MenuItem { IsEnabled = false };
            var offsetPlus = new MenuItem { Header = Loc.T("tray.offset.faster") };
            var offsetMinus = new MenuItem { Header = Loc.T("tray.offset.slower") };
            var offsetReset = new MenuItem { Header = Loc.T("tray.offset.reset") };

            void UpdateOffsetLabel() =>
                offsetLabel.Header = Loc.T("tray.offset.label", ("value", coordinator.ManualOffsetSeconds.ToString("+0.0;-0.0;0")));
            UpdateOffsetLabel();

            void AdjustOffset(double? delta)
            {
                coordinator.ManualOffsetSeconds = delta is { } d ? coordinator.ManualOffsetSeconds + d : 0;
                settings.ManualOffsetSeconds = coordinator.ManualOffsetSeconds;
                settings.Save();
                UpdateOffsetLabel();
                settingsWindow?.RefreshExternalState();
            }

            void SyncTranslationEngine()
            {
                LyricsEngineFactory.ApplyTranslation(coordinator, CurrentConfig(), translationCache);
                if (settings.ClassicShowTranslation)
                    _ = coordinator.RetranslateCurrentAsync();
            }

            static string LocalizeStatus(LyricsStatus s) => s.Kind switch
            {
                LyricsStatusKind.NoTrack => Loc.T("status.noTrack"),
                LyricsStatusKind.HiddenByUser => Loc.T("status.hidden.user", ("track", s.Track ?? "")),
                LyricsStatusKind.Cache => Loc.T("status.cache", ("track", s.Track ?? ""), ("service", s.Service ?? "")),
                LyricsStatusKind.Searching => Loc.T("status.searching", ("track", s.Track ?? "")),
                LyricsStatusKind.Found => Loc.T("status.found", ("track", s.Track ?? ""), ("service", s.Service ?? ""), ("quality", (s.Quality ?? 0).ToString("0.00"))),
                LyricsStatusKind.NotFound => Loc.T("status.notFound", ("track", s.Track ?? "")),
                LyricsStatusKind.Wrong => Loc.T("status.wrong", ("track", s.Track ?? "")),
                LyricsStatusKind.Manual => Loc.T("status.manual", ("track", s.Track ?? ""), ("service", s.Service ?? "")),
                LyricsStatusKind.Edited => Loc.T("status.edited", ("track", s.Track ?? "")),
                _ => "",
            };

            string CurrentTrackSummary() =>
                coordinator.CurrentStatus is { } st ? LocalizeStatus(st) : Loc.T("status.noTrack");

            void OpenSettings()
            {
                if (settingsWindow is { IsLoaded: true })
                {
                    settingsWindow.RefreshExternalState();
                    settingsWindow.Activate();
                    return;
                }
                settingsWindow = new ClassicSettingsWindow(
                    settings,
                    nowPlaying.GetAvailableSources(),
                    CurrentTrackSummary,
                    () => coordinator.ManualOffsetSeconds,
                    AdjustOffset,
                    () => coordinator.ReloadCurrentTrackLyrics(),
                    OpenSearch,
                    OpenLyricsEditor,
                    ExportLrc,
                    () => coordinator.MarkWrongLyrics(),
                    onChanged: () =>
                    {
                        nowPlaying.SetSource(settings.PlaybackSource, settings.IncludeBrowsers, settings.PreferredSources);
                        SyncTranslationEngine();
                        lyricsWindow?.ReloadLyricsDisplay();
                    });
                if (lyricsWindow is { IsLoaded: true }) settingsWindow.Owner = lyricsWindow;
                settingsWindow.Show();
            }

            void RestoreMainWindow()
            {
                if (lyricsWindow is null) return;
                if (!lyricsWindow.IsVisible) lyricsWindow.Show();
                if (lyricsWindow.WindowState == WindowState.Minimized)
                    lyricsWindow.WindowState = WindowState.Normal;
                lyricsWindow.Activate();
            }

            var showWindowItem = new MenuItem { Header = "显示 Lyrx 窗口", FontWeight = FontWeights.Bold };
            showWindowItem.Click += (_, _) => RestoreMainWindow();

            var searchItem = new MenuItem { Header = Loc.T("tray.search") };
            searchItem.Click += (_, _) => OpenSearch();

            var editItem = new MenuItem { Header = Loc.T("tray.edit") };
            editItem.Click += (_, _) => OpenLyricsEditor();

            var exportItem = new MenuItem { Header = Loc.T("tray.export") };
            exportItem.Click += (_, _) => ExportLrc();

            var wrongItem = new MenuItem { Header = Loc.T("tray.wrong") };
            wrongItem.Click += (_, _) => coordinator.MarkWrongLyrics();

            var settingsItem = new MenuItem { Header = "设置…" };
            settingsItem.Click += (_, _) => OpenSettings();

            var exitItem = new MenuItem { Header = "退出" };
            exitItem.Click += (_, _) => app.Shutdown();

            offsetPlus.Click += (_, _) => AdjustOffset(0.5);
            offsetMinus.Click += (_, _) => AdjustOffset(-0.5);
            offsetReset.Click += (_, _) => AdjustOffset(null);

            var menu = new ContextMenu();
            menu.Opened += (_, _) =>
            {
                var hasLyrics = coordinator.CurrentLyrics is not null && coordinator.CurrentTrack is not null;
                editItem.IsEnabled = hasLyrics;
                exportItem.IsEnabled = hasLyrics;
                wrongItem.IsEnabled = hasLyrics;
                RebuildSourceMenu();
            };
            menu.Items.Add(showWindowItem);
            menu.Items.Add(trackItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(sourceMenu);
            menu.Items.Add(searchItem);
            menu.Items.Add(editItem);
            menu.Items.Add(exportItem);
            menu.Items.Add(wrongItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(offsetLabel);
            menu.Items.Add(offsetPlus);
            menu.Items.Add(offsetMinus);
            menu.Items.Add(offsetReset);
            menu.Items.Add(new Separator());
            menu.Items.Add(settingsItem);
            menu.Items.Add(exitItem);

            var trayIcon = new TrayIcon("Lyrx", menu, RestoreMainWindow);

            coordinator.StatusChanged += status =>
            {
                var statusText = LocalizeStatus(status);
                trackItem.Header = new TextBlock
                {
                    Text = statusText,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360,
                };
                trayIcon.UpdateToolTip(string.IsNullOrWhiteSpace(statusText) ? "Lyrx" : $"Lyrx · {statusText}");
                settingsWindow?.RefreshExternalState();
            };

            lyricsWindow = new ClassicLyricsWindow(coordinator, nowPlaying, settings, AdjustOffset, OpenSettings, SyncTranslationEngine, menu);
            lyricsWindow.ViewStateChanged += () => settingsWindow?.RefreshExternalState();
            app.MainWindow = lyricsWindow;
            lyricsWindow.Show();

            TaskbarCommands.Listen(_ => app.Dispatcher.BeginInvoke(RestoreMainWindow));

            app.Exit += (_, _) =>
            {
                trayIcon.Dispose();
                coordinator.Dispose();
                nowPlaying.Dispose();
                settings.Save();
            };

            coordinator.Start();
        };
        app.Run();
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}

internal static class Log
{
    private static readonly string LogPath = InitPath();
    private static readonly object Sync = new();

    private static string InitPath() => Path.Combine(AppSettings.DataDirectory, "app.log");

    public static void Write(string message)
    {
        lock (Sync)
        {
            try
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}
