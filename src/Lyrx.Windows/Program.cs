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
        try
        {
            Run(args);
        }
        catch (Exception error)
        {
            // 启动期异常落在 WinExe 里是完全静默的：双击没反应、没有窗口、没有日志。
            // 至少留一份可查的痕迹。
            ReportStartupFailure(error);
        }
    }

    private static void Run(string[] args)
    {
        // 这两行必须在单实例判断之前：旧实例还开着时，新进程会被 ClaimInstance 直接劝退，
        // 日志里一个字都不留 —— 于是"明明编译了却没生效"，看起来像代码没改对。
        // 记下 pid、构建时间、自身路径，就能一眼确认当前跑的是哪一次编译、哪一个副本。
        var self = typeof(Program).Assembly.Location;
        var built = File.Exists(self) ? File.GetLastWriteTime(self) : DateTime.MinValue;
        Log.Write($"=== Lyrx 启动 pid={Environment.ProcessId} 构建={built:yyyy-MM-dd HH:mm:ss} "
            + $"路径={self} 参数={args.Length}项");

        // 日志落盘情况：成功就把真实路径再记一行；一个都没写成功就当面说清楚，
        // 不让"程序在跑、日志却是空的"再次变成查不下去的沉默故障。
        if (Log.ActivePath is { } logPath)
            Log.Write($"[boot] 日志文件={logPath}");

        // 开发时想同时开旧版本对比，用 Lyrx.exe --new-instance 跳过单实例检查。
        var newInstance = args.Any(a => string.Equals(a, "--new-instance", StringComparison.OrdinalIgnoreCase));
        if (!newInstance && !TaskbarCommands.ClaimInstance())
        {
            Log.Write("[boot] 已有实例在运行，本次启动退出（未进入程序体）");
            MessageBox.Show(
                "Lyrx 已经在运行了 —— 而且很可能还是旧版本，所以你怎么点都不会有新行为。\n\n"
                + "· 要用新编译的版本：系统托盘 Lyrx 图标右键 → 退出，然后重新启动。\n"
                + "· 只是想临时再开一个窗口对比：用 Lyrx.exe --new-instance 启动。\n\n"
                + $"本次启动的 exe 构建时间：{built:yyyy-MM-dd HH:mm:ss}\n路径：{self}",
                "Lyrx", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Startup += async (_, _) =>
        {
            var settings = AppSettings.Load();
            Loc.Initialize(settings.UiLanguage);
            var nowPlaying = await NowPlayingService.CreateAsync();
            // 补偿值语义已变：定位改由本机单调时钟主导，这里只剩"传输 + 音频管线"的几十毫秒。
            // 旧配置里那个 1400ms 是按锯齿均值凑出来的，直接沿用会让歌词整体提前 1.3 秒，故夹取到 0~400ms。
            // 补偿值按实测手感标定，默认 1400ms；这里只做一个宽区间保护，避免手改配置写出离谱值。
            var storedCompensationMs = settings.SmtcLatencyCompensationMs;
            var compensationMs = Math.Clamp(storedCompensationMs, 0, 3000);
            if (compensationMs != storedCompensationMs)
            {
                Log.Write($"[smtc] 补偿值 {storedCompensationMs}ms 超出合理区间，已收敛为 {compensationMs}ms");
                settings.SmtcLatencyCompensationMs = compensationMs;
                settings.Save();
            }
            nowPlaying.LatencyCompensation = TimeSpan.FromMilliseconds(compensationMs);

            var translationDb = Path.Combine(AppSettings.DataDirectory, "translations.db");
            var lyricsDb = Path.Combine(AppSettings.DataDirectory, "lyrics.db");
            var translationCache = new Musebase.Core.Translation.SqliteTranslationCache(translationDb);

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
                lyricsDb,
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
                        var displayName = NowPlayingService.FormatSourceDisplayName(id);
                        var item = new MenuItem
                        {
                            Header = NowPlayingService.IsBrowser(id) ? $"{displayName}  🌐" : displayName,
                            ToolTip = id,
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

            // 第二个实例（--new-instance）不去抢命令管道，否则会和正常实例互相打失败日志。
            if (!newInstance)
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

    /// <summary>
    /// 启动期异常在 WinExe 里是完全静默的（无窗口、无控制台、无日志）。这里同时落两处痕迹：
    /// %TEMP%\Lyrx-crash.log（一定能写）与 app.log（可能因为路径问题失败），再弹个窗，
    /// 免得"双击没反应"变成一个查不下去的黑洞。
    /// </summary>
    private static void ReportStartupFailure(Exception error)
    {
        var text = $"Lyrx 启动失败\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n"
            + $"类型：{error.GetType().FullName}\n消息：{error.Message}\n\n{error}";
        var path = Path.Combine(Path.GetTempPath(), "Lyrx-crash.log");
        try { File.WriteAllText(path, text); } catch { }
        try { Log.Write("[boot] 启动失败：\n" + error.ToString()); } catch { }
        try { MessageBox.Show(text + "\n\n已写入：" + path, "Lyrx 启动失败", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
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
    private static readonly object Sync = new();
    private static readonly List<string> Candidates = BuildCandidates();
    private static readonly Queue<string> Recent = new();
    private static string? _active;
    private static string? _failure;

    /// <summary>正在使用的日志文件；null = 一个都没写成功。</summary>
    public static string? ActivePath { get { lock (Sync) return _active; } }

    /// <summary>失败原因（首次失败时记录），用于在界面上直说"日志写不进去"。</summary>
    public static string? Failure { get { lock (Sync) return _failure; } }

    /// <summary>
    /// 日志落盘顺序：用户数据目录 →（仅非单文件版本）程序根目录 → 临时目录。
    /// 只写第一个成功的，之后都追加到它。
    /// </summary>
    private static List<string> BuildCandidates()
    {
        var list = new List<string>(3);
        try { list.Add(Path.Combine(AppSettings.DataDirectory, "app.log")); } catch { }
        var isSingleFile = string.IsNullOrEmpty(typeof(Program).Assembly.Location);
        if (!isSingleFile)
        {
            try { list.Add(Path.Combine(AppContext.BaseDirectory, "Lyrx.log")); } catch { }
        }
        try { list.Add(Path.Combine(Path.GetTempPath(), "Lyrx.log")); } catch { }
        return list;
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (Sync)
        {
            Recent.Enqueue(line);
            while (Recent.Count > 300) Recent.Dequeue();

            if (_active is not null && TryAppend(_active, line)) return;
            _active = null;
            foreach (var path in Candidates)
            {
                if (!TryAppend(path, line)) continue;
                _active = path;
                return;
            }
        }
    }

    private static bool TryAppend(string path, string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs, System.Text.Encoding.UTF8);
            sw.WriteLine(line);
            return true;
        }
        catch (Exception error)
        {
            _failure ??= $"{error.GetType().Name}: {error.Message}";
            return false;
        }
    }
}
