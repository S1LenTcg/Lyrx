using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Musebase.Android.Services;
using Musebase.Core.Search;
using Musebase.Engine;

namespace Musebase.Android.UI;

/// <summary>
/// 复刻桌面端 <c>ClassicSettingsWindow</c> 视觉风格并适配移动端的设置弹窗：
/// 顶部栏（绿色设置图标 +「设置」+ 关闭）、顶部 4 分类横向标签切换（带绿色指示条）、
/// 下方深色分区卡片、40×22 胶囊开关、深色下拉选择器以及 8 套主题色块预览。
/// </summary>
internal static class SettingsSheetDialog
{
    private static readonly Color BgWindow = Color.Rgb(18, 18, 18);        // #121212
    private static readonly Color BgSidebar = Color.Rgb(14, 14, 14);       // #0E0E0E
    private static readonly Color BgCard = Color.Rgb(26, 26, 26);          // #1A1A1A
    private static readonly Color BgInput = Color.Rgb(38, 38, 38);         // #262626
    private static readonly Color BgPopup = Color.Rgb(32, 32, 32);         // #202020
    private static readonly Color BgNavActive = Color.Rgb(40, 40, 40);     // #282828
    private static readonly Color BorderSubtle = Color.Rgb(46, 46, 46);    // #2E2E2E
    private static readonly Color BorderPopup = Color.Rgb(64, 64, 64);     // #404040
    private static readonly Color TextPrimary = Color.White;
    private static readonly Color TextMuted = Color.Rgb(167, 167, 167);    // #A7A7A7
    private static readonly Color AccentGreen = Color.Rgb(30, 215, 96);    // #1ED760
    private static readonly Color SwitchOff = Color.Rgb(68, 68, 68);       // #444444
    private static readonly Color DangerRed = Color.Rgb(255, 128, 128);

    private static readonly (string Id, string Title, string Description)[] SourceDefinitions =
    [
        ("netease", "网易云音乐 (NetEase)", "官方时间轴歌词，内置优质中译"),
        ("qqmusic", "QQ 音乐 (QQMusic)", "官方时间轴歌词，内置双语翻译"),
        ("kugou", "酷狗音乐 (Kugou)", "海量华语与日韩曲库时间轴歌词"),
        ("lrclib", "LRCLIB (国际开源曲库)", "欧美与全球曲目同步歌词库"),
    ];

    private static readonly (string Id, string Label)[] SpacingModes =
    [
        ("compact", "紧凑"),
        ("normal", "标准"),
        ("loose", "宽松"),
    ];

    private static readonly (string Id, string Label)[] AutoSyncOptions =
    [
        ("0", "关闭"),
        ("3", "3 秒"),
        ("5", "5 秒"),
        ("10", "10 秒"),
    ];

    private static readonly (string Id, string Label)[] TranslationEngines =
    [
        ("none", "仅使用歌词源自带翻译（推荐 / 免 Key）"),
        ("mymemory", "MyMemory（免费在线机器翻译）"),
        ("deepl", "DeepL API"),
        ("google", "Google Cloud Translation"),
        ("openrouter", "OpenRouter (AI 大模型翻译)"),
    ];

    private static readonly (string Id, string Label)[] TargetLanguages =
    [
        ("ZH", "简体中文 (ZH)"),
        ("ZH-HANT", "繁體中文 (ZH-HANT)"),
        ("EN", "English (EN)"),
        ("JA", "日本語 (JA)"),
        ("KO", "韩语 (KO)"),
    ];

    private static readonly string[] NavTitles =
    [
        "播放与窗口",
        "歌词搜索源",
        "双语与翻译",
        "当前歌曲",
    ];

    public static void Show(
        Activity activity,
        Action onAppearanceOrLayoutChanged,
        Action openNotificationSettings)
    {
        if (activity.IsFinishing || activity.IsDestroyed) return;

        var app = LyrxApp.Instance;
        var dm = activity.Resources?.DisplayMetrics;
        var screenW = dm?.WidthPixels ?? 1080;
        var screenH = dm?.HeightPixels ?? 2200;
        var dialogW = (int)(screenW * 0.94);
        var dialogH = Math.Min((int)(screenH * 0.56), UiHelpers.Dp(activity, 470));
        var contentApproxW = Math.Max(UiHelpers.Dp(activity, 240), dialogW - UiHelpers.Dp(activity, 48));

        var dialog = new Dialog(activity);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);

        var root = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Background = UiHelpers.RoundedRect(activity, BgWindow, 10, BorderSubtle, 1f),
            ClipToOutline = true,
            OutlineProvider = new RoundedOutlineProvider(UiHelpers.Dp(activity, 10)),
        };

        // ── 顶部标题栏（#0E0E0E：绿色齿轮图标 +「设置」+ 关闭 X）──
        var topBar = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        topBar.SetBackgroundColor(BgSidebar);
        topBar.SetGravity(GravityFlags.CenterVertical);
        topBar.SetPadding(UiHelpers.Dp(activity, 14), 0, UiHelpers.Dp(activity, 8), 0);

        var gearIcon = new VectorIconView(activity, ClassicIconKind.Settings);
        gearIcon.Configure(ClassicIconKind.Settings, AccentGreen, iconFraction: 0.88f);
        topBar.AddView(gearIcon, new LinearLayout.LayoutParams(UiHelpers.Dp(activity, 19), UiHelpers.Dp(activity, 19))
        {
            RightMargin = UiHelpers.Dp(activity, 8),
        });

        var titleTv = new TextView(activity) { Text = "设置" };
        titleTv.SetTextColor(TextPrimary);
        titleTv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14.5f);
        titleTv.SetTypeface(null, TypefaceStyle.Bold);
        topBar.AddView(titleTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var closeBtn = UiHelpers.IconButton(activity, ClassicIconKind.Close, TextMuted, () => dialog.Dismiss(), sizeDp: 32, iconFraction: 0.50f);
        topBar.AddView(closeBtn, new LinearLayout.LayoutParams(UiHelpers.Dp(activity, 32), UiHelpers.Dp(activity, 32)));

        root.AddView(topBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(activity, 40)));

        // ── 顶部标签栏（4 个分页标签横向切换 + 底部绿条高亮）──
        var tabBar = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        tabBar.SetBackgroundColor(BgSidebar);
        tabBar.SetPadding(UiHelpers.Dp(activity, 8), UiHelpers.Dp(activity, 2), UiHelpers.Dp(activity, 8), UiHelpers.Dp(activity, 8));

        var navItems = new List<(LinearLayout Host, View Indicator, TextView Label)>();
        var rightScroll = new ScrollView(activity)
        {
            VerticalScrollBarEnabled = false,
        };
        rightScroll.SetBackgroundColor(BgWindow);

        var rightHost = new FrameLayout(activity);
        rightHost.SetPadding(UiHelpers.Dp(activity, 12), UiHelpers.Dp(activity, 10), UiHelpers.Dp(activity, 12), UiHelpers.Dp(activity, 12));

        var pageViews = new LinearLayout[4];
        Action? refreshTrackPage = null;

        pageViews[0] = BuildPlaybackAndAppearanceTab(activity, app, contentApproxW, onAppearanceOrLayoutChanged, openNotificationSettings);
        pageViews[1] = BuildSourcesTab(activity, app, dialog);
        pageViews[2] = BuildTranslationTab(activity, app, onAppearanceOrLayoutChanged);
        (pageViews[3], refreshTrackPage) = BuildCurrentTrackTab(activity, app, dialog, onAppearanceOrLayoutChanged);

        for (var i = 0; i < pageViews.Length; i++)
        {
            pageViews[i].Visibility = i == 0 ? ViewStates.Visible : ViewStates.Gone;
            rightHost.AddView(pageViews[i], new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        void SelectTab(int index)
        {
            for (var i = 0; i < navItems.Count; i++)
            {
                var active = i == index;
                var (host, indicator, label) = navItems[i];
                host.Background = active ? UiHelpers.RoundedRect(activity, BgNavActive, 6) : null;
                indicator.Background = active ? UiHelpers.RoundedRect(activity, AccentGreen, 1.5f) : null;
                label.SetTextColor(active ? TextPrimary : TextMuted);
                label.SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
                pageViews[i].Visibility = active ? ViewStates.Visible : ViewStates.Gone;
            }
            if (index == 3) refreshTrackPage?.Invoke();
            rightScroll.ScrollTo(0, 0);
        }

        for (var i = 0; i < NavTitles.Length; i++)
        {
            var idx = i;
            var item = new LinearLayout(activity)
            {
                Orientation = global::Android.Widget.Orientation.Vertical,
                Clickable = true,
            };
            item.SetGravity(GravityFlags.CenterHorizontal);
            item.SetPadding(UiHelpers.Dp(activity, 2), UiHelpers.Dp(activity, 4), UiHelpers.Dp(activity, 2), UiHelpers.Dp(activity, 3));

            var navLabel = new TextView(activity)
            {
                Text = NavTitles[i],
                Gravity = GravityFlags.Center,
                Ellipsize = TextUtils.TruncateAt.End,
            };
            navLabel.SetMaxLines(1);
            navLabel.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
            item.AddView(navLabel, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));

            var indicator = new View(activity);
            item.AddView(indicator, new LinearLayout.LayoutParams(UiHelpers.Dp(activity, 22), UiHelpers.Dp(activity, 2.5f))
            {
                TopMargin = UiHelpers.Dp(activity, 2),
            });

            item.Click += (_, _) => SelectTab(idx);
            navItems.Add((item, indicator, navLabel));

            var lp = new LinearLayout.LayoutParams(0, UiHelpers.Dp(activity, 34), 1f);
            if (i < NavTitles.Length - 1) lp.RightMargin = UiHelpers.Dp(activity, 2);
            tabBar.AddView(item, lp);
        }

        SelectTab(0);

        root.AddView(tabBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(MakeHorizontalLine(activity, 0));

        rightScroll.AddView(rightHost, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(rightScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        dialog.SetContentView(root);
        dialog.Window?.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        dialog.Window?.SetSoftInputMode(SoftInput.AdjustResize);
        dialog.Window?.SetLayout(dialogW, dialogH);
        dialog.SetCanceledOnTouchOutside(true);
        dialog.Show();
    }

    // ────────────────────────────────────────────────────────────────
    // Tab 0：播放与窗口
    // ────────────────────────────────────────────────────────────────
    private static LinearLayout BuildPlaybackAndAppearanceTab(
        Activity ctx,
        LyrxApp app,
        int contentApproxW,
        Action onAppearanceOrLayoutChanged,
        Action openNotificationSettings)
    {
        var page = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };

        // 卡片 1：播放音源与监听
        var audioCard = CreateSectionCard(ctx, "播放音源与监听");

        var sourceOptions = new List<(string Id, string Label)>
        {
            ("auto", "自动识别活跃音乐"),
        };
        foreach (var pkg in app.NowPlaying.GetAvailableSources())
        {
            var shortName = pkg.Split('.').LastOrDefault() ?? pkg;
            sourceOptions.Add((pkg, $"{shortName} ({pkg})"));
        }
        if (!string.Equals(app.Settings.PlaybackSource, "auto", StringComparison.OrdinalIgnoreCase)
            && !sourceOptions.Any(o => string.Equals(o.Id, app.Settings.PlaybackSource, StringComparison.OrdinalIgnoreCase)))
        {
            sourceOptions.Add((app.Settings.PlaybackSource, app.Settings.PlaybackSource));
        }

        var srcLabel = new TextView(ctx) { Text = "音源选择：" };
        srcLabel.SetTextColor(TextMuted);
        srcLabel.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        audioCard.AddView(srcLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(ctx, 5),
        });

        var (sourceDropdown, _) = CreateDarkDropdown(ctx, sourceOptions, app.Settings.PlaybackSource, (_, id) =>
        {
            app.Settings.PlaybackSource = id;
            app.SaveSettings();
            app.NowPlaying.SetSource(app.Settings.PlaybackSource, app.Settings.IncludeBrowsers, app.Settings.PreferredSources);
        });
        audioCard.AddView(sourceDropdown, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 34)));

        audioCard.AddView(MakeHorizontalLine(ctx, 9));

        var (browserToggle, _) = CreateToggleSwitch(ctx, app.Settings.IncludeBrowsers, val =>
        {
            app.Settings.IncludeBrowsers = val;
            app.SaveSettings();
            app.NowPlaying.SetSource(app.Settings.PlaybackSource, app.Settings.IncludeBrowsers, app.Settings.PreferredSources);
        });
        audioCard.AddView(CreateSettingRow(
            ctx,
            "包含浏览器音频",
            "识别网页版 Spotify、YouTube Music、Bilibili 等浏览器媒体",
            browserToggle));

        audioCard.AddView(MakeHorizontalLine(ctx, 9));

        var granted = app.NowPlaying.HasNotificationAccess();
        var permBtn = CreateDarkButton(
            ctx,
            granted ? "已授权" : "去开启",
            granted ? AccentGreen : Color.Rgb(255, 196, 96),
            openNotificationSettings,
            horizontalPadDp: 12);
        audioCard.AddView(CreateSettingRow(
            ctx,
            "通知使用权",
            granted ? "已开启，可读取播放器状态与控制进度" : "需在系统设置授权后才能监听系统正在播放的歌曲",
            permBtn,
            wrapRightWidth: true));

        page.AddView(audioCard);

        // 卡片 2：窗口与外观配色
        var themeCard = CreateSectionCard(ctx, "窗口与外观配色");

        var spacingOptions = SpacingModes;
        var (spacingDropdown, _) = CreateDarkDropdown(
            ctx,
            spacingOptions,
            app.Settings.LyricsLineSpacing,
            (_, id) =>
            {
                app.Settings.LyricsLineSpacing = id;
                app.SaveSettings();
                onAppearanceOrLayoutChanged();
            },
            fixedWidthDp: 102);
        themeCard.AddView(CreateSettingRow(ctx, "歌词行距", null, spacingDropdown, wrapRightWidth: true));

        themeCard.AddView(MakeHorizontalLine(ctx, 9));

        var autoSyncId = app.Settings.AutoSyncSeconds.ToString();
        var autoSyncList = AutoSyncOptions.ToList();
        if (!autoSyncList.Any(o => o.Id == autoSyncId))
            autoSyncList.Add((autoSyncId, $"{app.Settings.AutoSyncSeconds} 秒"));
        var (autoSyncDropdown, _) = CreateDarkDropdown(
            ctx,
            autoSyncList,
            autoSyncId,
            (_, id) =>
            {
                if (int.TryParse(id, out var sec))
                {
                    app.Settings.AutoSyncSeconds = sec;
                    app.SaveSettings();
                }
            },
            fixedWidthDp: 102);
        themeCard.AddView(CreateSettingRow(ctx, "自动同步", null, autoSyncDropdown, wrapRightWidth: true));

        themeCard.AddView(MakeHorizontalLine(ctx, 9));

        var (karaokeToggle, _) = CreateToggleSwitch(ctx, app.Settings.CharacterKaraoke, val =>
        {
            app.Settings.CharacterKaraoke = val;
            app.SaveSettings();
            onAppearanceOrLayoutChanged();
        });
        themeCard.AddView(CreateSettingRow(
            ctx,
            "逐字卡拉 OK",
            "歌词包含逐字时间轴时随播放进度渐变点亮",
            karaokeToggle));

        themeCard.AddView(MakeHorizontalLine(ctx, 9));

        var themeLabel = new TextView(ctx) { Text = "背景配色模式：" };
        themeLabel.SetTextColor(TextMuted);
        themeLabel.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        themeCard.AddView(themeLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(ctx, 5),
        });

        var currentPresetId = app.Settings.BackgroundMode?.Equals("black", StringComparison.OrdinalIgnoreCase) == true
            ? "midnight"
            : string.IsNullOrWhiteSpace(app.Settings.BackgroundMode)
                ? "dynamic"
                : app.Settings.BackgroundMode;

        var presetDropdownItems = LyricsPalette.ThemePresets
            .Select(p => (p.Id, p.Label))
            .ToArray();

        Action<string>? syncSwatchHighlight = null;
        Action<string>? syncPresetDropdown = null;

        void ApplyThemeSelection(string presetId)
        {
            app.Settings.BackgroundMode = presetId;
            app.SaveSettings();
            syncSwatchHighlight?.Invoke(presetId);
            syncPresetDropdown?.Invoke(presetId);
            onAppearanceOrLayoutChanged();
        }

        var (themeDropdown, setThemeDropdownById) = CreateDarkDropdown(
            ctx,
            presetDropdownItems,
            currentPresetId,
            (_, id) => ApplyThemeSelection(id));
        syncPresetDropdown = setThemeDropdownById;

        themeCard.AddView(themeDropdown, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 34))
        {
            BottomMargin = UiHelpers.Dp(ctx, 9),
        });

        var swatchContainer = BuildThemeSwatchFlow(
            ctx,
            contentApproxW,
            currentPresetId,
            presetId => ApplyThemeSelection(presetId),
            out syncSwatchHighlight);
        themeCard.AddView(swatchContainer);

        page.AddView(themeCard);

        // 卡片 3：开发者模式（精确复刻桌面端改版：开发者模式 -> 导出log）
        var devCard = CreateSectionCard(ctx, "开发者模式");
        var (timingToggle, _) = CreateToggleSwitch(ctx, app.Settings.TimingDiagnosticsEnabled, val =>
        {
            app.Settings.TimingDiagnosticsEnabled = val;
            app.NowPlaying.TimingDiagnostics = val;
            app.SaveSettings();
        });
        devCard.AddView(CreateSettingRow(
            ctx,
            "导出log",
            "开启后将歌词与歌曲播放采样写入本地日志文件，用于排查同步偏差",
            timingToggle));

        devCard.AddView(MakeHorizontalLine(ctx, 9));

        var shareLogRow = new LinearLayout(ctx)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        shareLogRow.SetGravity(GravityFlags.CenterVertical);
        var logHint = new TextView(ctx)
        {
            Text = "日志文件：lyrx.log",
            Ellipsize = TextUtils.TruncateAt.Middle,
        };
        logHint.SetMaxLines(1);
        logHint.SetTextColor(TextMuted);
        logHint.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11);
        shareLogRow.AddView(logHint, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        {
            RightMargin = UiHelpers.Dp(ctx, 8),
        });
        var shareLogBtn = CreateDarkButton(ctx, "分享最近日志", TextPrimary, () => ShareRecentLogs(ctx), horizontalPadDp: 10);
        shareLogRow.AddView(shareLogBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, UiHelpers.Dp(ctx, 30)));
        devCard.AddView(shareLogRow);

        page.AddView(devCard);
        return page;
    }

    // ────────────────────────────────────────────────────────────────
    // Tab 1：歌词搜索源
    // ────────────────────────────────────────────────────────────────
    private static LinearLayout BuildSourcesTab(Activity ctx, LyrxApp app, Dialog dialog)
    {
        var page = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };

        var card = CreateSectionCard(ctx, "启用与搜索来源");
        var note = new TextView(ctx)
        {
            Text = "同时搜索所有已启用的歌词源，并按时间轴完整度与曲目匹配度择优返回。",
        };
        note.SetTextColor(TextMuted);
        note.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
        note.SetLineSpacing(0f, 1.15f);
        card.AddView(note, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(ctx, 10),
        });

        var enabledSet = new HashSet<string>(
            app.Settings.EnabledLyricsSources.Count > 0
                ? app.Settings.EnabledLyricsSources
                : LyricsSourceRegistry.AllIds,
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < SourceDefinitions.Length; i++)
        {
            if (i > 0) card.AddView(MakeHorizontalLine(ctx, 9));
            var (id, title, desc) = SourceDefinitions[i];
            Action<bool>? setChecked = null;
            var (toggleView, setCheckedFn) = CreateToggleSwitch(ctx, enabledSet.Contains(id), val =>
            {
                if (!val && enabledSet.Count <= 1 && enabledSet.Contains(id))
                {
                    setChecked?.Invoke(true);
                    Toast.MakeText(ctx, "至少保留一个启用的歌词源", ToastLength.Short)?.Show();
                    return;
                }
                if (val) enabledSet.Add(id);
                else enabledSet.Remove(id);

                app.Settings.EnabledLyricsSources = SourceDefinitions
                    .Select(s => s.Id)
                    .Where(enabledSet.Contains)
                    .ToList();
                app.SaveSettings();
                app.SyncLyricsSources();
            });
            setChecked = setCheckedFn;
            card.AddView(CreateSettingRow(ctx, title, desc, toggleView));
        }

        page.AddView(card);

        var actionCard = CreateSectionCard(ctx, "重搜缓存");
        var reloadBtn = CreateDarkButton(ctx, "清空缓存并重新搜索当前歌曲", TextPrimary, () =>
        {
            app.Coordinator.ReloadCurrentTrackLyrics();
            Toast.MakeText(ctx, "正在按最新音源重新搜索歌词…", ToastLength.Short)?.Show();
            dialog.Dismiss();
        });
        actionCard.AddView(reloadBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 36)));
        page.AddView(actionCard);

        return page;
    }

    // ────────────────────────────────────────────────────────────────
    // Tab 2：双语与翻译
    // ────────────────────────────────────────────────────────────────
    private static LinearLayout BuildTranslationTab(Activity ctx, LyrxApp app, Action onAppearanceOrLayoutChanged)
    {
        var page = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };

        // 卡片 1：译文显示控制
        var displayCard = CreateSectionCard(ctx, "双语译文显示");

        var (showTrToggle, _) = CreateToggleSwitch(ctx, app.Settings.ShowTranslation, val =>
        {
            app.Settings.ShowTranslation = val;
            app.SaveSettings();
            onAppearanceOrLayoutChanged();
        });
        displayCard.AddView(CreateSettingRow(
            ctx,
            "显示歌词译文",
            "也可通过主界面底部 文A 按钮随时切换",
            showTrToggle));

        displayCard.AddView(MakeHorizontalLine(ctx, 9));

        var (targetOnlyToggle, _) = CreateToggleSwitch(ctx, app.Settings.ShowOnlyTargetTranslation, val =>
        {
            app.Settings.ShowOnlyTargetTranslation = val;
            app.SaveSettings();
            app.SyncTranslationEngine();
            onAppearanceOrLayoutChanged();
        });
        displayCard.AddView(CreateSettingRow(
            ctx,
            "只显示目标语言译文",
            "隐藏非目标语种自带译文（目标语为中文时保留中文自带译文）",
            targetOnlyToggle));

        displayCard.AddView(MakeHorizontalLine(ctx, 9));

        var (hideSameToggle, _) = CreateToggleSwitch(ctx, app.Settings.HideSameTranslation, val =>
        {
            app.Settings.HideSameTranslation = val;
            app.SaveSettings();
            onAppearanceOrLayoutChanged();
        });
        displayCard.AddView(CreateSettingRow(
            ctx,
            "隐藏与原文相同的译文",
            "当译文和原文内容完全一致时不重复显示",
            hideSameToggle));

        page.AddView(displayCard);

        // 卡片 2：目标语言与机器翻译补充
        var engineCard = CreateSectionCard(ctx, "翻译引擎与语言");

        var rawLang = app.Settings.EffectiveTargetLanguage;
        var dropdownLang = rawLang.StartsWith("EN", StringComparison.OrdinalIgnoreCase) ? "EN" : rawLang;
        if (!TargetLanguages.Any(t => string.Equals(t.Id, dropdownLang, StringComparison.OrdinalIgnoreCase)))
            dropdownLang = "ZH";

        var langLabel = new TextView(ctx) { Text = "目标语言：" };
        langLabel.SetTextColor(TextMuted);
        langLabel.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        engineCard.AddView(langLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(ctx, 5),
        });

        var (langDropdown, _) = CreateDarkDropdown(ctx, TargetLanguages, dropdownLang, (idx, id) =>
        {
            app.Settings.TargetLanguage = id;
            app.SaveSettings();
            app.SyncTranslationEngine();
            if (app.Settings.ApiTranslationEnabled)
                _ = app.Coordinator.RetranslateCurrentAsync();
            onAppearanceOrLayoutChanged();
        });
        engineCard.AddView(langDropdown, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 34)));

        engineCard.AddView(MakeHorizontalLine(ctx, 10));

        var engineLabel = new TextView(ctx) { Text = "在线机器翻译补充（当歌词源无自带翻译时使用）：" };
        engineLabel.SetTextColor(TextMuted);
        engineLabel.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        engineCard.AddView(engineLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(ctx, 5),
        });

        // 条件显示的 API Key 输入面板（DeepL / Google / OpenRouter）
        var keySection = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };
        var keyTitle = new TextView(ctx);
        keyTitle.SetTextColor(TextMuted);
        keyTitle.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
        keySection.AddView(keyTitle, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = UiHelpers.Dp(ctx, 10),
            BottomMargin = UiHelpers.Dp(ctx, 5),
        });

        var keyInput = CreateDarkEditText(ctx, "粘贴 API Key…", isSecret: true);
        keySection.AddView(keyInput, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 36)));

        var modelLabel = new TextView(ctx) { Text = "OpenRouter 模型（可选）：" };
        modelLabel.SetTextColor(TextMuted);
        modelLabel.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
        keySection.AddView(modelLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = UiHelpers.Dp(ctx, 8),
            BottomMargin = UiHelpers.Dp(ctx, 4),
        });

        var modelInput = CreateDarkEditText(ctx, "例如 openai/gpt-4o-mini", isSecret: false);
        modelInput.Text = app.Settings.OpenRouterModel ?? "";
        keySection.AddView(modelInput, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 36)));

        var activeEngine = app.Settings.EffectiveTranslationEngine;

        void RefreshKeySection(string engineId)
        {
            activeEngine = engineId;
            var needsKey = engineId is "deepl" or "google" or "openrouter";
            keySection.Visibility = needsKey ? ViewStates.Visible : ViewStates.Gone;
            if (!needsKey) return;

            keyTitle.Text = engineId switch
            {
                "deepl" => "DeepL API Key：",
                "google" => "Google Cloud Translation API Key：",
                _ => "OpenRouter API Key：",
            };
            keyInput.Text = engineId switch
            {
                "deepl" => app.Settings.DeeplApiKey ?? "",
                "google" => app.Settings.GoogleApiKey ?? "",
                "openrouter" => app.Settings.OpenRouterApiKey ?? "",
                _ => "",
            };
            var showModel = string.Equals(engineId, "openrouter", StringComparison.OrdinalIgnoreCase);
            modelLabel.Visibility = showModel ? ViewStates.Visible : ViewStates.Gone;
            modelInput.Visibility = showModel ? ViewStates.Visible : ViewStates.Gone;
        }

        var saveKeyBtn = CreateDarkButton(ctx, "保存密钥并立即翻译", AccentGreen, () =>
        {
            var trimmed = keyInput.Text?.Trim();
            switch (activeEngine)
            {
                case "deepl":
                    app.Settings.DeeplApiKey = string.IsNullOrEmpty(trimmed) ? null : trimmed;
                    break;
                case "google":
                    app.Settings.GoogleApiKey = string.IsNullOrEmpty(trimmed) ? null : trimmed;
                    break;
                case "openrouter":
                    app.Settings.OpenRouterApiKey = string.IsNullOrEmpty(trimmed) ? null : trimmed;
                    var mod = modelInput.Text?.Trim();
                    app.Settings.OpenRouterModel = string.IsNullOrEmpty(mod) ? null : mod;
                    break;
            }
            app.Settings.ApiTranslationEnabled = true;
            app.SaveSettings();
            app.SyncTranslationEngine();
            _ = app.Coordinator.RetranslateCurrentAsync();
            onAppearanceOrLayoutChanged();
            Toast.MakeText(ctx, "已应用翻译密钥", ToastLength.Short)?.Show();
        });
        keySection.AddView(saveKeyBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 34))
        {
            TopMargin = UiHelpers.Dp(ctx, 8),
        });

        var (engineDropdown, _) = CreateDarkDropdown(ctx, TranslationEngines, activeEngine, (idx, id) =>
        {
            app.Settings.TranslationEngine = id;
            app.Settings.ApiTranslationEnabled = !string.Equals(id, "none", StringComparison.OrdinalIgnoreCase);
            app.SaveSettings();
            app.SyncTranslationEngine();
            RefreshKeySection(id);
            if (app.Settings.ApiTranslationEnabled)
                _ = app.Coordinator.RetranslateCurrentAsync();
            onAppearanceOrLayoutChanged();
        });
        engineCard.AddView(engineDropdown, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 34)));

        RefreshKeySection(activeEngine);
        engineCard.AddView(keySection);

        page.AddView(engineCard);
        return page;
    }

    // ────────────────────────────────────────────────────────────────
    // Tab 3：当前歌曲
    // ────────────────────────────────────────────────────────────────
    private static (LinearLayout Page, Action Refresh) BuildCurrentTrackTab(
        Activity ctx,
        LyrxApp app,
        Dialog dialog,
        Action onAppearanceOrLayoutChanged)
    {
        var page = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };

        // 卡片 1：当前曲目信息与状态
        var infoCard = CreateSectionCard(ctx, "曲目与歌词状态");
        var trackTitleTv = new TextView(ctx);
        trackTitleTv.SetTextColor(TextPrimary);
        trackTitleTv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
        trackTitleTv.SetTypeface(null, TypefaceStyle.Bold);

        var trackStatusTv = new TextView(ctx);
        trackStatusTv.SetTextColor(TextMuted);
        trackStatusTv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        trackStatusTv.SetLineSpacing(0f, 1.25f);

        infoCard.AddView(trackTitleTv, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(ctx, 4),
        });
        infoCard.AddView(trackStatusTv);
        page.AddView(infoCard);

        // 卡片 2：时间轴微调
        var offsetCard = CreateSectionCard(ctx, "当前歌曲时间偏移");
        var offsetReadout = new TextView(ctx);
        offsetReadout.SetTextColor(TextMuted);
        offsetReadout.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        offsetCard.AddView(offsetReadout, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(ctx, 8),
        });

        void RefreshData()
        {
            var t = app.NowPlaying.CurrentTrack;
            trackTitleTv.Text = t is null ? "当前未检测到播放曲目" : $"{t.Title} — {t.Artist}";
            trackStatusTv.Text = app.GetTrackStatusSummary();

            var trackVal = app.Coordinator.TrackOffsetSeconds;
            var globalVal = app.Settings.ManualOffsetSeconds;
            var fmtTrack = Math.Abs(trackVal) < 0.005 ? "0.0s" : $"{trackVal:+0.0;-0.0;0.0}s";
            var fmtGlobal = Math.Abs(globalVal) < 0.005 ? "0.0s" : $"{globalVal:+0.0;-0.0;0.0}s";
            offsetReadout.Text = $"仅此曲偏移：{fmtTrack}　|　全局偏移：{fmtGlobal}";
        }

        void StepOffset(double? delta)
        {
            if (app.NowPlaying.CurrentTrack is { } tr)
                app.AdjustTrackOffsetDelta(tr, delta);
            else
                app.AdjustGlobalOffsetDelta(delta);
            RefreshData();
            onAppearanceOrLayoutChanged();
        }

        var stepRow = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Horizontal };
        (string Label, double? Delta)[] steps =
        [
            ("-0.5s", -0.5),
            ("-0.1s", -0.1),
            ("归零", null),
            ("+0.1s", 0.1),
            ("+0.5s", 0.5),
        ];
        for (var i = 0; i < steps.Length; i++)
        {
            var (label, delta) = steps[i];
            var btn = CreateDarkButton(ctx, label, TextPrimary, () => StepOffset(delta), horizontalPadDp: 0);
            btn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
            var lp = new LinearLayout.LayoutParams(0, UiHelpers.Dp(ctx, 32), 1f);
            if (i < steps.Length - 1) lp.RightMargin = UiHelpers.Dp(ctx, 5);
            stepRow.AddView(btn, lp);
        }
        offsetCard.AddView(stepRow);
        page.AddView(offsetCard);

        // 卡片 3：歌词操作
        var opsCard = CreateSectionCard(ctx, "歌词操作");
        var reloadBtn = CreateDarkButton(ctx, "重新搜索当前歌词", TextPrimary, () =>
        {
            app.Coordinator.ReloadCurrentTrackLyrics();
            Toast.MakeText(ctx, "正在重新搜索歌词…", ToastLength.Short)?.Show();
            dialog.Dismiss();
        });
        opsCard.AddView(reloadBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 36))
        {
            BottomMargin = UiHelpers.Dp(ctx, 8),
        });

        var exportBtn = CreateDarkButton(ctx, "导出当前歌词 (.lrc)", TextPrimary, () => ExportCurrentLrc(ctx, app));
        opsCard.AddView(exportBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 36))
        {
            BottomMargin = UiHelpers.Dp(ctx, 8),
        });

        var wrongBtn = CreateDarkButton(ctx, "标记为错误歌词（跳过并隐藏）", DangerRed, () =>
        {
            app.Coordinator.MarkWrongLyrics();
            Toast.MakeText(ctx, "已隐藏当前歌曲的错误歌词", ToastLength.Short)?.Show();
            dialog.Dismiss();
        });
        opsCard.AddView(wrongBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(ctx, 36)));

        page.AddView(opsCard);
        RefreshData();
        return (page, RefreshData);
    }

    // ────────────────────────────────────────────────────────────────
    // 桌面端风格控件构建器（ToggleSwitch / DarkDropdown / Swatches）
    // ────────────────────────────────────────────────────────────────
    private static LinearLayout CreateSectionCard(Context ctx, string title)
    {
        var card = new LinearLayout(ctx)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Background = UiHelpers.RoundedRect(ctx, BgCard, 10, BorderSubtle, 1f),
        };
        card.SetPadding(
            UiHelpers.Dp(ctx, 12),
            UiHelpers.Dp(ctx, 10),
            UiHelpers.Dp(ctx, 12),
            UiHelpers.Dp(ctx, 10));

        var header = new TextView(ctx) { Text = title };
        header.SetTextColor(TextPrimary);
        header.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13.5f);
        header.SetTypeface(null, TypefaceStyle.Bold);
        card.AddView(header, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(ctx, 8),
        });

        card.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(ctx, 9),
        };
        return card;
    }

    private static View MakeHorizontalLine(Context ctx, float verticalMarginDp)
    {
        var line = new View(ctx);
        line.SetBackgroundColor(BorderSubtle);
        var marginPx = UiHelpers.Dp(ctx, verticalMarginDp);
        line.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Math.Max(1, UiHelpers.Dp(ctx, 0.8f)))
        {
            TopMargin = marginPx,
            BottomMargin = marginPx,
        };
        return line;
    }

    private static LinearLayout CreateSettingRow(
        Context ctx,
        string title,
        string? subtitle,
        View rightControl,
        bool wrapRightWidth = false)
    {
        var row = new LinearLayout(ctx)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        row.SetGravity(GravityFlags.CenterVertical);

        var leftCol = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };
        var titleTv = new TextView(ctx) { Text = title };
        titleTv.SetTextColor(TextPrimary);
        titleTv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12.5f);
        titleTv.SetTypeface(null, TypefaceStyle.Bold);
        leftCol.AddView(titleTv);

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subTv = new TextView(ctx) { Text = subtitle };
            subTv.SetTextColor(TextMuted);
            subTv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11);
            subTv.SetLineSpacing(0f, 1.15f);
            leftCol.AddView(subTv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = UiHelpers.Dp(ctx, 2),
            });
        }

        row.AddView(leftCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        {
            RightMargin = UiHelpers.Dp(ctx, 10),
        });

        if (rightControl.LayoutParameters is not null)
            row.AddView(rightControl);
        else if (wrapRightWidth)
            row.AddView(rightControl, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        else
            row.AddView(rightControl);

        return row;
    }

    private static (FrameLayout View, Action<bool> SetChecked) CreateToggleSwitch(
        Context ctx,
        bool initial,
        Action<bool> onChanged)
    {
        var isChecked = initial;
        var w = UiHelpers.Dp(ctx, 40);
        var h = UiHelpers.Dp(ctx, 22);
        var thumbSize = UiHelpers.Dp(ctx, 16);
        var inset = UiHelpers.Dp(ctx, 3);
        var travelPx = w - thumbSize - inset * 2;

        var trackDrawable = UiHelpers.RoundedRect(ctx, isChecked ? AccentGreen : SwitchOff, 11);
        var host = new FrameLayout(ctx)
        {
            Background = trackDrawable,
            Clickable = true,
            LayoutParameters = new LinearLayout.LayoutParams(w, h),
        };

        var thumb = new View(ctx)
        {
            Background = UiHelpers.RoundedRect(ctx, Color.White, 8),
            TranslationX = isChecked ? travelPx : 0f,
        };
        host.AddView(thumb, new FrameLayout.LayoutParams(thumbSize, thumbSize)
        {
            Gravity = GravityFlags.CenterVertical | GravityFlags.Start,
            LeftMargin = inset,
        });

        void ApplyState(bool next, bool animate)
        {
            isChecked = next;
            trackDrawable.SetColor((isChecked ? AccentGreen : SwitchOff).ToArgb());
            var targetX = isChecked ? travelPx : 0f;
            if (animate)
            {
                thumb.Animate()?.TranslationX(targetX)?.SetDuration(140)?.Start();
            }
            else
            {
                thumb.Animate()?.Cancel();
                thumb.TranslationX = targetX;
            }
        }

        host.Click += (_, _) =>
        {
            var next = !isChecked;
            ApplyState(next, animate: true);
            onChanged(next);
        };

        return (host, next => ApplyState(next, animate: false));
    }

    private static (LinearLayout View, Action<string> SetSelectedById) CreateDarkDropdown(
        Context ctx,
        IReadOnlyList<(string Id, string Label)> items,
        string initialId,
        Action<int, string> onSelected,
        int? fixedWidthDp = null)
    {
        var currentIdx = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Id, initialId, StringComparison.OrdinalIgnoreCase))
            {
                currentIdx = i;
                break;
            }
        }

        var box = new LinearLayout(ctx)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            Background = UiHelpers.RoundedRect(ctx, BgInput, 6, BorderSubtle, 1f),
            Clickable = true,
        };
        box.SetGravity(GravityFlags.CenterVertical);
        box.SetPadding(UiHelpers.Dp(ctx, 10), 0, UiHelpers.Dp(ctx, 8), 0);
        if (fixedWidthDp is { } fw)
            box.LayoutParameters = new LinearLayout.LayoutParams(UiHelpers.Dp(ctx, fw), UiHelpers.Dp(ctx, 32));

        var selectedTv = new TextView(ctx)
        {
            Text = items.Count > 0 ? items[currentIdx].Label : "",
            Ellipsize = TextUtils.TruncateAt.End,
        };
        selectedTv.SetMaxLines(1);
        selectedTv.SetTextColor(TextPrimary);
        selectedTv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        box.AddView(selectedTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        {
            RightMargin = UiHelpers.Dp(ctx, 6),
        });

        var chevron = new VectorIconView(ctx, ClassicIconKind.ChevronDown);
        chevron.Configure(ClassicIconKind.ChevronDown, TextMuted, iconFraction: 0.78f);
        box.AddView(chevron, new LinearLayout.LayoutParams(UiHelpers.Dp(ctx, 14), UiHelpers.Dp(ctx, 14)));

        void UpdateBoxLabel(int idx)
        {
            currentIdx = Math.Clamp(idx, 0, Math.Max(0, items.Count - 1));
            selectedTv.Text = items.Count > 0 ? items[currentIdx].Label : "";
        }

        PopupWindow? openPopup = null;
        box.Click += (_, _) =>
        {
            if (openPopup is { IsShowing: true })
            {
                openPopup.Dismiss();
                return;
            }
            if (items.Count == 0) return;

            var popupCard = new LinearLayout(ctx)
            {
                Orientation = global::Android.Widget.Orientation.Vertical,
                Background = UiHelpers.RoundedRect(ctx, BgPopup, 8, BorderPopup, 1f),
                Elevation = UiHelpers.Dp(ctx, 12),
            };
            var pad = UiHelpers.Dp(ctx, 4);
            popupCard.SetPadding(pad, pad, pad, pad);

            var scroll = new ScrollView(ctx) { VerticalScrollBarEnabled = false };
            var listCol = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };
            scroll.AddView(listCol, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            var screenW = ctx.Resources?.DisplayMetrics?.WidthPixels ?? 1080;
            var popupW = Math.Clamp(
                Math.Max(box.Width, UiHelpers.Dp(ctx, 190)),
                UiHelpers.Dp(ctx, 160),
                (int)(screenW * 0.78));

            var popup = new PopupWindow(popupCard, popupW, ViewGroup.LayoutParams.WrapContent, focusable: true)
            {
                OutsideTouchable = true,
                Elevation = UiHelpers.Dp(ctx, 12),
            };
            popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
            popup.DismissEvent += (_, _) =>
            {
                chevron.Animate()?.Rotation(0f)?.SetDuration(160)?.Start();
                openPopup = null;
            };

            for (var i = 0; i < items.Count; i++)
            {
                var idx = i;
                var active = idx == currentIdx;
                var itemRow = new LinearLayout(ctx)
                {
                    Orientation = global::Android.Widget.Orientation.Horizontal,
                    Clickable = true,
                };
                itemRow.SetGravity(GravityFlags.CenterVertical);
                itemRow.SetPadding(UiHelpers.Dp(ctx, 10), UiHelpers.Dp(ctx, 8), UiHelpers.Dp(ctx, 10), UiHelpers.Dp(ctx, 8));
                if (active)
                    itemRow.Background = UiHelpers.RoundedRect(ctx, Color.Rgb(42, 42, 42), 5);

                var labelTv = new TextView(ctx)
                {
                    Text = items[i].Label,
                    Ellipsize = TextUtils.TruncateAt.End,
                };
                labelTv.SetMaxLines(1);
                labelTv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
                labelTv.SetTextColor(active ? AccentGreen : TextPrimary);
                labelTv.SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
                itemRow.AddView(labelTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                var check = new TextView(ctx)
                {
                    Text = active ? "✓" : "",
                };
                check.SetTextColor(AccentGreen);
                check.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
                check.SetTypeface(null, TypefaceStyle.Bold);
                itemRow.AddView(check, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
                {
                    LeftMargin = UiHelpers.Dp(ctx, 8),
                });

                itemRow.Click += (_, _) =>
                {
                    popup.Dismiss();
                    if (currentIdx != idx)
                    {
                        UpdateBoxLabel(idx);
                        onSelected(idx, items[idx].Id);
                    }
                };
                listCol.AddView(itemRow, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            }

            // 超出 6 项时限制弹窗最大高度
            var maxH = UiHelpers.Dp(ctx, 236);
            scroll.Measure(
                View.MeasureSpec.MakeMeasureSpec(Math.Max(1, popupW - pad * 2), MeasureSpecMode.Exactly),
                View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
            popupCard.AddView(scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                scroll.MeasuredHeight > maxH ? maxH : ViewGroup.LayoutParams.WrapContent));

            openPopup = popup;
            chevron.Animate()?.Rotation(180f)?.SetDuration(160)?.Start();

            var xOff = box.Width - popupW;
            popup.ShowAsDropDown(box, xOff, UiHelpers.Dp(ctx, 4));
        };

        return (box, targetId =>
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Id, targetId, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateBoxLabel(i);
                    break;
                }
            }
        });
    }

    private static LinearLayout BuildThemeSwatchFlow(
        Context ctx,
        int availableWidthPx,
        string initialId,
        Action<string> onPick,
        out Action<string> setSelectedId)
    {
        var flowCol = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };
        var chipViews = new List<(string Id, LinearLayout Chip)>();
        var currentRow = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Horizontal };
        var rowUsedPx = 0;
        var gapPx = UiHelpers.Dp(ctx, 6);

        void FinishRow()
        {
            if (currentRow.ChildCount == 0) return;
            flowCol.AddView(currentRow, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                BottomMargin = UiHelpers.Dp(ctx, 6),
            });
            currentRow = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Horizontal };
            rowUsedPx = 0;
        }

        foreach (var preset in LyricsPalette.ThemePresets)
        {
            var shortName = preset.Label.Split('（')[0];
            var chip = new LinearLayout(ctx)
            {
                Orientation = global::Android.Widget.Orientation.Horizontal,
                Clickable = true,
            };
            chip.SetGravity(GravityFlags.CenterVertical);
            chip.SetPadding(UiHelpers.Dp(ctx, 9), UiHelpers.Dp(ctx, 5), UiHelpers.Dp(ctx, 11), UiHelpers.Dp(ctx, 5));

            var dotHost = BuildSwatchDot(ctx, preset);
            chip.AddView(dotHost, new LinearLayout.LayoutParams(UiHelpers.Dp(ctx, 14), UiHelpers.Dp(ctx, 14))
            {
                RightMargin = UiHelpers.Dp(ctx, 6),
            });

            var labelTv = new TextView(ctx) { Text = shortName };
            labelTv.SetTextColor(TextPrimary);
            labelTv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
            labelTv.SetMaxLines(1);
            chip.AddView(labelTv);

            var capturedId = preset.Id;
            chip.Click += (_, _) => onPick(capturedId);
            chipViews.Add((capturedId, chip));

            chip.Measure(
                View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified),
                View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
            var chipW = chip.MeasuredWidth;

            if (rowUsedPx > 0 && rowUsedPx + gapPx + chipW > availableWidthPx)
                FinishRow();

            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            if (currentRow.ChildCount > 0) lp.LeftMargin = gapPx;
            currentRow.AddView(chip, lp);
            rowUsedPx += (rowUsedPx > 0 ? gapPx : 0) + chipW;
        }
        FinishRow();

        void ApplyHighlight(string activeId)
        {
            foreach (var (id, chip) in chipViews)
            {
                var selected = string.Equals(id, activeId, StringComparison.OrdinalIgnoreCase);
                chip.Background = UiHelpers.RoundedRect(
                    ctx,
                    Color.Rgb(30, 30, 30),
                    15,
                    selected ? AccentGreen : Color.Rgb(58, 58, 58),
                    selected ? 1.5f : 1f);
            }
        }

        ApplyHighlight(initialId);
        setSelectedId = ApplyHighlight;
        return flowCol;
    }

    private static View BuildSwatchDot(Context ctx, ClassicThemePreset preset)
    {
        if (!preset.IsDynamic)
        {
            return new View(ctx)
            {
                Background = UiHelpers.RoundedRect(ctx, preset.Background, 7, Color.Rgb(102, 102, 102), 1f),
            };
        }

        // 动态取色图标：外圈深绿细环 + 中心亮绿圆点（复刻桌面版同款双环指示）
        var ring = new FrameLayout(ctx)
        {
            Background = UiHelpers.RoundedRect(ctx, Color.Rgb(24, 52, 34), 7, AccentGreen, 1f),
        };
        var inner = new View(ctx)
        {
            Background = UiHelpers.RoundedRect(ctx, AccentGreen, 3),
        };
        ring.AddView(inner, new FrameLayout.LayoutParams(UiHelpers.Dp(ctx, 6), UiHelpers.Dp(ctx, 6))
        {
            Gravity = GravityFlags.Center,
        });
        return ring;
    }

    private static Button CreateDarkButton(
        Context ctx,
        string label,
        Color textColor,
        Action onClick,
        int horizontalPadDp = 12)
    {
        var btn = new Button(ctx)
        {
            Text = label,
            Background = UiHelpers.RoundedRect(ctx, BgInput, 6, BorderSubtle, 1f),
            Gravity = GravityFlags.Center,
        };
        btn.SetAllCaps(false);
        btn.SetTextColor(textColor);
        btn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        btn.SetTypeface(null, TypefaceStyle.Bold);
        var hPad = UiHelpers.Dp(ctx, horizontalPadDp);
        btn.SetPadding(hPad, 0, hPad, 0);
        btn.SetMinHeight(0);
        btn.SetMinimumHeight(0);
        btn.SetMinWidth(0);
        btn.SetMinimumWidth(0);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static EditText CreateDarkEditText(Context ctx, string hint, bool isSecret)
    {
        var edit = new EditText(ctx)
        {
            Hint = hint,
            Background = UiHelpers.RoundedRect(ctx, BgInput, 6, BorderSubtle, 1f),
            InputType = isSecret
                ? InputTypes.ClassText | InputTypes.TextVariationPassword
                : InputTypes.ClassText,
        };
        edit.SetSingleLine(true);
        edit.SetTextColor(TextPrimary);
        edit.SetHintTextColor(Color.Rgb(118, 118, 118));
        edit.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        edit.SetPadding(UiHelpers.Dp(ctx, 10), 0, UiHelpers.Dp(ctx, 10), 0);
        return edit;
    }

    private static void ExportCurrentLrc(Activity ctx, LyrxApp app)
    {
        var track = app.NowPlaying.CurrentTrack;
        var lyrics = app.Coordinator.LyricsFor(track);
        if (track is null || lyrics is null || lyrics.Lines.Count == 0)
        {
            Toast.MakeText(ctx, "当前暂无可导出的歌词", ToastLength.Short)?.Show();
            return;
        }

        var lrc = lyrics.ToLegacyString(app.Coordinator.TargetLanguage?.ToLowerInvariant());
        try
        {
            var share = new Intent(Intent.ActionSend);
            share.SetType("text/plain");
            share.PutExtra(Intent.ExtraSubject, $"{track.Artist} - {track.Title}.lrc");
            share.PutExtra(Intent.ExtraText, lrc);
            ctx.StartActivity(Intent.CreateChooser(share, $"导出歌词：{track.Title}"));
        }
        catch
        {
            CopyToClipboard(ctx, "LRC", lrc);
            Toast.MakeText(ctx, "已复制 LRC 歌词到剪贴板", ToastLength.Short)?.Show();
        }
    }

    private static void ShareRecentLogs(Activity ctx)
    {
        var lines = AndroidLog.SnapshotRecent();
        if (lines.Count == 0)
        {
            Toast.MakeText(ctx, "暂无可分享的日志记录", ToastLength.Short)?.Show();
            return;
        }

        var text = string.Join('\n', lines);
        try
        {
            var share = new Intent(Intent.ActionSend);
            share.SetType("text/plain");
            share.PutExtra(Intent.ExtraSubject, "Lyrx Android 诊断日志");
            share.PutExtra(Intent.ExtraText, text);
            ctx.StartActivity(Intent.CreateChooser(share, "分享最近日志"));
        }
        catch
        {
            CopyToClipboard(ctx, "LyrxLog", text);
            Toast.MakeText(ctx, "已复制最近诊断日志到剪贴板", ToastLength.Short)?.Show();
        }
    }

    private static void CopyToClipboard(Context ctx, string label, string text)
    {
        if (ctx.GetSystemService(Context.ClipboardService) is global::Android.Content.ClipboardManager cm)
            cm.PrimaryClip = ClipData.NewPlainText(label, text);
    }
}
