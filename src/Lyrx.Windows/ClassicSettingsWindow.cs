using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using Musebase.Core.Search;
using Musebase.Windows.Services;

namespace Musebase.Windows;

internal sealed class ClassicSettingsWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private static readonly Brush BgWindow = FrozenBrush(18, 18, 18);
    private static readonly Brush BgSidebar = FrozenBrush(14, 14, 14);
    private static readonly Brush BgCard = FrozenBrush(26, 26, 26);
    private static readonly Brush BgInput = FrozenBrush(38, 38, 38);
    private static readonly Brush BgInputHover = FrozenBrush(48, 48, 48);
    private static readonly Brush BgPopup = FrozenBrush(32, 32, 32);
    private static readonly Brush BgNavActive = FrozenBrush(40, 40, 40);
    private static readonly Brush BgNavHover = FrozenBrush(28, 28, 28);
    private static readonly Brush BorderSubtle = FrozenBrush(46, 46, 46);
    private static readonly Brush BorderPopup = FrozenBrush(64, 64, 64);
    private static readonly Brush TextPrimary = Brushes.White;
    private static readonly Brush TextMuted = FrozenBrush(167, 167, 167);
    private static readonly Brush TextDim = FrozenBrush(115, 115, 115);
    private static readonly Brush AccentGreen = FrozenBrush(30, 215, 96);
    private static readonly Brush SwitchOff = FrozenBrush(68, 68, 68);

    private static readonly (string Code, string Label)[] TargetLanguages =
    [
        ("ZH", "简体中文 (ZH)"),
        ("ZH-HANT", "繁體中文 (ZH-HANT)"),
        ("EN", "English (EN)"),
        ("JA", "日本語 (JA)"),
        ("KO", "韩语 (KO)"),
    ];

    private static readonly (string Id, string Label)[] TranslationEngines =
    [
        ("none", "仅使用歌词源自带翻译（推荐 / 免 Key）"),
        ("mymemory", "MyMemory（免费在线机器翻译）"),
        ("deepl", "DeepL API"),
        ("google", "Google Cloud Translation"),
        ("openrouter", "OpenRouter (AI 大模型翻译)"),
    ];

    private static readonly (string Id, string Title, string Subtitle)[] SourceDefinitions =
    [
        ("netease", "网易云音乐 (NetEase)", "官方时间轴歌词，内置优质中译"),
        ("qqmusic", "QQ 音乐 (QQMusic)", "官方时间轴歌词，内置双语翻译"),
        ("kugou", "酷狗音乐 (Kugou)", "海量华语与日韩曲库时间轴歌词"),
        ("lrclib", "LRCLIB (国际开源曲库)", "欧美与全球曲目同步歌词库"),
    ];

    private readonly Action _syncExternalUi;

    public ClassicSettingsWindow(
        AppSettings settings,
        IReadOnlyList<string> availablePlayers,
        Func<string> getTrackSummary,
        Func<double> getOffset,
        Action<double?> adjustOffset,
        Action reloadCurrentLyrics,
        Action openSearch,
        Action openEditor,
        Action exportLrc,
        Action markWrong,
        Action onChanged)
    {
        Title = "设置 · Lyrx";
        Icon = TrayIcon.LoadAppIcon();
        Width = 620;
        Height = 472;
        MinWidth = 560;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = BgWindow;
        Foreground = TextPrimary;
        FontFamily = ClassicLyricsFonts.Body;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var pref = DwmwcpRound;
                _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
            }
            catch { }
        };

        var outerBorder = new Border
        {
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = BgWindow,
            ClipToBounds = true,
        };

        var root = new DockPanel();
        outerBorder.Child = root;
        Content = outerBorder;

        // ---- 顶部标题栏（可拖动） ----
        var header = new DockPanel();
        var headerBorder = new Border
        {
            Height = 48,
            Padding = new Thickness(20, 0, 12, 0),
            Background = BgSidebar,
            Child = header,
        };
        headerBorder.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        };

        var closeBtn = new Button
        {
            Content = new ClassicTransportGlyph(ClassicTransportIcon.Close),
            Width = 32,
            Height = 32,
            Foreground = TextMuted,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "关闭 (Esc)",
            Template = FlatIconButtonTemplate(),
        };
        ((ClassicTransportGlyph)closeBtn.Content).SetBinding(
            ClassicTransportGlyph.TintProperty,
            new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = closeBtn });
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        header.Children.Add(closeBtn);

        var autoSaveHint = new TextBlock
        {
            Text = "所有修改实时生效",
            FontSize = 11.5,
            Foreground = TextDim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        DockPanel.SetDock(autoSaveHint, Dock.Right);
        header.Children.Add(autoSaveHint);

        var headerTitle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerTitle.Children.Add(new ClassicTransportGlyph(ClassicTransportIcon.Settings)
        {
            Tint = AccentGreen,
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 8, 0),
        });
        headerTitle.Children.Add(new TextBlock
        {
            Text = "设置",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(headerTitle);

        DockPanel.SetDock(headerBorder, Dock.Top);
        root.Children.Add(headerBorder);
        var topDivider = new Border
        {
            Height = 1,
            Background = BorderSubtle,
        };
        DockPanel.SetDock(topDivider, Dock.Top);
        root.Children.Add(topDivider);

        // ---- 主体：左侧导航 + 右侧内容页 ----
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(154) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(mainGrid);

        var navPanel = new StackPanel
        {
            Background = BgSidebar,
            Margin = new Thickness(0),
        };
        var navInner = new StackPanel { Margin = new Thickness(10, 12, 10, 12) };
        navPanel.Children.Add(navInner);
        Grid.SetColumn(navPanel, 0);
        mainGrid.Children.Add(navPanel);

        var divider = new Border { Background = BorderSubtle };
        Grid.SetColumn(divider, 1);
        mainGrid.Children.Add(divider);

        var pageScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18, 14, 18, 16),
            Resources = { [typeof(ScrollBar)] = CreateDarkScrollBarStyle() },
        };
        var pageHost = new Grid();
        pageScroll.Content = pageHost;
        Grid.SetColumn(pageScroll, 2);
        mainGrid.Children.Add(pageScroll);

        var suppressingEvents = false;
        void SaveAndNotify()
        {
            if (suppressingEvents) return;
            settings.Save();
            onChanged();
        }

        // =====================================================================
        // 分页 1：播放与窗口
        // =====================================================================
        var pageGeneral = new StackPanel();

        var sourceCard = AddSectionCard(pageGeneral, "播放源（音频适配）");
        sourceCard.Children.Add(HintLabel("选择要跟随显示歌词的音乐播放器（基于系统 SMTC 媒体会话）："));

        var playerModes = new List<string> { "auto" };
        var playerLabels = new List<string> { "自动检测正在播放的音乐应用（推荐）" };

        var allSources = availablePlayers
            .Concat(string.Equals(settings.PlaybackSource, "auto", StringComparison.OrdinalIgnoreCase)
                ? []
                : [settings.PlaybackSource])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var id in allSources)
        {
            playerModes.Add(id);
            var running = availablePlayers.Contains(id, StringComparer.OrdinalIgnoreCase);
            playerLabels.Add(running ? $"锁定：{id}（正在运行）" : $"锁定：{id}（当前未运行）");
        }

        var selectedPlayerIdx = Math.Max(0, playerModes.FindIndex(
            m => string.Equals(m, settings.PlaybackSource, StringComparison.OrdinalIgnoreCase)));
        var (playerDropdown, _) = CreateDarkDropdown(playerLabels, selectedPlayerIdx, idx =>
        {
            settings.PlaybackSource = playerModes[Math.Clamp(idx, 0, playerModes.Count - 1)];
            SaveAndNotify();
        });
        playerDropdown.Margin = new Thickness(0, 2, 0, 10);
        sourceCard.Children.Add(playerDropdown);

        var browserToggle = CreateToggleSwitch(settings.IncludeBrowsers, val =>
        {
            settings.IncludeBrowsers = val;
            SaveAndNotify();
        });
        sourceCard.Children.Add(CreateSettingRow(
            "包含浏览器音频",
            "识别网页版 Spotify、YouTube Music、Bilibili 等浏览器媒体",
            browserToggle));

        var windowCard = AddSectionCard(pageGeneral, "窗口与外观配色");
        var topmostToggle = CreateToggleSwitch(settings.ClassicTopmost, val =>
        {
            settings.ClassicTopmost = val;
            SaveAndNotify();
        });
        windowCard.Children.Add(CreateSettingRow(
            "窗口始终置顶",
            "将歌词窗口固定在其他窗口最前方，适合小窗模式",
            topmostToggle));

        // 行距：英文与韩文的字形比汉字更需要行间留白，给一个档位而不是写死一套参数
        var spacingIds = new[] { "compact", "normal", "loose" };
        var spacingLabels = new List<string> { "紧凑", "标准", "宽松" };
        var spacingIdx = Math.Max(0, Array.FindIndex(spacingIds,
            id => string.Equals(id, settings.LyricsLineSpacing, StringComparison.OrdinalIgnoreCase)));
        var (spacingDropdown, _) = CreateDarkDropdown(spacingLabels, spacingIdx, idx =>
        {
            settings.LyricsLineSpacing = spacingIds[Math.Clamp(idx, 0, spacingIds.Length - 1)];
            SaveAndNotify();
        }, compact: true);
        windowCard.Children.Add(CreateSettingRow("歌词行距", null, spacingDropdown));

        windowCard.Children.Add(CreateDivider());
        windowCard.Children.Add(FieldLabel("背景配色模式："));

        var presets = ClassicLyricsPalette.ThemePresets;
        var presetLabels = presets.Select(p => p.Label).ToList();
        var currentThemeIdx = Math.Max(0, presets
            .Select((p, idx) => (p, idx))
            .FirstOrDefault(x => string.Equals(x.p.Id, settings.ClassicBackgroundMode, StringComparison.OrdinalIgnoreCase)).idx);

        var swatchBorders = new List<(string Id, Border Frame)>();
        Action<int>? setThemeDropdownIndex = null;

        void SelectThemeByIndex(int index, bool notify)
        {
            var clamped = Math.Clamp(index, 0, presets.Count - 1);
            var chosen = presets[clamped];
            settings.ClassicBackgroundMode = chosen.Id;
            setThemeDropdownIndex?.Invoke(clamped);
            foreach (var (id, frame) in swatchBorders)
            {
                var active = string.Equals(id, chosen.Id, StringComparison.OrdinalIgnoreCase);
                frame.BorderBrush = active ? AccentGreen : BorderSubtle;
                frame.Background = active ? BgNavActive : BgInput;
            }
            if (notify) SaveAndNotify();
        }

        var (themeDropdown, updateThemeDropdown) = CreateDarkDropdown(presetLabels, currentThemeIdx, idx =>
            SelectThemeByIndex(idx, notify: true));
        setThemeDropdownIndex = updateThemeDropdown;
        themeDropdown.Margin = new Thickness(0, 4, 0, 8);
        windowCard.Children.Add(themeDropdown);

        var swatchWrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        for (var i = 0; i < presets.Count; i++)
        {
            var capturedIndex = i;
            var preset = presets[i];
            var chipRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var dotGrid = new Grid { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) };
            dotGrid.Children.Add(new Ellipse
            {
                Fill = new SolidColorBrush(preset.Background),
                Stroke = new SolidColorBrush(preset.Subdued),
                StrokeThickness = 1.5,
            });
            if (preset.IsDynamic)
            {
                dotGrid.Children.Add(new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = AccentGreen,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            chipRow.Children.Add(dotGrid);

            var shortName = preset.Label.Split('（')[0];
            chipRow.Children.Add(new TextBlock
            {
                Text = shortName,
                FontSize = 11.5,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var chipBorder = new Border
            {
                Child = chipRow,
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1.2),
                Padding = new Thickness(9, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                ToolTip = preset.Label,
            };
            chipBorder.MouseLeftButtonUp += (_, _) => SelectThemeByIndex(capturedIndex, notify: true);
            swatchBorders.Add((preset.Id, chipBorder));
            swatchWrap.Children.Add(chipBorder);
        }
        windowCard.Children.Add(swatchWrap);
        SelectThemeByIndex(currentThemeIdx, notify: false);

        // =====================================================================
        // 分页 2：歌词搜索源
        // =====================================================================
        var pageSources = new StackPanel { Visibility = Visibility.Collapsed };
        var providerCard = AddSectionCard(pageSources, "在线歌词搜索源");
        providerCard.Children.Add(HintLabel("启用后会并行检索以下歌词库，并自动挑选时间轴最准、包含翻译的最佳版本："));

        var sourceChecks = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        var enabledSet = new HashSet<string>(settings.EnabledLyricsSources, StringComparer.OrdinalIgnoreCase);

        void SyncEnabledSources()
        {
            var selected = sourceChecks
                .Where(kv => kv.Value.IsChecked == true)
                .Select(kv => kv.Key)
                .ToList();
            if (selected.Count == 0)
            {
                selected.Add("netease");
                if (sourceChecks.TryGetValue("netease", out var neteaseBox))
                {
                    suppressingEvents = true;
                    neteaseBox.IsChecked = true;
                    suppressingEvents = false;
                }
            }
            settings.EnabledLyricsSources = selected;
            SaveAndNotify();
        }

        var firstSource = true;
        foreach (var (id, title, subtitle) in SourceDefinitions)
        {
            if (!LyricsSourceRegistry.All.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!firstSource) providerCard.Children.Add(CreateDivider());
            firstSource = false;

            var toggle = CreateToggleSwitch(enabledSet.Contains(id), _ => SyncEnabledSources());
            sourceChecks[id] = toggle;
            providerCard.Children.Add(CreateSettingRow(title, subtitle, toggle));
        }

        var cacheCard = AddSectionCard(pageSources, "歌词缓存与刷新");
        var refreshHint = HintLabel("如果某首歌的本地缓存歌词不准确，或刚开启新的歌词源，可立即重新在线匹配：");
        cacheCard.Children.Add(refreshHint);
        var reloadLyricsBtn = ActionPillButton("重新获取当前歌曲歌词（忽略缓存）", primary: false);
        reloadLyricsBtn.HorizontalAlignment = HorizontalAlignment.Left;
        reloadLyricsBtn.Click += (_, _) =>
        {
            reloadCurrentLyrics();
            reloadLyricsBtn.Content = "✓ 已触发重新检索";
        };
        cacheCard.Children.Add(reloadLyricsBtn);

        // =====================================================================
        // 分页 3：双语与翻译
        // =====================================================================
        var pageTranslation = new StackPanel { Visibility = Visibility.Collapsed };
        var transCard = AddSectionCard(pageTranslation, "双语歌词显示");

        var showTransToggle = CreateToggleSwitch(settings.ClassicShowTranslation, val =>
        {
            settings.ClassicShowTranslation = val;
            SaveAndNotify();
        });
        transCard.Children.Add(CreateSettingRow(
            "显示双语翻译（文A）",
            "在原歌词下方展开显示目标语言译文，与主界面「文A」按钮同步",
            showTransToggle));

        transCard.Children.Add(CreateDivider());

        var onlyTargetToggle = CreateToggleSwitch(settings.ShowOnlyTargetTranslation, val =>
        {
            settings.ShowOnlyTargetTranslation = val;
            SaveAndNotify();
        });
        transCard.Children.Add(CreateSettingRow(
            "仅显示目标语言翻译",
            "隐藏非当前目标语种的其他附带译文",
            onlyTargetToggle));

        transCard.Children.Add(CreateDivider());

        var hideSameToggle = CreateToggleSwitch(settings.HideSameTranslation, val =>
        {
            settings.HideSameTranslation = val;
            SaveAndNotify();
        });
        transCard.Children.Add(CreateSettingRow(
            "译文与原文相同时自动隐藏",
            "避免纯中文歌曲或重复行出现冗余双行",
            hideSameToggle));

        var engineCard = AddSectionCard(pageTranslation, "语言与在线翻译引擎");
        engineCard.Children.Add(FieldLabel("目标翻译语言："));

        var langLabels = TargetLanguages.Select(t => t.Label).ToList();
        var langIdx = Math.Max(0, Array.FindIndex(TargetLanguages,
            t => string.Equals(t.Code, settings.EffectiveTargetLanguage, StringComparison.OrdinalIgnoreCase)));
        var (langDropdown, _) = CreateDarkDropdown(langLabels, langIdx, idx =>
        {
            settings.TargetLanguage = TargetLanguages[Math.Clamp(idx, 0, TargetLanguages.Length - 1)].Code;
            SaveAndNotify();
        });
        langDropdown.Margin = new Thickness(0, 4, 0, 10);
        engineCard.Children.Add(langDropdown);

        engineCard.Children.Add(FieldLabel("在线机器翻译补充（当歌词源无自带翻译时使用）："));
        var engineLabels = TranslationEngines.Select(e => e.Label).ToList();
        var currentEngine = settings.ApiTranslationEnabled ? settings.EffectiveTranslationEngine : "none";
        var engineIdx = Math.Max(0, Array.FindIndex(TranslationEngines,
            e => string.Equals(e.Id, currentEngine, StringComparison.OrdinalIgnoreCase)));

        var apiKeyPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        var apiKeyLabel = HintLabel("API 密钥：");
        var apiKeyBox = new PasswordBox
        {
            Height = 32,
            Padding = new Thickness(10, 5, 10, 5),
            Background = BgInput,
            Foreground = TextPrimary,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CaretBrush = TextPrimary,
        };
        apiKeyPanel.Children.Add(apiKeyLabel);
        apiKeyPanel.Children.Add(apiKeyBox);

        void SaveCurrentApiKey(string engineId)
        {
            var key = string.IsNullOrWhiteSpace(apiKeyBox.Password) ? null : apiKeyBox.Password.Trim();
            if (engineId == "deepl") settings.DeeplApiKey = key;
            else if (engineId == "google") settings.GoogleApiKey = key;
            else if (engineId == "openrouter") settings.OpenRouterApiKey = key;
            SaveAndNotify();
        }

        var selectedEngineId = TranslationEngines[engineIdx].Id;
        apiKeyBox.LostFocus += (_, _) => SaveCurrentApiKey(selectedEngineId);

        void SyncEngineSelection(int idx, bool notify)
        {
            selectedEngineId = TranslationEngines[Math.Clamp(idx, 0, TranslationEngines.Length - 1)].Id;
            if (selectedEngineId == "none")
            {
                settings.ApiTranslationEnabled = false;
                settings.TranslationEngine = "none";
                apiKeyPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                settings.ApiTranslationEnabled = true;
                settings.TranslationEngine = selectedEngineId;
                if (selectedEngineId is "deepl" or "google" or "openrouter")
                {
                    apiKeyPanel.Visibility = Visibility.Visible;
                    apiKeyLabel.Text = selectedEngineId switch
                    {
                        "deepl" => "DeepL API Key（输入后自动加密保存）：",
                        "google" => "Google Cloud Translation API Key：",
                        _ => "OpenRouter API Key：",
                    };
                    apiKeyBox.Password = selectedEngineId switch
                    {
                        "deepl" => settings.DeeplApiKey ?? "",
                        "google" => settings.GoogleApiKey ?? "",
                        _ => settings.OpenRouterApiKey ?? "",
                    };
                }
                else
                {
                    apiKeyPanel.Visibility = Visibility.Collapsed;
                }
            }
            if (notify) SaveAndNotify();
        }

        var (engineDropdown, _) = CreateDarkDropdown(engineLabels, engineIdx, idx =>
            SyncEngineSelection(idx, notify: true));
        engineDropdown.Margin = new Thickness(0, 4, 0, 0);
        engineCard.Children.Add(engineDropdown);
        engineCard.Children.Add(apiKeyPanel);
        SyncEngineSelection(engineIdx, notify: false);

        // =====================================================================
        // 分页 4：当前歌曲工具
        // =====================================================================
        var pageTools = new StackPanel { Visibility = Visibility.Collapsed };

        var statusCard = AddSectionCard(pageTools, "当前播放与匹配状态");
        var trackStatusText = new TextBlock
        {
            Foreground = TextPrimary,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
        };
        statusCard.Children.Add(trackStatusText);

        var offsetCard = AddSectionCard(pageTools, "歌词时间轴同步微调");
        offsetCard.Children.Add(HintLabel("当歌词比人声偏快或偏慢时，可微调同步点（支持 0.2 秒精细调节）："));

        var offsetValueLabel = new TextBlock
        {
            Foreground = AccentGreen,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        void RefreshOffsetText() =>
            offsetValueLabel.Text = $"当前手动偏移：{getOffset():+0.0;-0.0;0.0} 秒";

        var offsetRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 2) };
        offsetRow.Children.Add(offsetValueLabel);
        var minusFastBtn = SmallToolButton("延后 0.5s");
        minusFastBtn.Click += (_, _) => { adjustOffset(-0.5); RefreshOffsetText(); };
        var minusBtn = SmallToolButton("延后 0.2s");
        minusBtn.Click += (_, _) => { adjustOffset(-0.2); RefreshOffsetText(); };
        var resetBtn = SmallToolButton("重置");
        resetBtn.Click += (_, _) => { adjustOffset(null); RefreshOffsetText(); };
        var plusBtn = SmallToolButton("提前 0.2s");
        plusBtn.Click += (_, _) => { adjustOffset(0.2); RefreshOffsetText(); };
        var plusFastBtn = SmallToolButton("提前 0.5s");
        plusFastBtn.Click += (_, _) => { adjustOffset(0.5); RefreshOffsetText(); };
        offsetRow.Children.Add(minusFastBtn);
        offsetRow.Children.Add(minusBtn);
        offsetRow.Children.Add(resetBtn);
        offsetRow.Children.Add(plusBtn);
        offsetRow.Children.Add(plusFastBtn);
        offsetCard.Children.Add(offsetRow);

        var actionsCard = AddSectionCard(pageTools, "歌词管理操作");
        actionsCard.Children.Add(HintLabel("针对当前正在播放歌曲的手动干预工具："));
        var actionsGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };

        var searchBtn = ActionTileButton("🔍 手动搜索 / 更换歌词…", () => openSearch());
        var editBtn = ActionTileButton("✏️ 编辑当前歌词内容…", () => openEditor());
        var exportBtn = ActionTileButton("💾 导出为 .lrc 歌词文件…", () => exportLrc());
        var wrongBtn = ActionTileButton("🚫 标记当前歌词有误", () =>
        {
            markWrong();
            trackStatusText.Text = getTrackSummary();
        });
        actionsGrid.Children.Add(searchBtn);
        actionsGrid.Children.Add(editBtn);
        actionsGrid.Children.Add(exportBtn);
        actionsGrid.Children.Add(wrongBtn);
        actionsCard.Children.Add(actionsGrid);

        pageHost.Children.Add(pageGeneral);
        pageHost.Children.Add(pageSources);
        pageHost.Children.Add(pageTranslation);
        pageHost.Children.Add(pageTools);

        // ---- 左侧导航按钮切换 ----
        var pages = new (string Label, StackPanel Page)[]
        {
            ("播放与窗口", pageGeneral),
            ("歌词搜索源", pageSources),
            ("双语与翻译", pageTranslation),
            ("当前歌曲", pageTools),
        };
        var navButtons = new List<(Border Container, Border Indicator, TextBlock Label)>();

        _syncExternalUi = () =>
        {
            suppressingEvents = true;
            showTransToggle.IsChecked = settings.ClassicShowTranslation;
            topmostToggle.IsChecked = settings.ClassicTopmost;
            trackStatusText.Text = getTrackSummary();
            RefreshOffsetText();
            suppressingEvents = false;
        };

        void SelectTab(int index)
        {
            for (var i = 0; i < pages.Length; i++)
            {
                var active = i == index;
                pages[i].Page.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                var (container, indicator, label) = navButtons[i];
                container.Background = active ? BgNavActive : Brushes.Transparent;
                indicator.Visibility = active ? Visibility.Visible : Visibility.Hidden;
                label.Foreground = active ? TextPrimary : TextMuted;
                label.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            }
            pageScroll.ScrollToTop();
            _syncExternalUi();
        }

        for (var i = 0; i < pages.Length; i++)
        {
            var captured = i;
            var rowGrid = new Grid { Height = 38 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var indicator = new Border
            {
                Width = 3,
                Height = 18,
                CornerRadius = new CornerRadius(1.5),
                Background = AccentGreen,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(indicator, 0);
            rowGrid.Children.Add(indicator);

            var label = new TextBlock
            {
                Text = pages[i].Label,
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 8, 0),
            };
            Grid.SetColumn(label, 1);
            rowGrid.Children.Add(label);

            var navBorder = new Border
            {
                Child = rowGrid,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            navBorder.MouseEnter += (_, _) =>
            {
                if (pages[captured].Page.Visibility != Visibility.Visible)
                    navBorder.Background = BgNavHover;
            };
            navBorder.MouseLeave += (_, _) =>
            {
                if (pages[captured].Page.Visibility != Visibility.Visible)
                    navBorder.Background = Brushes.Transparent;
            };
            navBorder.MouseLeftButtonUp += (_, _) => SelectTab(captured);

            navButtons.Add((navBorder, indicator, label));
            navInner.Children.Add(navBorder);
        }

        SelectTab(0);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    public void RefreshExternalState() => _syncExternalUi();

    private static StackPanel AddSectionCard(StackPanel host, string title)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            Margin = new Thickness(0, 0, 0, 8),
        });
        host.Children.Add(new Border
        {
            Background = BgCard,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = panel,
        });
        return panel;
    }

    private static Border CreateDivider() => new()
    {
        Height = 1,
        Background = BorderSubtle,
        Margin = new Thickness(0, 8, 0, 8),
    };

    private static DockPanel CreateSettingRow(string title, string? subtitle, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11.5,
                Foreground = TextMuted,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        row.Children.Add(textStack);
        return row;
    }

    private static CheckBox CreateToggleSwitch(bool isChecked, Action<bool> onChanged)
    {
        var track = new FrameworkElementFactory(typeof(Border), "Track");
        track.SetValue(Border.WidthProperty, 40d);
        track.SetValue(Border.HeightProperty, 22d);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        track.SetValue(Border.BackgroundProperty, SwitchOff);

        var thumb = new FrameworkElementFactory(typeof(Ellipse), "Thumb");
        thumb.SetValue(FrameworkElement.WidthProperty, 16d);
        thumb.SetValue(FrameworkElement.HeightProperty, 16d);
        thumb.SetValue(Shape.FillProperty, Brushes.White);
        thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        thumb.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 0, 0));
        track.AppendChild(thumb);

        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = track };
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, AccentGreen, "Track"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "Thumb"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 3, 0), "Thumb"));
        template.Triggers.Add(checkedTrigger);

        var box = new CheckBox
        {
            IsChecked = isChecked,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = template,
        };
        box.Checked += (_, _) => onChanged(true);
        box.Unchecked += (_, _) => onChanged(false);
        return box;
    }

    private static (FrameworkElement Control, Action<int> SetSelectedIndex) CreateDarkDropdown(
        IReadOnlyList<string> items,
        int selectedIndex,
        Action<int> onSelected,
        bool compact = false)
    {
        var currentIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, items.Count - 1));

        var selectedText = new TextBlock
        {
            Text = items.Count > 0 ? items[currentIndex] : "",
            Foreground = TextPrimary,
            FontSize = compact ? 12 : 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var chevron = new Path
        {
            Data = Geometry.Parse("M 0,0 L 5,5 L 10,0"),
            Stroke = TextMuted,
            StrokeThickness = 1.6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(compact ? 6 : 8, 0, 0, 0),
        };

        var boxContent = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        boxContent.Children.Add(chevron);
        boxContent.Children.Add(selectedText);

        var boxBorder = new Border
        {
            Background = BgInput,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = compact ? new Thickness(9, 4, 9, 4) : new Thickness(11, 7, 11, 7),
            Cursor = Cursors.Hand,
            Child = boxContent,
        };

        var popupStack = new StackPanel();
        var popupBorder = new Border
        {
            Background = BgPopup,
            BorderBrush = BorderPopup,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = popupStack,
        };

        var popup = new Popup
        {
            PlacementTarget = boxBorder,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = popupBorder,
        };

        var itemVisuals = new List<(Border Row, TextBlock Text, TextBlock Check)>();

        void UpdateSelectionVisuals(int idx)
        {
            currentIndex = Math.Clamp(idx, 0, Math.Max(0, items.Count - 1));
            selectedText.Text = items.Count > 0 ? items[currentIndex] : "";
            for (var i = 0; i < itemVisuals.Count; i++)
            {
                var active = i == currentIndex;
                itemVisuals[i].Text.Foreground = active ? AccentGreen : TextPrimary;
                itemVisuals[i].Text.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
                itemVisuals[i].Check.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            var captured = i;
            var itemRow = new DockPanel();
            var checkMark = new TextBlock
            {
                Text = "✓",
                Foreground = AccentGreen,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 2, 0),
            };
            DockPanel.SetDock(checkMark, Dock.Right);
            itemRow.Children.Add(checkMark);

            var itemLabel = new TextBlock
            {
                Text = items[i],
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            itemRow.Children.Add(itemLabel);

            var itemBorder = new Border
            {
                Child = itemRow,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            itemBorder.MouseEnter += (_, _) => itemBorder.Background = BgInputHover;
            itemBorder.MouseLeave += (_, _) => itemBorder.Background = Brushes.Transparent;
            itemBorder.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                popup.IsOpen = false;
                if (currentIndex != captured)
                {
                    UpdateSelectionVisuals(captured);
                    onSelected(captured);
                }
            };

            itemVisuals.Add((itemBorder, itemLabel, checkMark));
            popupStack.Children.Add(itemBorder);
        }

        UpdateSelectionVisuals(currentIndex);

        boxBorder.MouseEnter += (_, _) =>
        {
            if (!popup.IsOpen) boxBorder.Background = BgInputHover;
        };
        boxBorder.MouseLeave += (_, _) =>
        {
            if (!popup.IsOpen) boxBorder.Background = BgInput;
        };

        // 打开时机是关键：原来在 MouseLeftButtonDown 里置 IsOpen=true，
        // 而 Popup 的 StaysOpen=false 会把握住鼠标、把随后的"松开"判成窗外点击，
        // 于是展开后立刻又收回（就是"点开就闪回去"那个 bug）。
        // 改成松开时才打开；同时记下关闭时刻，避免"点外面关闭"和"再点一次打开"互相打架。
        var lastClosedTick = 0L;
        boxBorder.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (popup.IsOpen)
            {
                popup.IsOpen = false;
                return;
            }
            if (Environment.TickCount64 - lastClosedTick < 250) return; // 刚被"点外面"关掉，别马上重开
            popupBorder.Width = Math.Max(compact ? 140 : 180, boxBorder.ActualWidth);
            popup.IsOpen = true;
        };
        popup.Opened += (_, _) => boxBorder.BorderBrush = AccentGreen;
        popup.Closed += (_, _) =>
        {
            lastClosedTick = Environment.TickCount64;
            boxBorder.BorderBrush = BorderSubtle;
            boxBorder.Background = boxBorder.IsMouseOver ? BgInputHover : BgInput;
        };

        return (boxBorder, UpdateSelectionVisuals);
    }

    private static TextBlock HintLabel(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = TextMuted,
        Margin = new Thickness(0, 0, 0, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = TextMuted,
        Margin = new Thickness(0, 2, 0, 2),
    };

    private static Button SmallToolButton(string label)
    {
        var btn = new Button
        {
            Content = label,
            Height = 28,
            Padding = new Thickness(11, 0, 11, 0),
            Margin = new Thickness(0, 0, 6, 4),
            Background = BgInput,
            Foreground = TextPrimary,
            FontSize = 12,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = PillButtonTemplate(cornerRadius: 6, hoverBg: BgInputHover),
        };
        return btn;
    }

    private static Button ActionTileButton(string label, Action onClick)
    {
        var btn = new Button
        {
            Content = label,
            Height = 36,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 8, 8),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = BgInput,
            Foreground = TextPrimary,
            FontSize = 12.5,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = PillButtonTemplate(cornerRadius: 6, hoverBg: BgInputHover),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Button ActionPillButton(string label, bool primary)
    {
        var btn = new Button
        {
            Content = label,
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0),
            Background = primary ? AccentGreen : BgInput,
            Foreground = primary ? Brushes.Black : TextPrimary,
            FontWeight = primary ? FontWeights.Bold : FontWeights.Normal,
            FontSize = 12.5,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = PillButtonTemplate(cornerRadius: 16, hoverBg: primary ? null : BgInputHover),
        };
        return btn;
    }

    private static ControlTemplate PillButtonTemplate(double cornerRadius, Brush? hoverBg)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Border");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        border.SetValue(Border.BorderBrushProperty, BorderSubtle);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, new TemplateBindingExtension(HorizontalContentAlignmentProperty));
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        if (hoverBg is not null)
            hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Border"));
        else
            hover.Setters.Add(new Setter(OpacityProperty, 0.88));
        template.Triggers.Add(hover);
        return template;
    }

    private static ControlTemplate FlatIconButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
        template.Triggers.Add(hover);
        return template;
    }

    private static Style CreateDarkScrollBarStyle() =>
        (Style)System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="ScrollBar">
                <Setter Property="Width" Value="6"/>
                <Setter Property="MinWidth" Value="6"/>
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="ScrollBar">
                            <Grid Background="Transparent" Width="6" Margin="0,4,2,4">
                                <Track x:Name="PART_Track" IsDirectionReversed="True">
                                    <Track.Thumb>
                                        <Thumb>
                                            <Thumb.Template>
                                                <ControlTemplate TargetType="Thumb">
                                                    <Border Background="#4D4D4D" CornerRadius="3"/>
                                                </ControlTemplate>
                                            </Thumb.Template>
                                        </Thumb>
                                    </Track.Thumb>
                                </Track>
                            </Grid>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """);

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
