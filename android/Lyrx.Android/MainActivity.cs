using Android.Animation;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Text.Style;
using Android.Util;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using Musebase.Android.Services;
using Musebase.Android.UI;
using Musebase.Core;
using Musebase.Core.Search;
using Musebase.Engine;

namespace Musebase.Android;

/// <summary>
/// Android 主界面：整体视觉排版、字体层级、色彩体系与交互均向桌面端 <c>ClassicLyricsWindow</c> 对齐。
/// 采用 #121212 外框 + 圆角动态取色歌词主卡片 + 底部精简播控与时间轴。
/// </summary>
[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : Activity
{
    private readonly record struct KaraokeSegment(int Start, int End, double StartTime, double EndTime, bool IsWhitespace);

    private sealed class LyricRowHolder
    {
        public int LineIndex { get; init; }
        public required LinearLayout Root { get; init; }
        public required TextView Original { get; init; }
        public required TextView Translation { get; init; }
        public required string RawText { get; init; }
        public IReadOnlyList<KaraokeSegment> KaraokeSegments { get; init; } = Array.Empty<KaraokeSegment>();
        public Color CurrentColor { get; set; } = LyricsPalette.DefaultSubdued;
        public ValueAnimator? ColorAnimator { get; set; }
    }

    private sealed class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View? v, WindowInsets? insets)
        {
            if (v is not null && insets is not null && OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                v.SetPadding(
                    bars.Left,
                    bars.Top,
                    bars.Right,
                    bars.Bottom);
            }
            return insets!;
        }
    }

    private static readonly System.Net.Http.HttpClient CoverHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static readonly DecelerateInterpolator ScrollInterpolator = new(1.8f);
    private static readonly DecelerateInterpolator RowEaseInterpolator = new(1.5f);

    private readonly List<LyricRowHolder> _rows = new();
    private readonly Handler _uiHandler = new(Looper.MainLooper!);
    private Action? _tickRunnable;

    private TrackInfo? _track;
    private Lyrics? _renderedLyrics;
    private LyricsStatusKind? _renderedStatus;
    private int _activeLineIndex = -1;
    private bool _latinMetrics;
    private bool _manualScroll;
    private long _manualScrollAtMs;
    private long _coverGeneration;
    private string? _lastCoverKey;
    private double? _pendingSeekSeconds;
    private long _pendingSeekDeadlineMs;
    private bool _disposed;
    private bool _isResumed;

    private Color _coverBackground = LyricsPalette.DefaultBackground;
    private Color _coverSubdued = LyricsPalette.DefaultSubdued;
    private Color _background = LyricsPalette.DefaultBackground;
    private Color _subdued = LyricsPalette.DefaultSubdued;

    private ValueAnimator? _bgAnimator;
    private ObjectAnimator? _scrollAnimator;

    private LinearLayout _rootLayout = null!;
    private LinearLayout _permissionBanner = null!;
    private FrameLayout _lyricsPanel = null!;
    private GradientDrawable _panelBgDrawable = null!;
    private GradientDrawable _topFadeDrawable = null!;
    private GradientDrawable _bottomFadeDrawable = null!;
    private ScrollView _lyricsScrollView = null!;
    private LinearLayout _lyricsContainer = null!;
    private TextView _emptyHintView = null!;
    private LinearLayout _resumePill = null!;

    private ImageView _coverView = null!;
    private TextView _titleView = null!;
    private TextView _artistView = null!;
    private LinearLayout _offsetHost = null!;
    private TextView _offsetBadge = null!;
    private VectorIconView _offsetIcon = null!;
    private VectorIconView _translateIcon = null!;
    private VectorIconView _settingsIcon = null!;

    private TextView _elapsedView = null!;
    private ClassicSeekBar _seekBar = null!;
    private TextView _durationView = null!;
    private FrameLayout _prevBtnHost = null!;
    private VectorIconView _prevIcon = null!;
    private FrameLayout _playBtnHost = null!;
    private VectorIconView _playIcon = null!;
    private FrameLayout _nextBtnHost = null!;
    private VectorIconView _nextIcon = null!;

    private static LyrxApp App => LyrxApp.Instance;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        ApplyChromeSystemBars();
        BuildLayout();
        SetContentView(_rootLayout);

        ApplyActivePalette(animate: false);
        BindEngineEvents();
        StartUiLoopIfNeeded();
        RefreshAll(forceTrackCheck: true);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _isResumed = true;
        App.EnsureNowPlayingStarted();
        RefreshPermissionBanner();
        RefreshAll(forceTrackCheck: true);
        StartUiLoopIfNeeded();
    }

    protected override void OnPause()
    {
        _isResumed = false;
        StopUiLoop();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _disposed = true;
        StopUiLoop();
        _bgAnimator?.Cancel();
        _scrollAnimator?.Cancel();
        UnbindEngineEvents();
        base.OnDestroy();
    }

    private void ApplyChromeSystemBars()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
#pragma warning disable CS0618
            Window?.SetStatusBarColor(LyricsPalette.ChromeBackground);
            Window?.SetNavigationBarColor(LyricsPalette.ChromeBackground);
#pragma warning restore CS0618
        }
    }

    private void BuildLayout()
    {
        _rootLayout = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        _rootLayout.SetBackgroundColor(LyricsPalette.ChromeBackground);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            _rootLayout.SetOnApplyWindowInsetsListener(new InsetsListener());
        else
            _rootLayout.SetFitsSystemWindows(true);

        _permissionBanner = BuildPermissionBanner();
        _rootLayout.AddView(_permissionBanner, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            LeftMargin = Dp(10),
            TopMargin = Dp(4),
            RightMargin = Dp(10),
            BottomMargin = Dp(4),
        });

        _lyricsPanel = BuildLyricsPanel();
        _rootLayout.AddView(_lyricsPanel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f)
        {
            LeftMargin = Dp(8),
            TopMargin = Dp(4),
            RightMargin = Dp(8),
            BottomMargin = Dp(4),
        });

        _rootLayout.AddView(BuildFooter(), new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }

    private LinearLayout BuildPermissionBanner()
    {
        var card = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            Background = UiHelpers.RoundedRect(this, LyricsPalette.SurfaceCard, 10, LyricsPalette.SubtleBorder, 1f),
            Visibility = ViewStates.Gone,
        };
        card.SetGravity(GravityFlags.CenterVertical);
        card.SetPadding(Dp(14), Dp(10), Dp(12), Dp(10));

        var col = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        var title = new TextView(this) { Text = "需要开启「通知使用权」" };
        title.SetTextColor(Color.White);
        title.SetTextSize(ComplexUnitType.Sp, 13);
        title.SetTypeface(null, TypefaceStyle.Bold);

        var desc = new TextView(this) { Text = "开启后可自动读取正在播放的曲目、封面与进度" };
        desc.SetTextColor(LyricsPalette.MutedGray);
        desc.SetTextSize(ComplexUnitType.Sp, 11.5f);

        col.AddView(title);
        col.AddView(desc);
        card.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        {
            RightMargin = Dp(10),
        });

        var goBtn = new Button(this)
        {
            Text = "去授权",
            Background = UiHelpers.RoundedRect(this, LyricsPalette.AccentGreen, 14),
            Gravity = GravityFlags.Center,
        };
        goBtn.SetAllCaps(false);
        goBtn.SetTextColor(LyricsPalette.ChromeBackground);
        goBtn.SetTextSize(ComplexUnitType.Sp, 12);
        goBtn.SetTypeface(null, TypefaceStyle.Bold);
        goBtn.SetPadding(Dp(14), 0, Dp(14), 0);
        goBtn.SetMinHeight(0);
        goBtn.SetMinimumHeight(0);
        goBtn.Click += (_, _) => OpenNotificationListenerSettings();

        card.AddView(goBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, Dp(30)));
        return card;
    }

    private FrameLayout BuildLyricsPanel()
    {
        _panelBgDrawable = UiHelpers.RoundedRect(this, _background, 10);
        var panel = new FrameLayout(this)
        {
            Background = _panelBgDrawable,
            ClipToOutline = true,
            OutlineProvider = new RoundedOutlineProvider(Dp(10)),
        };

        _lyricsScrollView = new ScrollView(this)
        {
            VerticalScrollBarEnabled = false,
            FillViewport = true,
        };
        _lyricsScrollView.Touch += (_, e) =>
        {
            if (e.Event?.ActionMasked is MotionEventActions.Move)
                PauseAutoScroll();
            e.Handled = false;
        };

        _lyricsContainer = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        ApplyLyricsContainerPadding();

        _emptyHintView = new TextView(this)
        {
            Gravity = GravityFlags.Left,
            Text = "在你的播放器中播放一首歌",
        };
        _emptyHintView.SetTextColor(_subdued);
        _emptyHintView.SetTextSize(ComplexUnitType.Sp, 24);
        _emptyHintView.SetTypeface(null, TypefaceStyle.Bold);
        _emptyHintView.SetLineSpacing(0f, 1.15f);
        _lyricsContainer.AddView(_emptyHintView, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _lyricsScrollView.AddView(_lyricsContainer, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _lyricsScrollView.LayoutChange += (_, _) =>
        {
            ApplyLyricsContainerPadding();
        };

        panel.AddView(_lyricsScrollView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // 卡片上下边缘柔和渐隐遮罩（使用同色透明通道，避免插值泛黑）
        var transparentBg = Color.Argb(0, _background.R, _background.G, _background.B);
        _topFadeDrawable = new GradientDrawable(
            GradientDrawable.Orientation.TopBottom,
            [_background.ToArgb(), transparentBg.ToArgb()]);
        var topFade = new View(this) { Background = _topFadeDrawable };
        panel.AddView(topFade, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(26))
        {
            Gravity = GravityFlags.Top,
        });

        _bottomFadeDrawable = new GradientDrawable(
            GradientDrawable.Orientation.BottomTop,
            [_background.ToArgb(), transparentBg.ToArgb()]);
        var bottomFade = new View(this) { Background = _bottomFadeDrawable };
        panel.AddView(bottomFade, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(34))
        {
            Gravity = GravityFlags.Bottom,
        });

        _resumePill = BuildResumePill();
        panel.AddView(_resumePill, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, Dp(42))
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            BottomMargin = Dp(20),
        });

        return panel;
    }

    private LinearLayout BuildResumePill()
    {
        var pill = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            Background = UiHelpers.RoundedRect(this, Color.White, 21),
            Clickable = true,
            LongClickable = true,
            Elevation = Dp(6),
            Visibility = ViewStates.Gone,
        };
        pill.SetGravity(GravityFlags.Center);
        pill.SetPadding(Dp(16), 0, Dp(18), 0);

        var icon = new VectorIconView(this, ClassicIconKind.Sync);
        icon.Configure(ClassicIconKind.Sync, LyricsPalette.ChromeBackground, iconFraction: 0.72f);
        pill.AddView(icon, new LinearLayout.LayoutParams(Dp(20), Dp(20))
        {
            RightMargin = Dp(7),
        });

        var label = new TextView(this) { Text = "同步" };
        label.SetTextColor(LyricsPalette.ChromeBackground);
        label.SetTextSize(ComplexUnitType.Sp, 14.5f);
        label.SetTypeface(null, TypefaceStyle.Bold);
        pill.AddView(label);

        pill.Click += (_, _) =>
        {
            App.Coordinator.RefreshCurrentLine();
            ResumeAutoScroll();
            FollowActiveLine(animate: true);
        };
        pill.LongClick += (_, e) =>
        {
            e.Handled = true;
            OpenOffsetDialog();
        };
        return pill;
    }

    private LinearLayout BuildFooter()
    {
        var footer = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        footer.SetBackgroundColor(LyricsPalette.ChromeBackground);
        footer.SetPadding(Dp(14), Dp(6), Dp(14), Dp(10));

        // 第一行：左侧封面+曲目信息，右侧时间偏移 / 译文 / 设置图标（对齐桌面 compact footer）
        var topRow = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        topRow.SetGravity(GravityFlags.CenterVertical);

        var coverFrame = new FrameLayout(this)
        {
            Background = UiHelpers.RoundedRect(this, LyricsPalette.CoverFrameBackground, 4),
            ClipToOutline = true,
            OutlineProvider = new RoundedOutlineProvider(Dp(4)),
        };
        var noteFallback = new TextView(this)
        {
            Text = "♫",
            Gravity = GravityFlags.Center,
        };
        noteFallback.SetTextColor(LyricsPalette.MutedGray);
        noteFallback.SetTextSize(ComplexUnitType.Sp, 18);
        coverFrame.AddView(noteFallback, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _coverView = new ImageView(this);
        _coverView.SetScaleType(ImageView.ScaleType.CenterCrop);
        coverFrame.AddView(_coverView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        topRow.AddView(coverFrame, new LinearLayout.LayoutParams(Dp(46), Dp(46))
        {
            RightMargin = Dp(12),
        });

        var trackText = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Clickable = true,
        };
        trackText.Click += (_, _) => OpenSettingsDialog();

        _titleView = new TextView(this)
        {
            Text = "等待播放",
            Ellipsize = TextUtils.TruncateAt.End,
        };
        _titleView.SetMaxLines(1);
        _titleView.SetTextColor(Color.White);
        _titleView.SetTextSize(ComplexUnitType.Sp, 14.5f);
        _titleView.SetTypeface(null, TypefaceStyle.Bold);

        _artistView = new TextView(this)
        {
            Text = "",
            Ellipsize = TextUtils.TruncateAt.End,
        };
        _artistView.SetMaxLines(1);
        _artistView.SetTextColor(LyricsPalette.MutedGray);
        _artistView.SetTextSize(ComplexUnitType.Sp, 12f);

        trackText.AddView(_titleView);
        trackText.AddView(_artistView);
        topRow.AddView(trackText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        {
            RightMargin = Dp(8),
        });

        topRow.AddView(BuildFooterActions());
        footer.AddView(topRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(6),
        });

        // 第二行：播放进度条 + 时间标签
        var timeline = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        timeline.SetGravity(GravityFlags.CenterVertical);

        _elapsedView = MakeTimeLabel(GravityFlags.Right);
        _seekBar = new ClassicSeekBar(this);
        _seekBar.PreviewChanged += sec => _elapsedView.Text = FormatTime(TimeSpan.FromSeconds(sec));
        _seekBar.SeekRequested += sec => _ = SeekToSecondsAsync(sec);
        _durationView = MakeTimeLabel(GravityFlags.Left);

        timeline.AddView(_elapsedView, new LinearLayout.LayoutParams(Dp(40), ViewGroup.LayoutParams.WrapContent));
        timeline.AddView(_seekBar, new LinearLayout.LayoutParams(0, Dp(24), 1f)
        {
            LeftMargin = Dp(6),
            RightMargin = Dp(6),
        });
        timeline.AddView(_durationView, new LinearLayout.LayoutParams(Dp(40), ViewGroup.LayoutParams.WrapContent));
        footer.AddView(timeline, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(4),
        });

        // 第三行：极简 Spotify 传输按钮 [⏮] [⚪▶/⏸] [⏭]
        var transport = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        transport.SetGravity(GravityFlags.Center);

        _prevBtnHost = MakeGlyphButton(ClassicIconKind.Previous, 42, 0.56f, null,
            out _prevIcon, () => _ = App.NowPlaying.SkipPreviousAsync());
        _playBtnHost = MakeGlyphButton(ClassicIconKind.Play, 40, 0.52f, Color.White,
            out _playIcon, () => _ = App.NowPlaying.TogglePlayPauseAsync());
        _nextBtnHost = MakeGlyphButton(ClassicIconKind.Next, 42, 0.56f, null,
            out _nextIcon, () => _ = App.NowPlaying.SkipNextAsync());

        transport.AddView(_prevBtnHost, new LinearLayout.LayoutParams(Dp(42), Dp(42)));
        transport.AddView(_playBtnHost, new LinearLayout.LayoutParams(Dp(40), Dp(40))
        {
            LeftMargin = Dp(22),
            RightMargin = Dp(22),
        });
        transport.AddView(_nextBtnHost, new LinearLayout.LayoutParams(Dp(42), Dp(42)));

        footer.AddView(transport, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        return footer;
    }

    private LinearLayout BuildFooterActions()
    {
        var actions = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        actions.SetGravity(GravityFlags.CenterVertical);

        _offsetHost = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            Clickable = true,
        };
        _offsetHost.SetGravity(GravityFlags.Center);
        _offsetHost.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));
        _offsetIcon = new VectorIconView(this, ClassicIconKind.Clock);
        _offsetIcon.Configure(ClassicIconKind.Clock, LyricsPalette.MutedGray, iconFraction: 0.90f);
        _offsetHost.AddView(_offsetIcon, new LinearLayout.LayoutParams(Dp(18), Dp(18)));

        _offsetBadge = new TextView(this)
        {
            Visibility = ViewStates.Gone,
        };
        _offsetBadge.SetTextColor(LyricsPalette.AccentGreen);
        _offsetBadge.SetTextSize(ComplexUnitType.Sp, 11f);
        _offsetBadge.SetTypeface(null, TypefaceStyle.Bold);
        _offsetBadge.SetPadding(Dp(4), 0, 0, 0);
        _offsetHost.AddView(_offsetBadge);
        _offsetHost.Click += (_, _) => OpenOffsetDialog();

        var translateHost = MakeGlyphButton(ClassicIconKind.Translate, 36, 0.60f, null,
            out _translateIcon, ToggleTranslationDisplay);
        var settingsHost = MakeGlyphButton(ClassicIconKind.Settings, 36, 0.60f, null,
            out _settingsIcon, OpenSettingsDialog);

        actions.AddView(_offsetHost, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, Dp(36)));
        actions.AddView(translateHost, new LinearLayout.LayoutParams(Dp(36), Dp(36))
        {
            LeftMargin = Dp(2),
        });
        actions.AddView(settingsHost, new LinearLayout.LayoutParams(Dp(36), Dp(36))
        {
            LeftMargin = Dp(2),
        });
        return actions;
    }

    private FrameLayout MakeGlyphButton(
        ClassicIconKind kind,
        float sizeDp,
        float iconFraction,
        Color? circleBg,
        out VectorIconView iconOut,
        Action onClick)
    {
        var host = new FrameLayout(this)
        {
            Clickable = true,
            Focusable = true,
        };
        if (circleBg is { } bg)
            host.Background = UiHelpers.RoundedRect(this, bg, sizeDp / 2f);

        var tint = circleBg is not null ? LyricsPalette.ChromeBackground : LyricsPalette.MutedGray;
        iconOut = new VectorIconView(this, kind);
        iconOut.Configure(kind, tint, iconFraction: iconFraction);
        host.AddView(iconOut, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        host.Click += (_, _) => onClick();
        return host;
    }

    private TextView MakeTimeLabel(GravityFlags gravity)
    {
        var tv = new TextView(this)
        {
            Text = "–:––",
            Gravity = gravity | GravityFlags.CenterVertical,
        };
        tv.SetTextColor(LyricsPalette.MutedGray);
        tv.SetTextSize(ComplexUnitType.Sp, 11.5f);
        return tv;
    }

    private void ApplyLyricsContainerPadding()
    {
        var side = Dp(24);
        var top = Dp(40);
        var bottom = _lyricsScrollView.Height > 0 ? _lyricsScrollView.Height / 2 : Dp(220);
        if (_lyricsContainer.PaddingLeft != side || _lyricsContainer.PaddingTop != top || _lyricsContainer.PaddingBottom != bottom)
            _lyricsContainer.SetPadding(side, top, side, bottom);
    }

    private void BindEngineEvents()
    {
        App.Coordinator.CurrentLineChanged += OnEngineDirty;
        App.Coordinator.StatusChanged += OnStatusDirty;
        App.Coordinator.TranslationStatusChanged += OnTranslationUpdated;
        App.NowPlaying.TrackChanged += OnTrackUpdated;
        App.NowPlaying.IsPlayingChanged += OnPlayingUpdated;
        App.NowPlaying.ArtworkChanged += OnArtworkUpdated;
    }

    private void UnbindEngineEvents()
    {
        if (LyrxApp.Instance is not { } app) return;
        app.Coordinator.CurrentLineChanged -= OnEngineDirty;
        app.Coordinator.StatusChanged -= OnStatusDirty;
        app.Coordinator.TranslationStatusChanged -= OnTranslationUpdated;
        app.NowPlaying.TrackChanged -= OnTrackUpdated;
        app.NowPlaying.IsPlayingChanged -= OnPlayingUpdated;
        app.NowPlaying.ArtworkChanged -= OnArtworkUpdated;
    }

    private void OnEngineDirty(DisplayLine? _) => RunIfAlive(() => RefreshAll(forceTrackCheck: false));
    private void OnStatusDirty(LyricsStatus _) => RunIfAlive(() => RefreshAll(forceTrackCheck: false));
    private void OnTranslationUpdated(TranslationDisplayStatus _) => RunIfAlive(() =>
    {
        RefreshRowTranslations();
        RefreshAll(forceTrackCheck: false);
    });
    private void OnTrackUpdated(TrackInfo? _) => RunIfAlive(() =>
    {
        RefreshAll(forceTrackCheck: true);
        StartUiLoopIfNeeded();
    });
    private void OnPlayingUpdated(bool _) => RunIfAlive(() =>
    {
        UpdateTransportAndButtons();
        StartUiLoopIfNeeded();
    });
    private void OnArtworkUpdated() => RunIfAlive(() => RefreshCoverArt(App.NowPlaying.CurrentTrack));

    private void RunIfAlive(Action action)
    {
        if (_disposed || IsFinishing || IsDestroyed) return;
        RunOnUiThread(() =>
        {
            if (_disposed || IsFinishing || IsDestroyed) return;
            action();
        });
    }

    private void StartUiLoopIfNeeded()
    {
        _tickRunnable ??= () =>
        {
            if (_disposed || !_isResumed) return;
            RefreshFrameTick();
            if (App.NowPlaying.IsPlaying || _manualScroll)
                _uiHandler.PostDelayed(_tickRunnable!, 33);
        };
        _uiHandler.RemoveCallbacks(_tickRunnable);
        if (_isResumed && (App.NowPlaying.IsPlaying || _manualScroll))
            _uiHandler.Post(_tickRunnable);
    }

    private void StopUiLoop()
    {
        if (_tickRunnable is not null)
            _uiHandler.RemoveCallbacks(_tickRunnable);
    }

    private void RefreshAll(bool forceTrackCheck)
    {
        var track = App.NowPlaying.CurrentTrack;
        var songChanged = forceTrackCheck && !SameTrackIdentity(track, _track);
        var metadataChanged = !Equals(track, _track);

        if (songChanged)
        {
            _track = track;
            App.ApplyTrackOffsetFor(track);
            _activeLineIndex = -1;
            _pendingSeekSeconds = null;
            _pendingSeekDeadlineMs = 0;
            _manualScroll = false;
            _resumePill.Visibility = ViewStates.Gone;

            _titleView.Text = string.IsNullOrWhiteSpace(track?.Title) ? "等待播放" : track!.Title;
            _artistView.Text = track?.Artist ?? "";

            RefreshCoverArt(track, resetBeforeLoad: true);
        }
        else if (metadataChanged)
        {
            _track = track;
            _titleView.Text = string.IsNullOrWhiteSpace(track?.Title) ? "等待播放" : track!.Title;
            _artistView.Text = track?.Artist ?? "";
            if (_lastCoverKey is null && track is not null)
                RefreshCoverArt(track);
        }

        EnsureLyricsListMatchesCurrent();
        UpdateTransportAndButtons();
        RefreshFrameTick();
    }

    private void RefreshFrameTick()
    {
        AutoSyncIfIdle();

        var track = App.NowPlaying.CurrentTrack;
        var lyrics = App.Coordinator.LyricsFor(track);

        if (!ReferenceEquals(lyrics, _renderedLyrics) || App.Coordinator.CurrentStatus?.Kind != _renderedStatus)
            EnsureLyricsListMatchesCurrent();

        var pos = App.NowPlaying.GetEstimatedPosition();
        var duration = track?.Duration;
        var displaySec = ResolveTimelineSeconds(pos, duration);

        if (!_seekBar.IsDragging)
        {
            _elapsedView.Text = displaySec is { } s ? FormatTime(TimeSpan.FromSeconds(s)) : "–:––";
            _durationView.Text = duration is { } d && d.TotalSeconds > 0 ? FormatTime(d) : "–:––";
            _seekBar.SetProgress(
                displaySec ?? 0,
                duration is { } total && total.TotalSeconds > 0 ? total.TotalSeconds : 0,
                enabled: duration is { } dur && dur.TotalSeconds > 0 && track is not null);
        }

        if (lyrics is null || _rows.Count == 0 || displaySec is null)
            return;

        var adjusted = displaySec.Value
            + lyrics.TimeDelay
            + App.Coordinator.ManualOffsetSeconds
            + App.Coordinator.TrackOffsetSeconds;
        var (currentIndex, _) = lyrics.LineIndexesAt(adjusted);
        var nextActive = currentIndex ?? -1;

        if (nextActive != _activeLineIndex)
        {
            var prev = _activeLineIndex;
            _activeLineIndex = nextActive;

            foreach (var row in _rows)
            {
                if (row.LineIndex == prev || row.LineIndex == _activeLineIndex)
                    ApplyRowVisualState(row, animate: true);
                else if ((prev < 0 || prev > _activeLineIndex) && row.LineIndex < _activeLineIndex)
                    ApplyRowVisualState(row, animate: false);
            }

            if (!_manualScroll)
                FollowActiveLine(animate: true);
        }

        if (_activeLineIndex >= 0 && App.Settings.CharacterKaraoke)
        {
            var activeRow = _rows.FirstOrDefault(r => r.LineIndex == _activeLineIndex);
            if (activeRow is { KaraokeSegments.Count: > 0 })
            {
                var elapsed = adjusted - lyrics.Lines[_activeLineIndex].Position;
                ApplyKaraokeSpans(activeRow, Math.Max(0, elapsed));
            }
        }
    }

    private double? ResolveTimelineSeconds(TimeSpan? rawPos, TimeSpan? duration)
    {
        if (rawPos is null && _pendingSeekSeconds is null) return null;
        var current = rawPos?.TotalSeconds ?? 0;
        if (_pendingSeekSeconds is { } pending)
        {
            if (Math.Abs(current - pending) <= 1.2 || SystemClock.UptimeMillis() >= _pendingSeekDeadlineMs)
                _pendingSeekSeconds = null;
            else
                current = pending;
        }
        if (duration is { } d && d.TotalSeconds > 0)
            current = Math.Clamp(current, 0, d.TotalSeconds);
        return Math.Max(0, current);
    }

    private void EnsureLyricsListMatchesCurrent()
    {
        var track = App.NowPlaying.CurrentTrack;
        var lyrics = App.Coordinator.LyricsFor(track);
        var status = App.Coordinator.CurrentStatus?.Kind;
        if (ReferenceEquals(lyrics, _renderedLyrics) && status == _renderedStatus)
            return;

        _renderedLyrics = lyrics;
        _renderedStatus = status;
        _activeLineIndex = -1;
        _scrollAnimator?.Cancel();
        _lyricsScrollView.ScrollTo(0, 0);
        _rows.Clear();
        _lyricsContainer.RemoveAllViews();

        if (lyrics is null || lyrics.Lines.Count == 0)
        {
            _emptyHintView.Text = FormatEmptyHint(status);
            _emptyHintView.SetTextColor(_subdued);
            _lyricsContainer.AddView(_emptyHintView, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            return;
        }

        _latinMetrics = IsMostlyLatin(lyrics);
        var (lineScale, gapScale) = App.Settings.LineSpacingScales;
        var mainSize = 24f;
        var subSize = MathF.Round(mainSize * 0.58f);
        var mainLineMult = (_latinMetrics ? 1.24f : 1.12f) * (float)lineScale;
        var rowGapPx = Dp((float)Math.Max(10, mainSize * (_latinMetrics ? 0.92 : 0.72) * gapScale));

        for (var i = 0; i < lyrics.Lines.Count; i++)
        {
            var line = lyrics.Lines[i];
            if (!line.Enabled) continue;

            var mainText = string.IsNullOrWhiteSpace(line.Content) ? "♪" : line.Content;
            var timeTags = string.IsNullOrWhiteSpace(line.Content) ? null : line.Attachments.GetInlineTimeTags();
            var segments = BuildKaraokeSegments(mainText, timeTags);

            var rowLayout = new LinearLayout(this)
            {
                Orientation = global::Android.Widget.Orientation.Vertical,
            };
            rowLayout.SetGravity(GravityFlags.Left);
            rowLayout.SetPadding(0, 0, Dp(14), rowGapPx);

            var originalView = new TextView(this)
            {
                Text = mainText,
                Gravity = GravityFlags.Left,
            };
            originalView.SetTextSize(ComplexUnitType.Sp, mainSize);
            originalView.SetTypeface(null, TypefaceStyle.Bold);
            originalView.SetLineSpacing(0f, mainLineMult);

            var translationView = new TextView(this)
            {
                Gravity = GravityFlags.Left,
                Visibility = ViewStates.Gone,
            };
            translationView.SetTextSize(ComplexUnitType.Sp, subSize);
            translationView.SetPadding(0, Dp(4), 0, 0);

            var holder = new LyricRowHolder
            {
                LineIndex = i,
                Root = rowLayout,
                Original = originalView,
                Translation = translationView,
                RawText = mainText,
                KaraokeSegments = segments,
            };

            var capturedLine = line;
            rowLayout.Click += (_, _) => SeekToLine(capturedLine, lyrics);
            rowLayout.AddView(originalView);
            rowLayout.AddView(translationView);

            _lyricsContainer.AddView(rowLayout, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            _rows.Add(holder);
            ApplyRowTranslation(holder, line);
            ApplyRowVisualState(holder, animate: false);
        }
    }

    private void RebuildTypographySizing()
    {
        _renderedLyrics = null; // 触发以新行距参数重排歌词视图
        EnsureLyricsListMatchesCurrent();
        RefreshFrameTick();
    }

    private void RefreshRowTranslations()
    {
        if (_renderedLyrics is null) return;
        foreach (var row in _rows)
        {
            if (row.LineIndex >= 0 && row.LineIndex < _renderedLyrics.Lines.Count)
                ApplyRowTranslation(row, _renderedLyrics.Lines[row.LineIndex]);
        }
    }

    private void ApplyRowTranslation(LyricRowHolder row, LyricsLine line)
    {
        var tr = App.Settings.ShowTranslation ? ResolveLineTranslation(line) : null;
        if (string.IsNullOrWhiteSpace(tr))
        {
            row.Translation.Text = "";
            row.Translation.Visibility = ViewStates.Gone;
        }
        else
        {
            row.Translation.Text = tr;
            row.Translation.Visibility = ViewStates.Visible;
        }
    }

    private string? ResolveLineTranslation(LyricsLine line)
    {
        var targetLower = App.Settings.EffectiveTargetLanguage.ToLowerInvariant();
        string? tr;
        if (targetLower.StartsWith("zh", StringComparison.Ordinal))
            tr = line.Attachments.Translation(null, targetLower);
        else if (App.Settings.ShowOnlyTargetTranslation)
            tr = line.Attachments.Translation(targetLower);
        else
            tr = line.Attachments.Translation(targetLower, null);

        if (string.IsNullOrWhiteSpace(tr)) return null;
        if (App.Settings.HideSameTranslation
            && string.Equals(tr.Trim(), line.Content.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;
        return tr;
    }

    private void ApplyRowVisualState(LyricRowHolder row, bool animate)
    {
        var isActive = row.LineIndex == _activeLineIndex;
        var isPast = _activeLineIndex >= 0 && row.LineIndex < _activeLineIndex;

        var targetColor = isActive
            ? Color.White
            : isPast
                ? LyricsPalette.BlendColor(_subdued, _background, 0.50)
                : _subdued;
        var targetAlpha = isActive ? 1.0f : (isPast ? 0.45f : 0.72f);
        var targetScale = isActive ? 1.045f : 1.0f;

        if (!isActive && row.KaraokeSegments.Count > 0)
            row.Original.Text = row.RawText;

        row.Root.PivotX = 0f;
        row.Root.PivotY = row.Original.Height > 0 ? row.Original.Height / 2f : Dp(16);

        if (animate)
        {
            row.Root.Animate()
                ?.ScaleX(targetScale)
                ?.ScaleY(targetScale)
                ?.Alpha(targetAlpha)
                ?.SetDuration(360)
                ?.SetInterpolator(RowEaseInterpolator)
                ?.Start();
            AnimateRowColor(row, targetColor);
        }
        else
        {
            row.Root.Animate()?.Cancel();
            row.Root.ScaleX = targetScale;
            row.Root.ScaleY = targetScale;
            row.Root.Alpha = targetAlpha;
            row.ColorAnimator?.Cancel();
            row.CurrentColor = targetColor;
            row.Original.SetTextColor(targetColor);
            row.Translation.SetTextColor(targetColor);
        }
    }

    private void AnimateRowColor(LyricRowHolder row, Color targetColor)
    {
        var start = row.CurrentColor;
        if (start == targetColor) return;
        row.ColorAnimator?.Cancel();
        if (ValueAnimator.OfArgb(start.ToArgb(), targetColor.ToArgb()) is not { } anim)
        {
            row.CurrentColor = targetColor;
            row.Original.SetTextColor(targetColor);
            row.Translation.SetTextColor(targetColor);
            return;
        }
        anim.SetDuration(340);
        anim.SetInterpolator(RowEaseInterpolator);
        anim.Update += (_, e) =>
        {
            if (_disposed || e.Animation?.AnimatedValue is not Java.Lang.Integer val) return;
            var c = new Color(val.IntValue());
            row.CurrentColor = c;
            row.Original.SetTextColor(c);
            row.Translation.SetTextColor(c);
        };
        row.ColorAnimator = anim;
        anim.Start();
    }

    private void ApplyKaraokeSpans(LyricRowHolder row, double elapsedSeconds)
    {
        var lit = Color.White;
        var unlit = LyricsPalette.BlendColor(_background, _subdued, 0.70);
        var spannable = new SpannableString(row.RawText);

        foreach (var seg in row.KaraokeSegments)
        {
            if (seg.End <= seg.Start || seg.End > row.RawText.Length) continue;
            var color = unlit;
            if (!seg.IsWhitespace)
            {
                if (elapsedSeconds >= seg.EndTime)
                    color = lit;
                else if (elapsedSeconds > seg.StartTime && seg.EndTime > seg.StartTime)
                {
                    var p = (elapsedSeconds - seg.StartTime) / (seg.EndTime - seg.StartTime);
                    color = LyricsPalette.BlendColor(unlit, lit, p);
                }
            }
            spannable.SetSpan(
                new ForegroundColorSpan(color),
                seg.Start,
                seg.End,
                SpanTypes.ExclusiveExclusive);
        }

        row.Original.TextFormatted = spannable;
    }

    private void FollowActiveLine(bool animate)
    {
        var row = _rows.FirstOrDefault(r => r.LineIndex == _activeLineIndex);
        if (row is null) return;

        row.Root.Post(() =>
        {
            if (_disposed || _lyricsScrollView.Height <= 0) return;
            var lineH = row.Original.Height > 0 ? row.Original.Height : row.Root.Height;
            var maxScroll = Math.Max(0, _lyricsContainer.Height - _lyricsScrollView.Height);
            var targetY = Math.Clamp(row.Root.Top + (lineH - _lyricsScrollView.Height) / 2, 0, maxScroll);

            _scrollAnimator?.Cancel();
            if (!animate)
            {
                _lyricsScrollView.ScrollTo(0, targetY);
                return;
            }

            if (ObjectAnimator.OfInt(_lyricsScrollView, "scrollY", targetY) is { } anim)
            {
                var distance = Math.Abs(targetY - _lyricsScrollView.ScrollY);
                anim.SetDuration((long)Math.Clamp(420 + distance * 0.25, 420, 620));
                anim.SetInterpolator(ScrollInterpolator);
                _scrollAnimator = anim;
                anim.Start();
            }
            else
            {
                _lyricsScrollView.SmoothScrollTo(0, targetY);
            }
        });
    }

    private void PauseAutoScroll()
    {
        if (_rows.Count == 0) return;
        _manualScrollAtMs = SystemClock.UptimeMillis();
        if (_manualScroll) return;
        _manualScroll = true;
        _scrollAnimator?.Cancel();
        _resumePill.Visibility = ViewStates.Visible;
        StartUiLoopIfNeeded();
    }

    private void ResumeAutoScroll()
    {
        _manualScroll = false;
        _resumePill.Visibility = ViewStates.Gone;
    }

    private void AutoSyncIfIdle()
    {
        var seconds = App.Settings.AutoSyncSeconds;
        if (seconds <= 0 || !_manualScroll || _rows.Count == 0) return;
        if (SystemClock.UptimeMillis() - _manualScrollAtMs < seconds * 1000L) return;
        ResumeAutoScroll();
        FollowActiveLine(animate: true);
    }

    private async Task SeekToSecondsAsync(double seconds)
    {
        _pendingSeekSeconds = Math.Max(0, seconds);
        _pendingSeekDeadlineMs = SystemClock.UptimeMillis() + 650;
        await App.NowPlaying.SeekAsync(TimeSpan.FromSeconds(_pendingSeekSeconds.Value));
        RunIfAlive(() =>
        {
            ResumeAutoScroll();
            RefreshFrameTick();
            FollowActiveLine(animate: true);
        });
    }

    private void SeekToLine(LyricsLine line, Lyrics lyrics)
    {
        var targetSec = Math.Max(
            0,
            line.Position - lyrics.TimeDelay - App.Coordinator.ManualOffsetSeconds - App.Coordinator.TrackOffsetSeconds);
        _ = SeekToSecondsAsync(targetSec);
    }

    private void RefreshCoverArt(TrackInfo? track, bool resetBeforeLoad = false)
    {
        var gen = Interlocked.Increment(ref _coverGeneration);
        if (track is null)
        {
            _lastCoverKey = null;
            _coverView.SetImageBitmap(null);
            _coverBackground = LyricsPalette.DefaultBackground;
            _coverSubdued = LyricsPalette.DefaultSubdued;
            ApplyActivePalette(animate: true);
            return;
        }

        if (resetBeforeLoad)
        {
            _lastCoverKey = null;
            _coverView.SetImageBitmap(null);
            _coverBackground = LyricsPalette.DefaultBackground;
            _coverSubdued = LyricsPalette.DefaultSubdued;
            ApplyActivePalette(animate: true);
        }

        var direct = App.NowPlaying.GetCurrentArtworkBitmap();
        if (direct is not null && !direct.IsRecycled)
        {
            _lastCoverKey = LyrxApp.TrackOffsetKey(track);
            ApplyCoverBitmap(direct);
            return;
        }

        var uri = App.NowPlaying.GetCurrentArtworkUri()
            ?? App.Coordinator.LyricsFor(track)?.Metadata.ArtworkUrl?.ToString();
        if (string.IsNullOrWhiteSpace(uri)) return;

        var expectedKey = LyrxApp.TrackOffsetKey(track);
        _ = Task.Run(async () =>
        {
            var bmp = await LoadCoverFromUriAsync(uri);
            RunIfAlive(() =>
            {
                if (gen != _coverGeneration || bmp is null) return;
                if (!SameTrackIdentity(App.NowPlaying.CurrentTrack, track)) return;
                _lastCoverKey = expectedKey;
                ApplyCoverBitmap(bmp);
            });
        });
    }

    private async Task<Bitmap?> LoadCoverFromUriAsync(string uri)
    {
        try
        {
            if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await CoverHttp.GetByteArrayAsync(uri);
                return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            }

            if (uri.StartsWith("content://", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = global::Android.Net.Uri.Parse(uri);
                if (parsed is null) return null;
                using var stream = ContentResolver?.OpenInputStream(parsed);
                return stream is not null ? BitmapFactory.DecodeStream(stream) : null;
            }
        }
        catch (Exception e)
        {
            AndroidLog.Write($"[cover] 加载失败: {e.Message}");
        }
        return null;
    }

    private void ApplyCoverBitmap(Bitmap bitmap)
    {
        _coverView.SetImageBitmap(bitmap);
        (_coverBackground, _coverSubdued) = LyricsPalette.FromCover(bitmap);
        ApplyActivePalette(animate: true);
    }

    private void ApplyActivePalette(bool animate)
    {
        var (bg, subdued) = LyricsPalette.Resolve(App.Settings.BackgroundMode, (_coverBackground, _coverSubdued));
        var previousBg = _background;
        _background = bg;
        _subdued = subdued;

        if (_rows.Count == 0)
            _emptyHintView.SetTextColor(_subdued);

        if (!animate || previousBg == bg)
        {
            ApplyPanelColors(bg);
            foreach (var row in _rows)
                ApplyRowVisualState(row, animate: false);
            return;
        }

        _bgAnimator?.Cancel();
        if (ValueAnimator.OfArgb(previousBg.ToArgb(), bg.ToArgb()) is not { } anim)
        {
            ApplyPanelColors(bg);
            foreach (var row in _rows)
                ApplyRowVisualState(row, animate: false);
            return;
        }

        anim.SetDuration(300);
        anim.SetInterpolator(RowEaseInterpolator);
        anim.Update += (_, e) =>
        {
            if (_disposed || e.Animation?.AnimatedValue is not Java.Lang.Integer val) return;
            ApplyPanelColors(new Color(val.IntValue()));
        };
        _bgAnimator = anim;
        anim.Start();

        foreach (var row in _rows)
            ApplyRowVisualState(row, animate: false);
    }

    private void ApplyPanelColors(Color bg)
    {
        var transparentSameRgb = Color.Argb(0, bg.R, bg.G, bg.B);
        _panelBgDrawable.SetColor(bg.ToArgb());
        _topFadeDrawable.SetColors([bg.ToArgb(), transparentSameRgb.ToArgb()]);
        _bottomFadeDrawable.SetColors([bg.ToArgb(), transparentSameRgb.ToArgb()]);
    }

    private void UpdateTransportAndButtons()
    {
        var controls = App.NowPlaying.GetControls();
        _prevBtnHost.Enabled = controls.CanPrevious;
        _playBtnHost.Enabled = controls.CanPlayPause;
        _nextBtnHost.Enabled = controls.CanNext;

        _prevBtnHost.Alpha = controls.CanPrevious ? 1f : 0.35f;
        _playBtnHost.Alpha = controls.CanPlayPause ? 1f : 0.35f;
        _nextBtnHost.Alpha = controls.CanNext ? 1f : 0.35f;

        _prevIcon.Configure(ClassicIconKind.Previous, LyricsPalette.MutedGray, iconFraction: 0.56f);
        _nextIcon.Configure(ClassicIconKind.Next, LyricsPalette.MutedGray, iconFraction: 0.56f);
        _playIcon.Configure(
            App.NowPlaying.IsPlaying ? ClassicIconKind.Pause : ClassicIconKind.Play,
            LyricsPalette.ChromeBackground,
            iconFraction: 0.52f);

        _translateIcon.Configure(
            ClassicIconKind.Translate,
            LyricsPalette.MutedGray,
            active: App.Settings.ShowTranslation,
            iconFraction: 0.60f);

        var totalOffset = App.Settings.ManualOffsetSeconds + App.Coordinator.TrackOffsetSeconds;
        var hasOffset = Math.Abs(totalOffset) >= 0.05;
        _offsetIcon.Configure(ClassicIconKind.Clock, LyricsPalette.MutedGray, active: hasOffset, iconFraction: 0.90f);
        if (hasOffset)
        {
            _offsetBadge.Text = $"{totalOffset:+0.#;-0.#;0}";
            _offsetBadge.Visibility = ViewStates.Visible;
        }
        else
        {
            _offsetBadge.Visibility = ViewStates.Gone;
        }
    }

    private void RefreshPermissionBanner()
    {
        var granted = App.NowPlaying.HasNotificationAccess();
        _permissionBanner.Visibility = granted ? ViewStates.Gone : ViewStates.Visible;
    }

    private void ToggleTranslationDisplay()
    {
        App.Settings.ShowTranslation = !App.Settings.ShowTranslation;
        App.SaveSettings();
        RefreshRowTranslations();
        UpdateTransportAndButtons();
        FollowActiveLine(animate: true);
    }

    private void OpenOffsetDialog() =>
        OffsetSheetDialog.Show(this, _offsetHost, () =>
        {
            UpdateTransportAndButtons();
            RefreshFrameTick();
            FollowActiveLine(animate: true);
        });

    private void OpenSettingsDialog() =>
        SettingsSheetDialog.Show(
            this,
            () =>
            {
                ApplyActivePalette(animate: true);
                RebuildTypographySizing();
                UpdateTransportAndButtons();
            },
            OpenNotificationListenerSettings);

    private void OpenNotificationListenerSettings()
    {
        try
        {
            StartActivity(new Intent(global::Android.Provider.Settings.ActionNotificationListenerSettings));
        }
        catch (Exception e)
        {
            AndroidLog.Write($"[perm] 无法打开通知读取设置页: {e.Message}");
            Toast.MakeText(this, "请在系统设置中搜索「通知使用权」并授权 Lyrx", ToastLength.Long)?.Show();
        }
    }

    private string FormatEmptyHint(LyricsStatusKind? status) =>
        !App.NowPlaying.HasNotificationAccess()
            ? "授予「通知使用权」后\n即可同步显示歌词"
            : _track is null
                ? "在你的播放器中播放一首歌"
                : status switch
                {
                    LyricsStatusKind.NotFound or LyricsStatusKind.Wrong => "这首歌暂无同步歌词",
                    LyricsStatusKind.HiddenByUser => "这首歌的歌词已隐藏",
                    _ => "正在查找歌词…",
                };

    private static bool SameTrackIdentity(TrackInfo? a, TrackInfo? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.Title, b.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMostlyLatin(Lyrics lyrics)
    {
        var latin = 0;
        var wide = 0;
        foreach (var line in lyrics.Lines)
        {
            if (!line.Enabled) continue;
            foreach (var c in line.Content)
            {
                if (c >= '\u2E80') wide++;
                else if (char.IsLetter(c)) latin++;
            }
        }
        return latin > wide;
    }

    private static IReadOnlyList<KaraokeSegment> BuildKaraokeSegments(string text, InlineTimeTags? timeTags)
    {
        if (timeTags is null || timeTags.Tags.Count == 0 || string.IsNullOrEmpty(text))
            return Array.Empty<KaraokeSegment>();

        var segments = new List<KaraokeSegment>(timeTags.Tags.Count + 4);
        var cursor = 0;
        var prevTime = 0.0;

        void AddRange(int start, int end, double startTime, double endTime)
        {
            if (end <= start) return;

            var visibleStart = start;
            while (visibleStart < end && char.IsWhiteSpace(text[visibleStart]))
                visibleStart++;

            if (visibleStart == end)
            {
                segments.Add(new KaraokeSegment(start, end, startTime, startTime, IsWhitespace: true));
                return;
            }
            if (visibleStart > start)
                segments.Add(new KaraokeSegment(start, visibleStart, startTime, startTime, IsWhitespace: true));

            var visibleEnd = end;
            while (visibleEnd > visibleStart && char.IsWhiteSpace(text[visibleEnd - 1]))
                visibleEnd--;

            segments.Add(new KaraokeSegment(visibleStart, visibleEnd, startTime, endTime, IsWhitespace: false));
            if (visibleEnd < end)
                segments.Add(new KaraokeSegment(visibleEnd, end, endTime, endTime, IsWhitespace: true));
        }

        foreach (var tag in timeTags.Tags)
        {
            var end = Math.Clamp(tag.Index, 0, text.Length);
            var time = Math.Max(prevTime, double.IsFinite(tag.Time) ? tag.Time : prevTime);
            if (end > cursor)
            {
                if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
                    end++;
                AddRange(cursor, end, prevTime, time);
                cursor = end;
            }
            prevTime = time;
        }

        if (cursor < text.Length)
        {
            var tailEnd = timeTags.Duration is { } dur && double.IsFinite(dur) ? Math.Max(prevTime, dur) : prevTime;
            AddRange(cursor, text.Length, prevTime, tailEnd);
        }

        return segments.Any(s => !s.IsWhitespace && (s.EndTime > s.StartTime || s.EndTime > 0))
            ? segments
            : Array.Empty<KaraokeSegment>();
    }

    private static string FormatTime(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
    }

    private int Dp(float dp) => (int)Math.Round(dp * (Resources?.DisplayMetrics?.Density ?? 2f));
}
