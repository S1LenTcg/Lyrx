using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Musebase.Core;
using Musebase.Engine;
using Musebase.Windows.Services;
using Windows.Media;

namespace Musebase.Windows;

internal sealed class ClassicLyricsWindow : Window
{
    private static readonly Brush Chrome = new SolidColorBrush(Color.FromRgb(18, 18, 18));
    private static readonly Color DefaultBackground = Color.FromRgb(51, 51, 51);
    private static readonly Color DefaultSubdued = Color.FromRgb(179, 179, 179);
    private static readonly Brush Muted = new SolidColorBrush(DefaultSubdued);
    private static readonly KeySpline ScrollSpline = new(0.16, 1.0, 0.3, 1.0);
    private static readonly IEasingFunction TranslationEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly DependencyProperty FollowOffsetProperty = DependencyProperty.Register(
        "FollowOffset", typeof(double), typeof(ClassicLyricsWindow),
        new PropertyMetadata(0d, (owner, args) =>
            ((ClassicLyricsWindow)owner)._scroll.ScrollToVerticalOffset((double)args.NewValue)));
    private static readonly DependencyProperty TranslationProgressProperty = DependencyProperty.Register(
        "TranslationProgress", typeof(double), typeof(ClassicLyricsWindow),
        new PropertyMetadata(0d, (owner, args) =>
            ((ClassicLyricsWindow)owner).OnTranslationProgressChanged((double)args.NewValue)));

    private const int WmExitSizeMove = 0x0232;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRound = 2;

    private readonly LyricsCoordinator _coordinator;
    private readonly NowPlayingService _player;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly StackPanel _lines = new();
    private readonly ScrollViewer _scroll;
    private readonly Border _panel;
    private readonly Border _footerBar;
    private readonly Border _topFade;
    private readonly Border _bottomFade;
    private readonly GradientStop _topFadeOpaqueStop = new(Color.FromArgb(240, DefaultBackground.R, DefaultBackground.G, DefaultBackground.B), 0);
    private readonly GradientStop _topFadeClearStop = new(Color.FromArgb(0, DefaultBackground.R, DefaultBackground.G, DefaultBackground.B), 1);
    private readonly GradientStop _bottomFadeClearStop = new(Color.FromArgb(0, DefaultBackground.R, DefaultBackground.G, DefaultBackground.B), 0);
    private readonly GradientStop _bottomFadeOpaqueStop = new(Color.FromArgb(240, DefaultBackground.R, DefaultBackground.G, DefaultBackground.B), 1);
    private readonly Border _transportToast;
    private readonly TextBlock _transportToastText;
    private DispatcherTimer? _transportToastTimer;
    private DispatcherTimer? _captionHideTimer;
    private readonly TextBlock _title = TrackLabel(Brushes.White, 14, 18);
    private readonly TextBlock _artist = TrackLabel(Muted, 12, 16);
    private readonly TextBlock _elapsed = TimeLabel(TextAlignment.Right);
    private readonly TextBlock _remaining = TimeLabel(TextAlignment.Left);
    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill };
    private readonly ClassicPlaybackSlider _progress = new();
    private readonly Button _shuffle;
    private readonly Button _previous;
    private readonly Button _play;
    private readonly Button _next;
    private readonly Button _repeat;
    private readonly Button _resume;
    private readonly Button _translate;
    private readonly Button _shareBtn;
    private readonly Button _fullscreen;
    private readonly Border _captionBar;
    private readonly StackPanel _captionButtonsPanel;
    private readonly Action _onToggleTranslation;
    private readonly WindowChrome _chrome;
    private const int MaxShareSelectionCount = 6;

    private readonly Dictionary<int, LyricRow> _rows = new();
    private TrackInfo? _track;
    private Lyrics? _lyrics;
    private LyricsStatusKind? _status;
    private CancellationTokenSource? _coverCts;
    private long _coverGeneration;
    private bool _coverInFlight;
    private bool _coverFromSmtc;
    private bool _coverWaitingOnlineFallback;
    private bool _coverTriedArtworkUrl;
    private long _coverThumbnailVersion = -1;
    private byte[]? _coverBytes;
    private int _activeIndex = -1;
    private Color _subdued = DefaultSubdued;
    private Color _background = DefaultBackground;
    private Color _coverSubdued = DefaultSubdued;
    private Color _coverBackground = DefaultBackground;
    private bool _manualScroll;
    private long _manualScrollAt;
    private bool _resumeScrollPending;
    private bool _captionVisible;
    private Point? _pendingWindowDragStart;
    private bool _mouseDownStartedOutsideRow;
    private int? _pressedRowIndex;
    private long _suppressRowClickUntilTick;
    private double? _pendingSeekSeconds;
    private int? _pendingSeekLineIndex;
    private long _pendingSeekDeadlineTick;
    private bool _seekInFlight;
    private long _seekGeneration;
    private bool _isFullscreen;
    private Rect _restoreBounds;
    private bool _wasMaximized;
    private Action? _relayoutFooter;
    private bool _isAnimatingTranslation;
    private bool _waitingForTranslationAnimation;
    private bool _chineseLyrics;
    private double _lineHeightScale = 1.0;
    private double _rowGapScale = 1.0;
    private LyricRow? _translationAnchorRow;
    private double _translationAnchorViewportY;

    private bool _isShareSelecting;
    private readonly HashSet<int> _selectedShareLines = new();
    private Lyrics? _shareSelectionLyrics;
    private LyricShareWindow? _shareWindow;
    private DispatcherTimer? _shareLimitHintTimer;
    private readonly Border _shareHeaderBar = null!;
    private readonly Button _shareCancelBtn = null!;
    private readonly TextBlock _shareCountText = null!;
    private readonly TextBlock _shareLimitHintText = null!;
    private readonly Border _shareActionBar = null!;
    private readonly Button _shareContinueBtn = null!;
    private int? _hoveredShareLineIndex;

    public event Action? ViewStateChanged;

    public ClassicLyricsWindow(
        LyricsCoordinator coordinator,
        NowPlayingService player,
        AppSettings settings,
        Action<double?> adjustOffset,
        Action openSettings,
        Action onToggleTranslation,
        ContextMenu? windowMenu = null)
    {
        _coordinator = coordinator;
        _player = player;
        _settings = settings;
        _onToggleTranslation = onToggleTranslation;
        ContextMenu = windowMenu;
        Title = "Lyrx";
        ShowInTaskbar = true;
        Icon = TrayIcon.LoadAppIcon();
        MinWidth = 480;
        MinHeight = 390;
        Topmost = _settings.ClassicTopmost;
        RestorePlacement();
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        _chrome = new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(5, 4, 5, 5),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };
        WindowChrome.SetWindowChrome(this, _chrome);
        Background = Chrome;
        FontFamily = ClassicLyricsFonts.Body;
        var lyricsFont = settings.ClassicLyricsFont is { } fontId
            ? ClassicLyricsFonts.GetChoices().FirstOrDefault(font => font.Id == fontId)?.Font
            : null;
        TextElement.SetFontFamily(_lines, lyricsFont ?? ClassicLyricsFonts.Family);

        var layout = new DockPanel();
        var lyricsView = new Grid();
        var footer = new Grid { Margin = new Thickness(0), UseLayoutRounding = true };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.RowDefinitions.Add(new RowDefinition());
        footer.RowDefinitions.Add(new RowDefinition());
        _footerBar = new Border
        {
            Child = footer,
            Background = Chrome,
            BorderBrush = CreateOffsetAlphaBrush(24, 255, 255, 255),
            BorderThickness = new Thickness(0, 1, 0, 0),
            UseLayoutRounding = true,
        };
        DockPanel.SetDock(_footerBar, Dock.Bottom);
        layout.Children.Add(_footerBar);

        var trackInfo = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true };
        var coverContent = new Grid();
        coverContent.Children.Add(new TextBlock
        {
            Text = "♫", Foreground = Muted, FontSize = 22, FontFamily = new FontFamily("Segoe UI Symbol"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        coverContent.Children.Add(_cover);
        var coverFrame = new Border
        {
            Child = coverContent, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
            CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 12, 0),
        };
        DockPanel.SetDock(coverFrame, Dock.Left);
        trackInfo.Children.Add(coverFrame);
        var trackText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        trackText.Children.Add(_title);
        trackText.Children.Add(_artist);
        trackInfo.Children.Add(trackText);
        footer.Children.Add(trackInfo);

        var transport = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(transport);

        _adjustOffset = adjustOffset;
        if (_coordinator.TrackOffsetSeconds == 0 && _coordinator.CurrentTrack is { } initialTrack)
            _coordinator.TrackOffsetSeconds = _settings.GetTrackOffset(TrackOffsetKey(initialTrack));

        var windowControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _adjustScopePerTrack = _settings.ClassicOffsetPerTrack;
        _offsetScopeWasPerTrack = _adjustScopePerTrack;
        _offsetBtn = CreateOffsetTriggerButton();
        _offsetPopup = CreateOffsetPopup();
        UpdateOffsetButton();
        _translate = ChromeButton(ClassicTransportIcon.Translate, "切换双语歌词", ToggleTranslation);
        UpdateTranslateButton();
        _shareBtn = ChromeButton(ClassicTransportIcon.Share, "分享歌词…", ToggleShareSelectionFromButton);
        UpdateShareButton();
        var settingsBtn = ChromeButton(ClassicTransportIcon.Settings, "设置", openSettings);
        _fullscreen = ChromeButton(ClassicTransportIcon.Fullscreen, "全屏 (F11)", ToggleFullscreen);
        _fullscreen.Margin = new Thickness(5, 0, 2, 0);
        windowControls.Children.Add(_offsetBtn);
        windowControls.Children.Add(_translate);
        windowControls.Children.Add(_shareBtn);
        windowControls.Children.Add(settingsBtn);
        windowControls.Children.Add(_fullscreen);
        Grid.SetColumn(windowControls, 2);
        footer.Children.Add(windowControls);

        var captionInk = CreateOffsetAlphaBrush(244, 255, 255, 255);
        var minimize = ChromeButton(
            ClassicTransportIcon.Minimize,
            "最小化",
            () => WindowState = WindowState.Minimized,
            height: 28,
            width: 32,
            glyphSize: 16,
            cornerRadius: 14,
            foreground: captionInk,
            hoverBackground: CreateOffsetAlphaBrush(42, 255, 255, 255));
        minimize.Margin = new Thickness(0);
        var close = ChromeButton(
            ClassicTransportIcon.Close,
            "关闭",
            Close,
            height: 28,
            width: 32,
            glyphSize: 16,
            cornerRadius: 14,
            foreground: captionInk,
            hoverBackground: CreateOffsetAlphaBrush(195, 232, 54, 68));
        close.Margin = new Thickness(2, 0, 0, 0);
        _captionButtonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _captionButtonsPanel.Children.Add(minimize);
        _captionButtonsPanel.Children.Add(close);
        _captionBar = BuildLiquidGlassCaptionCapsule(_captionButtonsPanel);
        Panel.SetZIndex(_captionBar, 20);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _shuffle = TransportButton(ClassicTransportIcon.Shuffle, "随机播放", _player.ToggleShuffleAsync, onRejected: () => ShowTransportFeedback("当前播放器未开放系统随机控制"));
        _previous = TransportButton(ClassicTransportIcon.Previous, "上一首", _player.SkipPreviousAsync);
        _play = TransportButton(ClassicTransportIcon.Play, "播放", _player.TogglePlayPauseAsync, round: true);
        _next = TransportButton(ClassicTransportIcon.Next, "下一首", _player.SkipNextAsync);
        _repeat = TransportButton(ClassicTransportIcon.Repeat, "顺序播放", _player.ToggleRepeatModeAsync, onRejected: () => ShowTransportFeedback("当前播放器未开放系统循环控制"));
        buttons.Children.Add(_shuffle);
        buttons.Children.Add(_previous);
        buttons.Children.Add(_play);
        buttons.Children.Add(_next);
        buttons.Children.Add(_repeat);

        transport.Children.Add(buttons);

        // 底部进度条：支持鼠标悬停、拖拽、点击与键盘步进跳转播放（或校准时钟）
        _progress.ValueChanged += (_, _) =>
        {
            if (_progress.IsUserSeeking)
                Refresh();
        };
        _progress.SeekRequested += seconds => _ = SeekToSecondsAsync(seconds);
        var timeline = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        Grid.SetColumn(_progress, 1);
        Grid.SetColumn(_remaining, 2);
        timeline.Children.Add(_elapsed);
        timeline.Children.Add(_remaining);
        timeline.Children.Add(_progress);
        transport.Children.Add(timeline);

        void LayoutFooter(double width)
        {
            var compact = width < 600;
            var fullscreenBar = !compact && _isFullscreen;
            _footerBar.Padding = compact
                ? new Thickness(12, 8, 12, 10)
                : fullscreenBar
                    ? new Thickness(26, 10, 26, 12)
                    : new Thickness(18, 8, 18, 8);
            _captionBar.Margin = compact
                ? new Thickness(0, 12, 12, 0)
                : fullscreenBar
                    ? new Thickness(0, 20, 26, 0)
                    : new Thickness(0, 16, 18, 0);
            footer.Margin = new Thickness(0);
            var coverSize = compact ? 44 : fullscreenBar ? 64 : 56;
            coverFrame.Width = coverSize;
            coverFrame.Height = coverSize;
            coverContent.Clip = new RectangleGeometry(new Rect(0, 0, coverSize, coverSize), 4, 4);
            var innerWidth = Math.Max(0, width - _footerBar.Padding.Left - _footerBar.Padding.Right);
            var transportWidth = compact ? 0 : Math.Clamp(innerWidth * 0.44, 280, 722);
            _footerBar.Height = compact ? 124 : fullscreenBar ? 102 : 84;
            footer.RowDefinitions[0].Height = compact ? new GridLength(56) : new GridLength(1, GridUnitType.Star);
            footer.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            footer.ColumnDefinitions[1].Width = compact
                ? new GridLength(0)
                : new GridLength(transportWidth);
            footer.ColumnDefinitions[2].MinWidth = compact ? 0 : 232;
            footer.ColumnDefinitions[2].Width = compact
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
            trackInfo.HorizontalAlignment = HorizontalAlignment.Stretch;
            trackInfo.Margin = new Thickness(0, 0, 12, 0);
            trackInfo.MaxWidth = double.PositiveInfinity;
            Grid.SetRow(transport, compact ? 1 : 0);
            Grid.SetColumn(transport, compact ? 0 : 1);
            Grid.SetColumnSpan(transport, compact ? 3 : 1);
        }
        _relayoutFooter = () =>
        {
            var hostW = layout.ActualWidth > 0 ? layout.ActualWidth : Width;
            LayoutFooter(Math.Max(0, hostW));
        };
        LayoutFooter(Width);
        _footerBar.SizeChanged += (_, e) => { if (e.WidthChanged) LayoutFooter(e.NewSize.Width); };

        _scroll = new ScrollViewer
        {
            Content = _lines,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        lyricsView.Children.Add(_scroll);
        _resume = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 28),
            MinWidth = 120,
            Height = 48,
            Padding = new Thickness(18, 0, 18, 0),
            Background = Brushes.White,
            Foreground = Brushes.Black,
            Template = TransportTemplate(cornerRadius: 24),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "同步到当前播放位置（右键微调时间）",
        };
        var resumeContent = new StackPanel { Orientation = Orientation.Horizontal };
        resumeContent.Children.Add(new ClassicTransportGlyph(ClassicTransportIcon.Sync)
        {
            Tint = Brushes.Black, Width = 24, Height = 28, Margin = new Thickness(0, 0, 8, 0),
        });
        resumeContent.Children.Add(new TextBlock { Text = "同步", VerticalAlignment = VerticalAlignment.Center });
        _resume.Content = resumeContent;
        AutomationProperties.SetName(_resume, "同步歌词");
        var syncMenu = new ContextMenu { FontFamily = ClassicLyricsFonts.Body };
        var globalGroup = new MenuItem { Header = "所有歌曲" };
        foreach (var (label, offset) in new (string, double?)[]
        {
            ("歌词提前 0.1 秒", 0.1), ("歌词延后 0.1 秒", -0.1),
            ("歌词提前 0.2 秒", 0.2), ("歌词延后 0.2 秒", -0.2),
            ("歌词提前 0.5 秒", 0.5), ("歌词延后 0.5 秒", -0.5),
            ("重置歌词时间偏移", null),
        })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { adjustOffset(offset); ResumeAutoScroll(); Refresh(); };
            globalGroup.Items.Add(item);
        }
        syncMenu.Items.Add(globalGroup);

        // 仅当前这首歌：吸收"音源与歌词时间轴之间的固定错位"
        // （例如 B 站视频比专辑版多几秒片头，或影片是现场版/剪辑版），调一次即记住。
        var trackGroup = new MenuItem { Header = "仅此曲（会记住）" };
        foreach (var (label, offset) in new (string, double?)[]
        {
            ("歌词提前 0.2 秒", 0.2), ("歌词延后 0.2 秒", -0.2),
            ("歌词提前 0.5 秒", 0.5), ("歌词延后 0.5 秒", -0.5),
            ("清除此曲偏移", null),
        })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { AdjustTrackOffset(offset); Refresh(); };
            trackGroup.Items.Add(item);
        }
        syncMenu.Items.Add(trackGroup);
        _resume.ContextMenu = syncMenu;
        lyricsView.Children.Add(_resume);
        (_shareHeaderBar, _shareCancelBtn, _shareCountText, _shareLimitHintText) = BuildShareHeaderBar();
        (_shareActionBar, _shareContinueBtn) = BuildShareActionBar();
        _topFade = new Border
        {
            Height = 28,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush(
                new GradientStopCollection { _topFadeOpaqueStop, _topFadeClearStop },
                new Point(0, 0), new Point(0, 1)),
        };
        Panel.SetZIndex(_topFade, 6);
        _bottomFade = new Border
        {
            Height = 44,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush(
                new GradientStopCollection { _bottomFadeClearStop, _bottomFadeOpaqueStop },
                new Point(0, 0), new Point(0, 1)),
        };
        Panel.SetZIndex(_bottomFade, 6);
        _transportToastText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _transportToast = new Border
        {
            Child = _transportToastText,
            Background = CreateOffsetAlphaBrush(225, 24, 24, 26),
            BorderBrush = CreateOffsetAlphaBrush(42, 255, 255, 255),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 18),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        Panel.SetZIndex(_transportToast, 19);
        lyricsView.Children.Add(_topFade);
        lyricsView.Children.Add(_bottomFade);
        lyricsView.Children.Add(_transportToast);
        lyricsView.Children.Add(_shareHeaderBar);
        lyricsView.Children.Add(_shareActionBar);
        lyricsView.Children.Add(_captionBar);
        _resume.Click += (_, _) =>
        {
            _coordinator.RefreshCurrentLine();
            ResumeAutoScroll();
            Refresh();
        };

        void AdjustTrackOffset(double? offset)
        {
            var next = offset is null
                ? 0
                : Math.Clamp(Math.Round(_coordinator.TrackOffsetSeconds + offset.Value, 2), -60d, 60d);
            if (Math.Abs(next) < 0.005) next = 0;
            _coordinator.TrackOffsetSeconds = next;
            _settings.SetTrackOffset(TrackOffsetKey(_track ?? _coordinator.CurrentTrack), next);
            _settings.Save();
            UpdateOffsetButton();
            Refresh();
        }
        _adjustTrackOffsetAction = AdjustTrackOffset;

        _panel = new Border
        {
            Child = lyricsView,
            Background = new SolidColorBrush(DefaultBackground),
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            ClipToBounds = true,
        };
        layout.Children.Add(_panel);
        Content = layout;

        SizeChanged += (_, e) => { if (e.WidthChanged) UpdatePanelFrame(); };
        _scroll.SizeChanged += (_, _) => ResizeLyrics();
        _scroll.PreviewMouseWheel += (_, _) => PauseAutoScroll();
        _scroll.ManipulationStarting += (_, _) => PauseAutoScroll();
        _scroll.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
                PauseAutoScroll();
        };
        SetCurrentValue(TranslationProgressProperty, _settings.ClassicShowTranslation ? 1d : 0d);
        _timer.Tick += (_, _) => Refresh();
        _coordinator.TranslationStatusChanged += _ => Dispatcher.BeginInvoke(() => SyncRowTranslations(animateIfNew: true));
        SourceInitialized += (_, _) =>
        {
            UpdateWindowCornerPreference();
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WndProc);
        };
        Loaded += (_, _) => { UpdatePanelFrame(); ApplyActivePalette(); Refresh(); _timer.Start(); };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _shareLimitHintTimer?.Stop();
            _transportToastTimer?.Stop();
            _captionHideTimer?.Stop();
            _progress.CancelPendingSeek();
            CancelCoverLoad();
            if (_shareWindow is { IsLoaded: true })
            {
                try { _shareWindow.Close(); } catch { }
            }
            SavePlacement();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (HandleOffsetKeyDown(e)) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape && _isShareSelecting)
            {
                ExitShareSelectionMode(resumeAutoScroll: true);
                e.Handled = true;
                return;
            }
            if (key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (key == Key.Escape && _isFullscreen)
            {
                SetFullscreen(false);
                e.Handled = true;
            }
        };
        PreviewMouseMove += (_, e) =>
        {
            var pos = e.GetPosition(this);
            var inCaptionRevealZone = !_isShareSelecting
                && (pos.Y >= 0 && pos.Y <= 44 || (pos.Y <= 86 && pos.X >= Math.Max(0, ActualWidth - 200)) || _captionBar.IsMouseOver);
            if (inCaptionRevealZone)
                RevealCaptionBar();
            else
                ScheduleCaptionBarHide();

            if (_pendingWindowDragStart is { } origin)
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    _pendingWindowDragStart = null;
                }
                else if (Math.Abs(pos.X - origin.X) >= SystemParameters.MinimumHorizontalDragDistance
                    || Math.Abs(pos.Y - origin.Y) >= SystemParameters.MinimumVerticalDragDistance)
                {
                    _pendingWindowDragStart = null;
                    if (!_isFullscreen && WindowState == WindowState.Normal)
                    {
                        try { DragMove(); } catch { }
                    }
                }
            }
        };
        MouseLeave += (_, _) =>
        {
            _pendingWindowDragStart = null;
            ScheduleCaptionBarHide(delayMs: 120);
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            var source = e.OriginalSource as DependencyObject;
            var row = FindAncestorLyricRow(source);
            if (IsInteractiveTarget(source))
            {
                _pendingWindowDragStart = null;
                _mouseDownStartedOutsideRow = row is null;
                return;
            }
            _mouseDownStartedOutsideRow = true;
            _pressedRowIndex = null;
            if (e.ClickCount == 2)
            {
                _pendingWindowDragStart = null;
                _suppressRowClickUntilTick = Environment.TickCount64 + 180;
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
            if (!_isFullscreen && WindowState == WindowState.Normal && e.LeftButton == MouseButtonState.Pressed)
                _pendingWindowDragStart = e.GetPosition(this);
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            _pendingWindowDragStart = null;
            if (FindAncestorLyricRow(e.OriginalSource as DependencyObject) is null)
            {
                _pressedRowIndex = null;
                _mouseDownStartedOutsideRow = false;
            }
        };
    }

    private static TextBlock TrackLabel(Brush color, double size, double lineHeight)
    {
        var label = new TextBlock
        {
            Foreground = color, FontSize = size, FontWeight = FontWeights.Normal,
            LineHeight = lineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        };
        label.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = label });
        return label;
    }

    private static TextBlock TimeLabel(TextAlignment alignment)
    {
        var label = new TextBlock
        {
            Foreground = Muted, FontSize = 12, Text = "–:––", TextAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        return label;
    }

    private Button TransportButton(
        ClassicTransportIcon glyph,
        string label,
        Func<Task<bool>> action,
        bool round = false,
        Action? onRejected = null)
    {
        var icon = new ClassicTransportGlyph(glyph);
        var button = new Button
        {
            Content = icon,
            Width = round ? 36 : 34,
            Height = 36,
            Margin = new Thickness(3, 0, 3, 0),
            Foreground = round ? Chrome : Muted,
            Background = round ? Brushes.White : Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            ToolTip = label,
            Template = TransportTemplate(cornerRadius: round ? 18 : 4),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
        };
        icon.SetBinding(ClassicTransportGlyph.TintProperty, new Binding(nameof(Button.Foreground)) { Source = button });
        AutomationProperties.SetName(button, label);
        ToolTipService.SetShowOnDisabled(button, true);
        var buttonPending = false;
        button.Click += async (_, _) =>
        {
            if (buttonPending) return;
            buttonPending = true;
            try
            {
                var task = action();
                var completed = await Task.WhenAny(task, Task.Delay(1500));
                if (ReferenceEquals(completed, task))
                {
                    if (!await task) onRejected?.Invoke();
                }
            }
            finally
            {
                buttonPending = false;
                Refresh();
            }
        };
        return button;
    }

    private static ControlTemplate TransportTemplate(double cornerRadius, Brush? hoverBackground = null)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Button.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding(nameof(Button.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.35));
        template.Triggers.Add(disabled);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        if (cornerRadius >= 18) hover.Setters.Add(new Setter(OpacityProperty, 0.86));
        else hover.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
        if (hoverBackground is not null)
            hover.Setters.Add(new Setter(BackgroundProperty, hoverBackground));
        template.Triggers.Add(hover);
        return template;
    }

    private void Refresh()
    {
        AutoSyncIfIdle();

        var track = _coordinator.CurrentTrack;
        // 仅在 CurrentLyricsTrackKey 与当前曲目一致时才读取歌词及其 ArtworkUrl，
        // 避免切歌瞬间 Dispatcher 尚未清空旧歌词时把上一首歌词的封面 URL 写入新歌封面缓存。
        var lyrics = _coordinator.LyricsFor(track);
        var status = _coordinator.CurrentStatus?.Kind;
        var songChanged = !SameTrackIdentity(track, _track);
        var trackChanged = !Equals(track, _track);

        UpdateOffsetButton();
        if (songChanged)
        {
            ExitShareSelectionMode(resumeAutoScroll: false);
            _track = track;
            _coordinator.TrackOffsetSeconds = _settings.GetTrackOffset(TrackOffsetKey(track));
            UpdateOffsetButton();
            _activeIndex = -1;
            _seekGeneration++;
            _seekInFlight = false;
            _pendingSeekSeconds = null;
            _pendingSeekDeadlineTick = 0;
            _progress.CancelPendingSeek();
            CancelScrollAnimation();
            CancelCoverLoad();
            _coverInFlight = false;
            _coverFromSmtc = false;
            _coverWaitingOnlineFallback = false;
            _coverTriedArtworkUrl = false;
            _coverThumbnailVersion = _player.ThumbnailVersion;
            _coverBytes = null;
            _title.Text = track?.Title ?? "等待播放";
            _artist.Text = track?.Artist ?? "";
            _cover.Source = null;
            if (track is not null) StartCoverLoad(track, lyrics?.Metadata.ArtworkUrl);
            else ResetCoverPalette();
        }
        else if (trackChanged)
        {
            // 时长或专辑后补等元数据刷新：保留当前封面、进度条跳转与歌词视图，不重复闪烁或重拉封面。
            _track = track;
        }

        if (songChanged || !ReferenceEquals(lyrics, _lyrics) || status != _status || _lines.Children.Count == 0)
        {
            var animateReplacement = !songChanged
                && _settings.ClassicShowTranslation
                && _waitingForTranslationAnimation
                && lyrics is not null
                && HasAnyTranslation(lyrics);
            _lyrics = lyrics;
            _status = status;
            if (songChanged)
            {
                _waitingForTranslationAnimation = false;
                CancelTranslationAnimation();
                SetCurrentValue(TranslationProgressProperty, _settings.ClassicShowTranslation ? 1d : 0d);
            }
            else if (animateReplacement)
            {
                CancelTranslationAnimation();
                SetCurrentValue(TranslationProgressProperty, 0d);
            }
            RenderLyrics(preserveScroll: !songChanged);
            if (animateReplacement)
            {
                _waitingForTranslationAnimation = false;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AnimateTranslation(1d));
            }
        }

        if (!songChanged && track is not null)
        {
            var preferOnlineCover = Services.NowPlayingService.IsBrowserOrVideoSource(track.SourceAppId);
            var smtcVersionChanged = _player.ThumbnailVersion != _coverThumbnailVersion;
            var authoritativeSmtcArrived = !preferOnlineCover && smtcVersionChanged && _player.HasThumbnail;
            var delayedArtworkArrived = !_coverInFlight && !_coverTriedArtworkUrl && lyrics?.Metadata.ArtworkUrl is not null
                && (_cover.Source is null || (preferOnlineCover && _coverFromSmtc));
            var smtcThumbnailChanged = smtcVersionChanged && (
                (authoritativeSmtcArrived && (!_coverInFlight || _coverWaitingOnlineFallback || _coverFromSmtc || _cover.Source is null))
                || (!_coverInFlight && (_cover.Source is null || _coverFromSmtc)));
            if (delayedArtworkArrived || smtcThumbnailChanged)
                StartCoverLoad(track, lyrics?.Metadata.ArtworkUrl);
        }

        var position = track is null ? null : _player.GetEstimatedPosition();
        var duration = track?.Duration ?? (lyrics?.Length > 0 ? TimeSpan.FromSeconds(lyrics.Length.Value) : (lyrics?.Lines.Count > 0 ? TimeSpan.FromSeconds(lyrics.Lines[^1].Position + 15) : null));
        if (position is { } p) position = TimeSpan.FromTicks(Math.Clamp(p.Ticks, 0, Math.Max(0, duration?.Ticks ?? long.MaxValue)));
        var state = _player.GetTransportState();
        UpdateTransport(state);

        // 进度条：反映真实播放位置，支持拖拽、键盘步进与实时时间预览
        var total = duration is { TotalSeconds: > 0 } known ? known : (TimeSpan?)null;
        _progress.IsEnabled = track is not null && total is not null;
        _progress.Maximum = total?.TotalSeconds ?? 1;

        var liveSeconds = Math.Clamp(position?.TotalSeconds ?? 0, 0, _progress.Maximum);
        if (_pendingSeekSeconds is { } pendingTarget && !_seekInFlight)
        {
            if (Environment.TickCount64 >= _pendingSeekDeadlineTick
                || Math.Abs(liveSeconds - pendingTarget) <= 1.2)
            {
                _pendingSeekSeconds = null;
                _pendingSeekLineIndex = null;
            }
        }

        if (!_progress.IsUserSeeking && _pendingSeekSeconds is null)
        {
            _progress.Value = liveSeconds;
            _elapsed.Text = position is { } elapsed ? FormatTime(elapsed) : "–:––";
            _remaining.Text = total is { } remainingTotal && position is { } current
                ? $"−{FormatTime(remainingTotal - current)}"
                : "–:––";
        }
        else
        {
            var previewSeconds = _progress.IsUserSeeking
                ? _progress.Value
                : Math.Clamp(_pendingSeekSeconds ?? _progress.Value, 0, _progress.Maximum);
            if (!_progress.IsUserSeeking)
                _progress.Value = previewSeconds;
            var cur = TimeSpan.FromSeconds(previewSeconds);
            _elapsed.Text = FormatTime(cur);
            _remaining.Text = total is { } remTotal ? $"−{FormatTime(remTotal - cur)}" : "–:––";
        }

        if (lyrics is null || (position is null && !_progress.IsUserSeeking && _pendingSeekSeconds is null)) return;

        var effectiveSeconds = _progress.IsUserSeeking
            ? _progress.Value
            : (_pendingSeekSeconds is { } pending
                ? Math.Clamp(pending, 0, _progress.Maximum)
                : position!.Value.TotalSeconds);
        var adjustEpsilon = _pendingSeekSeconds is not null ? 1e-4 : 0.0;
        var adjusted = effectiveSeconds + adjustEpsilon + lyrics.TimeDelay + _coordinator.ManualOffsetSeconds + _coordinator.TrackOffsetSeconds;
        var (active, _) = lyrics.LineIndexesAt(adjusted);
        var index = _pendingSeekSeconds is not null && _pendingSeekLineIndex is { } pendingIdx && _rows.ContainsKey(pendingIdx)
            ? pendingIdx
            : (active ?? -1);
        if (_activeIndex != index)
        {
            var previousIndex = _activeIndex;
            _activeIndex = index;
            if (!_isShareSelecting)
            {
                if (previousIndex < 0)
                {
                    // 初次加载或切歌后定位首行：直接对齐避免整页大跨度飞跃
                    CancelScrollAnimation();
                    foreach (var (rowIndex, row) in _rows)
                    {
                        SetRowVisual(row, rowIndex, animate: false);
                        AutomationProperties.SetItemStatus(row, rowIndex == index ? "当前歌词" : "");
                    }
                    if (!_manualScroll && !_isAnimatingTranslation)
                        FollowActiveLine(animate: false);
                }
                else
                {
                    // 同曲内单句切换、点击歌词跳转、点击或拖动进度条：统一使用平滑过渡动画
                    if (_rows.TryGetValue(previousIndex, out var previousRow))
                    {
                        SetRowVisual(previousRow, previousIndex, animate: true);
                        AutomationProperties.SetItemStatus(previousRow, "");
                    }
                    if (_rows.TryGetValue(index, out var currentRow))
                    {
                        SetRowVisual(currentRow, index, animate: true);
                        AutomationProperties.SetItemStatus(currentRow, "当前歌词");
                    }
                    if (!_manualScroll && !_isAnimatingTranslation)
                        FollowActiveLine(animate: true, fast: _progress.IsDragging);
                }
            }
            _resumeScrollPending = false;
        }
        else if (!_isShareSelecting && _resumeScrollPending && !_manualScroll && !_isAnimatingTranslation)
        {
            FollowActiveLine(animate: false);
        }

        if (!_isShareSelecting
            && _activeIndex >= 0
            && _activeIndex < lyrics.Lines.Count
            && _rows.TryGetValue(_activeIndex, out var activeRow))
        {
            var elapsed = adjusted - lyrics.Lines[_activeIndex].Position;
            activeRow.UpdateKaraokeProgress(elapsed, KaraokeUnlitColor(), Colors.White, _settings.CharacterKaraoke);
        }
    }

    private void UpdateTransport(TransportState state)
    {
        _previous.IsEnabled = state.Controls.CanPrevious;
        _play.IsEnabled = state.Controls.CanPlayPause;
        _next.IsEnabled = state.Controls.CanNext;
        _shuffle.IsEnabled = state.CanShuffle;
        _repeat.IsEnabled = state.CanRepeat;
        var repeatOne = state.Repeat == MediaPlaybackAutoRepeatMode.Track;
        var repeatList = state.Repeat == MediaPlaybackAutoRepeatMode.List;
        ((ClassicTransportGlyph)_play.Content).SetState(state.IsPlaying ? ClassicTransportIcon.Pause : ClassicTransportIcon.Play);
        ((ClassicTransportGlyph)_shuffle.Content).SetState(ClassicTransportIcon.Shuffle, state.Shuffle == true);
        ((ClassicTransportGlyph)_repeat.Content).SetState(repeatOne ? ClassicTransportIcon.RepeatOne : ClassicTransportIcon.Repeat, repeatOne || repeatList);
        SetButtonLabel(_play, state.IsPlaying ? "暂停" : "播放");
        SetButtonLabel(_shuffle, !state.CanShuffle
            ? "当前播放器未开放系统随机控制"
            : state.Shuffle switch
            {
                true => "关闭随机播放",
                false => "开启随机播放",
                _ => "开启随机播放",
            });
        SetButtonLabel(_repeat, !state.CanRepeat
            ? "当前播放器未开放系统循环控制"
            : state.Repeat switch
            {
                MediaPlaybackAutoRepeatMode.Track => "单曲循环（点击切换为顺序播放）",
                MediaPlaybackAutoRepeatMode.List => "列表循环（点击切换为单曲循环）",
                MediaPlaybackAutoRepeatMode.None => "顺序播放（点击切换为列表循环）",
                _ => "顺序播放（点击切换为列表循环）",
            });
    }

    private static void SetButtonLabel(Button button, string text)
    {
        if (Equals(button.ToolTip, text)) return;
        button.ToolTip = text;
        AutomationProperties.SetName(button, text);
    }

    public void ReloadLyricsDisplay()
    {
        Topmost = _settings.ClassicTopmost;
        ApplyActivePalette();
        UpdateTranslateButton();
        SyncRowTranslations(animateIfNew: false);
        AnimateTranslation(_settings.ClassicShowTranslation ? 1d : 0d);
        // 行距档位这类排版参数是在建行时应用的，所以必须重建一次行，否则要等下一首歌才生效
        RenderLyrics(preserveScroll: true);
        Refresh();
        ViewStateChanged?.Invoke();
    }

    public void ApplyLyricsFont(FontFamily font)
    {
        if (Equals(TextElement.GetFontFamily(_lines), font)) return;
        TextElement.SetFontFamily(_lines, font);
        if (_activeIndex >= 0 && !_manualScroll && !_isAnimatingTranslation)
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FollowActiveLine(animate: false));
    }

    private void ToggleTranslation()
    {
        _settings.ClassicShowTranslation = !_settings.ClassicShowTranslation;
        _settings.Save();
        UpdateTranslateButton();
        _onToggleTranslation();
        SyncRowTranslations(animateIfNew: false);
        if (_settings.ClassicShowTranslation)
        {
            if (_rows.Values.Any(r => r.HasTranslation))
            {
                _waitingForTranslationAnimation = false;
                AnimateTranslation(1d);
            }
            else
            {
                _waitingForTranslationAnimation = true;
                CancelTranslationAnimation();
                SetCurrentValue(TranslationProgressProperty, 0d);
            }
        }
        else
        {
            _waitingForTranslationAnimation = false;
            AnimateTranslation(0d);
        }
        Refresh();
        ViewStateChanged?.Invoke();
    }

    private void UpdateTranslateButton()
    {
        var on = _settings.ClassicShowTranslation;
        ((ClassicTransportGlyph)_translate.Content).SetState(ClassicTransportIcon.Translate, active: on);
        SetButtonLabel(_translate, on ? "隐藏双语翻译" : "显示双语翻译");
    }

    private double CurrentTranslationProgress => (double)GetValue(TranslationProgressProperty);

    private string? ExtractTranslation(LyricsLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Content)) return null;
        var preferredLang = _coordinator.TargetLanguage?.ToLowerInvariant();
        var allowUntagged = !_settings.ShowOnlyTargetTranslation
            || preferredLang is null
            || preferredLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var candidate = allowUntagged
            ? line.Attachments.Translation(preferredLang, null)
            : line.Attachments.Translation(preferredLang);
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var trimmed = candidate.Trim();
        if (_settings.HideSameTranslation)
        {
            var isTargetSimplifiedZh = preferredLang is null
                || preferredLang.Equals("zh", StringComparison.OrdinalIgnoreCase)
                || preferredLang.StartsWith("zh-hans", StringComparison.OrdinalIgnoreCase)
                || preferredLang.Equals("zh-cn", StringComparison.OrdinalIgnoreCase);
            if (_chineseLyrics && isTargetSimplifiedZh)
                return null;
            if (string.Equals(NormalizeForCompare(trimmed), NormalizeForCompare(line.Content), StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return trimmed;
    }

    private static string NormalizeForCompare(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private bool HasAnyTranslation(Lyrics lyrics)
    {
        foreach (var line in lyrics.Lines)
        {
            if (line.Enabled && ExtractTranslation(line) is not null)
                return true;
        }
        return false;
    }

    private void SyncRowTranslations(bool animateIfNew)
    {
        if (_lyrics is not { } lyrics || _rows.Count == 0) return;
        var hadAny = _rows.Values.Any(r => r.HasTranslation);
        var changed = false;
        var startHidden = animateIfNew && _settings.ClassicShowTranslation && !hadAny;
        if (startHidden)
        {
            CancelTranslationAnimation();
            SetCurrentValue(TranslationProgressProperty, 0d);
        }

        var progress = CurrentTranslationProgress;
        var size = CurrentMainFontSize();
        foreach (var (index, row) in _rows)
        {
            if (index < 0 || index >= lyrics.Lines.Count) continue;
            var subText = ExtractTranslation(lyrics.Lines[index]);
            if (string.Equals(row.TranslationText, subText, StringComparison.Ordinal)) continue;
            row.SetTranslation(subText, progress);
            row.ApplySizing(size);
            changed = true;
        }

        if (changed && startHidden && _rows.Values.Any(r => r.HasTranslation))
        {
            _waitingForTranslationAnimation = false;
            AnimateTranslation(1d);
        }

        // 译文插进来会改变行高，原来的滚动偏移就不再对准当前行了 ——
        // 标记一次"待重新对齐"，下一帧 Refresh 就会把当前行重新摆回视口中央。
        if (changed) _resumeScrollPending = true;
    }

    private void AnimateTranslation(double target)
    {
        var current = CurrentTranslationProgress;
        if (!SystemParameters.ClientAreaAnimation || _rows.Count == 0 || Math.Abs(current - target) < 0.001)
        {
            CancelTranslationAnimation();
            _scroll.UpdateLayout();
            var immediateAnchor = SelectAnchorRow();
            var immediateAnchorY = immediateAnchor?.Main.TranslatePoint(new Point(0, 0), _scroll).Y ?? 0;
            SetCurrentValue(TranslationProgressProperty, target);
            if (immediateAnchor is not null && _scroll.ViewportHeight > 0)
            {
                _scroll.UpdateLayout();
                var delta = immediateAnchor.Main.TranslatePoint(new Point(0, 0), _scroll).Y - immediateAnchorY;
                if (Math.Abs(delta) > 0.05)
                    _scroll.ScrollToVerticalOffset(Math.Clamp(_scroll.VerticalOffset + delta, 0, _scroll.ScrollableHeight));
            }
            return;
        }

        CancelScrollAnimation();
        _scroll.UpdateLayout();
        _translationAnchorRow = SelectAnchorRow();
        if (_translationAnchorRow is not null)
            _translationAnchorViewportY = _translationAnchorRow.Main.TranslatePoint(new Point(0, 0), _scroll).Y;

        _isAnimatingTranslation = true;
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = TranslationEase,
        };
        animation.Completed += (_, _) =>
        {
            _isAnimatingTranslation = false;
            _translationAnchorRow = null;
            BeginAnimation(TranslationProgressProperty, null);
            SetCurrentValue(TranslationProgressProperty, target);
            if (_resumeScrollPending && !_manualScroll) FollowActiveLine(animate: false);
        };
        BeginAnimation(TranslationProgressProperty, animation);
    }

    private void CancelTranslationAnimation()
    {
        var current = CurrentTranslationProgress;
        _isAnimatingTranslation = false;
        _translationAnchorRow = null;
        BeginAnimation(TranslationProgressProperty, null);
        SetCurrentValue(TranslationProgressProperty, current);
    }

    private void OnTranslationProgressChanged(double progress)
    {
        foreach (var row in _rows.Values)
            row.ApplyProgress(progress);

        if (_translationAnchorRow is { IsLoaded: true } anchor && _scroll.ViewportHeight > 0)
        {
            _scroll.UpdateLayout();
            var currentY = anchor.Main.TranslatePoint(new Point(0, 0), _scroll).Y;
            var delta = currentY - _translationAnchorViewportY;
            if (Math.Abs(delta) > 0.05)
            {
                var targetOffset = Math.Clamp(_scroll.VerticalOffset + delta, 0, _scroll.ScrollableHeight);
                _scroll.ScrollToVerticalOffset(targetOffset);
            }
        }
    }

    private LyricRow? SelectAnchorRow()
    {
        if (_rows.Count == 0 || _scroll.ViewportHeight <= 0) return null;
        if (!_manualScroll && _rows.TryGetValue(_activeIndex, out var activeRow))
        {
            var activeY = activeRow.Main.TranslatePoint(new Point(0, 0), _scroll).Y;
            if (activeY + activeRow.Main.ActualHeight >= 0 && activeY <= _scroll.ViewportHeight)
                return activeRow;
        }

        var viewportCenter = _scroll.ViewportHeight / 2;
        LyricRow? bestRow = null;
        var bestDistance = double.MaxValue;
        foreach (var row in _rows.Values)
        {
            var y = row.Main.TranslatePoint(new Point(0, 0), _scroll).Y + row.Main.ActualHeight / 2;
            var distance = Math.Abs(y - viewportCenter);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestRow = row;
            }
        }
        return bestRow;
    }

    private void RenderLyrics(bool preserveScroll = false)
    {
        if (_isShareSelecting && !ReferenceEquals(_lyrics, _shareSelectionLyrics))
            ExitShareSelectionMode(resumeAutoScroll: false);

        CancelScrollAnimation();
        var savedActive = preserveScroll ? _activeIndex : -1;
        var savedManual = preserveScroll && _manualScroll;
        _rows.Clear();
        _lines.Children.Clear();
        if (!preserveScroll) _scroll.ScrollToTop();
        _activeIndex = savedActive;
        _manualScroll = savedManual;
        if (!preserveScroll)
        {
            _resume.Visibility = Visibility.Collapsed;
            _resumeScrollPending = false;
        }

        var progress = CurrentTranslationProgress;
        var (lineScale, gapScale) = _settings.LineSpacingScales;
        _lineHeightScale = lineScale;
        _rowGapScale = gapScale;
        if (_lyrics is { } lyrics)
        {
            _chineseLyrics = IsMostlyChinese(lyrics);
            for (var i = 0; i < lyrics.Lines.Count; i++)
            {
                var line = lyrics.Lines[i];
                if (!line.Enabled) continue;
                var mainText = string.IsNullOrWhiteSpace(line.Content) ? "♪" : line.Content;
                var timeTags = string.IsNullOrWhiteSpace(line.Content) ? null : line.Attachments.GetInlineTimeTags();
                var subText = ExtractTranslation(line);
                var row = NewLine(mainText, subText, progress, timeTags);
                row.SetShareMode(_isShareSelecting);
                var capturedIndex = i;
                var linePos = line.Position;
                row.Cursor = Cursors.Hand;
                row.ContextMenu = CreateRowContextMenu(capturedIndex);
                row.MouseEnter += (_, _) => OnLyricRowMouseEnter(capturedIndex);
                row.MouseLeave += (_, _) => OnLyricRowMouseLeave(capturedIndex);
                row.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount >= 2)
                    {
                        _pressedRowIndex = null;
                        _mouseDownStartedOutsideRow = true;
                        return;
                    }
                    _pressedRowIndex = capturedIndex;
                    _mouseDownStartedOutsideRow = false;
                };
                row.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var suppress = e.ClickCount >= 2
                        || _mouseDownStartedOutsideRow
                        || Environment.TickCount64 < _suppressRowClickUntilTick
                        || (_pressedRowIndex is { } downIndex && downIndex != capturedIndex);
                    _pressedRowIndex = null;
                    _mouseDownStartedOutsideRow = false;
                    if (suppress) return;

                    if (_isShareSelecting)
                    {
                        ToggleShareRowSelection(capturedIndex);
                        return;
                    }
                    var seekSeconds = linePos - lyrics.TimeDelay - _coordinator.ManualOffsetSeconds - _coordinator.TrackOffsetSeconds;
                    if (seekSeconds < 0) seekSeconds = 0;
                    _ = SeekToSecondsAsync(seekSeconds, targetLineIndex: capturedIndex);
                };
                _rows.Add(i, row);
                _lines.Children.Add(row);
            }
        }

        if (_rows.Count == 0)
        {
            if (_isShareSelecting)
                ExitShareSelectionMode(resumeAutoScroll: false);
            var message = _track is null ? "在你的播放器中播放一首歌" : _status switch
            {
                LyricsStatusKind.NotFound or LyricsStatusKind.Wrong => "这首歌暂无同步歌词",
                LyricsStatusKind.HiddenByUser => "这首歌的歌词已隐藏",
                _ => "正在查找歌词…",
            };
            var emptyRow = NewLine(message);
            emptyRow.Opacity = 1.0;
            _lines.Children.Add(emptyRow);
        }
        else if (_isShareSelecting)
        {
            foreach (var (rowIndex, row) in _rows)
                SetRowVisual(row, rowIndex, animate: false);
        }
        else if (preserveScroll && _activeIndex >= 0)
        {
            foreach (var (rowIndex, row) in _rows)
                SetRowVisual(row, rowIndex, animate: false);
            if (!_manualScroll && !_isAnimatingTranslation)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FollowActiveLine(animate: false));
        }
        ResizeLyrics();
        UpdateShareButton();
    }

    private LyricRow NewLine(string text, string? translation = null, double progress = 0d, InlineTimeTags? timeTags = null)
    {
        var row = new LyricRow(text, translation, _subdued, progress, timeTags)
        {
            LineHeightScale = _lineHeightScale,
            GapScale = _rowGapScale,
        };
        return row;
    }

    private static bool IsMostlyChinese(Lyrics lyrics)
    {
        var cjk = 0;
        var latin = 0;
        var kanaOrHangul = 0;
        foreach (var line in lyrics.Lines)
        {
            if (!line.Enabled) continue;
            foreach (var c in line.Content)
            {
                if ((c >= '\u3040' && c <= '\u30FF') || (c >= '\uAC00' && c <= '\uD7AF'))
                    kanaOrHangul++;
                else if (c >= '\u4E00' && c <= '\u9FFF')
                    cjk++;
                else if (char.IsLetter(c))
                    latin++;
            }
        }
        return cjk > 0 && cjk >= latin && kanaOrHangul * 20 < cjk;
    }

    private double CurrentMainFontSize()
    {
        var width = _scroll.ActualWidth;
        return width > 1600 ? 64 : width > 800 ? 48 : ActualWidth >= 768 ? 32 : 24;
    }

    private void ResizeLyrics()
    {
        var width = _scroll.ActualWidth;
        if (width <= 0) return;
        var side = width < 600 ? 24 : width < 900 ? 40 : Math.Max(64, (width - 1024) / 2);
        var top = width < 600 ? 36 : 64;
        if (_isShareSelecting)
        {
            side = Math.Max(16, side - 12);
            top = Math.Max(top, 78);
        }
        var bottom = _isShareSelecting
            ? Math.Max(104, _scroll.ActualHeight / 2)
            : _scroll.ActualHeight / 2;
        _lines.Margin = new Thickness(side, top, side, bottom);
        var size = CurrentMainFontSize();
        foreach (LyricRow line in _lines.Children)
            line.ApplySizing(size);
        if (_activeIndex >= 0 && !_isAnimatingTranslation && !_isShareSelecting) _resumeScrollPending = true;
    }

    private async Task SeekToSecondsAsync(double seconds, int? targetLineIndex = null)
    {
        var generation = ++_seekGeneration;
        var requestedTrack = _track;
        var maxSeconds = _progress.Maximum > 0 ? _progress.Maximum : seconds;
        var clampedSeconds = Math.Clamp(seconds, 0, Math.Max(0, maxSeconds));
        _seekInFlight = true;
        _pendingSeekSeconds = clampedSeconds;
        _pendingSeekLineIndex = targetLineIndex;
        _pendingSeekDeadlineTick = Environment.TickCount64 + 800;

        var previousActive = _activeIndex;
        _manualScroll = false;
        _resume.Visibility = Visibility.Collapsed;
        _resumeScrollPending = false;
        if (!_progress.IsUserSeeking && Math.Abs(_progress.Value - clampedSeconds) > 0.02)
            _progress.AnimateToValue(clampedSeconds);
        Refresh();
        if (generation == _seekGeneration && _activeIndex == previousActive && !_isShareSelecting && !_manualScroll && !_isAnimatingTranslation)
            FollowActiveLine(animate: true, fast: _progress.IsDragging);

        var succeeded = false;
        try
        {
            succeeded = await _player.SeekAsync(TimeSpan.FromSeconds(clampedSeconds));
        }
        finally
        {
            if (generation == _seekGeneration)
            {
                _seekInFlight = false;
                if (succeeded && SameTrackIdentity(_track, requestedTrack))
                    _pendingSeekDeadlineTick = Math.Min(_pendingSeekDeadlineTick, Environment.TickCount64 + 300);
                else
                {
                    _pendingSeekSeconds = null;
                    _pendingSeekLineIndex = null;
                }
            }
            Refresh();
        }
    }

    private void PauseAutoScroll()
    {
        if (_rows.Count == 0 || _manualScroll) return;
        CancelScrollAnimation();
        _manualScroll = true;
        _manualScrollAt = Environment.TickCount64;
        if (!_isShareSelecting)
            _resume.Visibility = Visibility.Visible;
        _resumeScrollPending = true;
    }

    /// <summary>
    /// 自动同步：手动滚动后 <see cref="AppSettings.AutoSyncSeconds"/> 秒内没有新的滚动操作，
    /// 就自动回到"跟随当前歌词行"，不必再去点「同步」按钮。
    /// 开着翻译时每行会变高，用户常常要滚动去看译文，这个兜底就是为那种场景准备的。
    /// </summary>
    private void AutoSyncIfIdle()
    {
        var seconds = _settings.AutoSyncSeconds;
        if (seconds <= 0 || !_manualScroll || _isShareSelecting || _rows.Count == 0) return;
        if (Environment.TickCount64 - _manualScrollAt < seconds * 1000L) return;

        _coordinator.RefreshCurrentLine();
        ResumeAutoScroll();
        _resumeScrollPending = true;
    }

    private void ResumeAutoScroll()
    {
        _manualScroll = false;
        _resume.Visibility = Visibility.Collapsed;
        _resumeScrollPending = true;
        if (_resume.IsKeyboardFocused) _scroll.Focus();
    }

    private void FollowActiveLine(bool animate = true, bool fast = false)
    {
        _resumeScrollPending = false;
        if (!_rows.TryGetValue(_activeIndex, out var active))
        {
            CancelScrollAnimation();
            _scroll.ScrollToTop();
            return;
        }

        CancelScrollAnimation();
        var start = _scroll.VerticalOffset;
        var y = active.Main.TranslatePoint(new Point(0, 0), _scroll).Y;
        var offset = Math.Clamp(start + y + (active.Main.ActualHeight - _scroll.ViewportHeight) / 2, 0, _scroll.ScrollableHeight);
        var distance = Math.Abs(offset - start);
        if (distance <= 1) return;

        if (animate && SystemParameters.ClientAreaAnimation)
        {
            if (fast)
            {
                var fastSeconds = Math.Clamp(0.18 + Math.Min(distance / 4000.0, 0.08), 0.18, 0.26);
                var animation = new DoubleAnimation(start, offset, TimeSpan.FromSeconds(fastSeconds))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                };
                BeginAnimation(FollowOffsetProperty, animation);
            }
            else
            {
                var durationSeconds = Math.Clamp(0.42 + Math.Min(distance / 1800.0, 0.18), 0.42, 0.60);
                var duration = TimeSpan.FromSeconds(durationSeconds);
                var animation = new DoubleAnimationUsingKeyFrames();
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                animation.KeyFrames.Add(new SplineDoubleKeyFrame(offset, KeyTime.FromTimeSpan(duration), ScrollSpline));
                BeginAnimation(FollowOffsetProperty, animation);
            }
        }
        else
        {
            _scroll.ScrollToVerticalOffset(offset);
        }
    }

    private void CancelScrollAnimation()
    {
        var offset = _scroll.VerticalOffset;
        BeginAnimation(FollowOffsetProperty, null);
        _scroll.ScrollToVerticalOffset(offset);
    }

    private static bool SameTrackIdentity(TrackInfo? a, TrackInfo? b) =>
        Services.NowPlayingService.SameTrackIdentity(a, b);

    private void CancelCoverLoad()
    {
        _coverGeneration++;
        _coverWaitingOnlineFallback = false;
        if (_coverCts is not null)
        {
            _coverCts.Cancel();
            _coverCts.Dispose();
            _coverCts = null;
        }
    }

    private void ResetCoverPalette()
    {
        _coverBackground = DefaultBackground;
        _coverSubdued = DefaultSubdued;
        ApplyActivePalette();
    }

    private void StartCoverLoad(TrackInfo track, Uri? artworkUrl)
    {
        CancelCoverLoad();
        var generation = _coverGeneration;
        var cts = new CancellationTokenSource();
        _coverCts = cts;
        _coverInFlight = true;
        _coverThumbnailVersion = _player.ThumbnailVersion;
        if (artworkUrl is not null) _coverTriedArtworkUrl = true;

        var task = LoadCoverAsync(track, artworkUrl, generation, cts.Token);
        if (!task.IsCompleted && _cover.Source is null)
            ResetCoverPalette();
    }

    private async Task LoadCoverAsync(TrackInfo track, Uri? artworkUrl, long generation, CancellationToken ct)
    {
        try
        {
            var preferOnlineCover = Services.NowPlayingService.IsBrowserOrVideoSource(track.SourceAppId);
            byte[]? bytes = null;
            var fromSmtc = false;
            var smtcThumbVersion = _player.ThumbnailVersion;

            if (preferOnlineCover)
            {
                // 浏览器与视频站点优先按清洗后的歌名从 QQ 音乐等在线库拉取官方专辑封面
                bytes = await Services.CoverFetcher.FetchCoverAsync(track.Title, track.Artist, artworkUrl, ct);
                if (ct.IsCancellationRequested || generation != _coverGeneration || !SameTrackIdentity(_track, track)) return;
            }

            if (bytes is null)
            {
                var snapshot = await _player.GetThumbnailForTrackAsync(track, ct);
                if (ct.IsCancellationRequested || generation != _coverGeneration || !SameTrackIdentity(_track, track)) return;
                smtcThumbVersion = snapshot.Version;
                bytes = snapshot.Bytes;
                if (bytes is not null) fromSmtc = true;
            }

            // 若本地 SMTC 未提供封面（例如 B 站桌面客户端，或 SMTC 异步加载中/命中上首残留图），自动回退向在线音乐库检索高清专辑封面
            if (bytes is null && !preferOnlineCover)
            {
                _coverWaitingOnlineFallback = true;
                try
                {
                    bytes = await Services.CoverFetcher.FetchCoverAsync(track.Title, track.Artist, artworkUrl, ct);
                }
                finally
                {
                    if (generation == _coverGeneration)
                        _coverWaitingOnlineFallback = false;
                }
                if (ct.IsCancellationRequested || generation != _coverGeneration || !SameTrackIdentity(_track, track)) return;
            }

            _coverThumbnailVersion = smtcThumbVersion;
            if (bytes is null)
            {
                _coverBytes = null;
                _cover.Source = null;
                _coverFromSmtc = false;
                ResetCoverPalette();
                return;
            }

            _coverBytes = bytes;
            using var stream = new MemoryStream(bytes);
            var cover = new BitmapImage();
            cover.BeginInit();
            cover.StreamSource = stream;
            cover.DecodePixelWidth = 160;
            cover.CacheOption = BitmapCacheOption.OnLoad;
            cover.EndInit();
            cover.Freeze();
            _cover.Source = cover;
            _coverFromSmtc = fromSmtc;

            (_coverBackground, _coverSubdued) = ClassicLyricsPalette.FromCover(cover);
            ApplyActivePalette();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 切歌取消
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or InvalidOperationException or IOException)
        {
            _coverBytes = null;
            _cover.Source = null;
            _coverFromSmtc = false;
            ResetCoverPalette();
        }
        finally
        {
            if (generation == _coverGeneration)
            {
                _coverInFlight = false;
                _coverWaitingOnlineFallback = false;
            }
        }
    }

    private void ApplyActivePalette()
    {
        var (bg, subdued) = ClassicLyricsPalette.Resolve(_settings.ClassicBackgroundMode, (_coverBackground, _coverSubdued));
        ApplyPalette(bg, subdued);
    }

    private void ApplyPalette(Color background, Color subdued)
    {
        _background = background;
        _subdued = subdued;
        var brush = (SolidColorBrush)_panel.Background;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        var current = brush.Color;
        brush.Color = background;
        var animatePalette = SystemParameters.ClientAreaAnimation && current != background;
        if (animatePalette)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(current, background, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        UpdateEdgeFades(current, background, animatePalette);
        foreach (var (index, row) in _rows) SetRowVisual(row, index, animate: false);
        if (_isShareSelecting) UpdateShareSelectionChrome();
        if (_rows.Count == 0 && _lines.Children.Count > 0 && _lines.Children[0] is LyricRow emptyRow)
        {
            emptyRow.Opacity = 1.0;
            SetRowVisual(emptyRow, isActive: false, isPast: false, subdued, animate: false);
        }
    }

    private void UpdateEdgeFades(Color from, Color to, bool animate)
    {
        AnimateFadeStop(_topFadeOpaqueStop, Color.FromArgb(240, from.R, from.G, from.B), Color.FromArgb(240, to.R, to.G, to.B), animate);
        AnimateFadeStop(_topFadeClearStop, Color.FromArgb(0, from.R, from.G, from.B), Color.FromArgb(0, to.R, to.G, to.B), animate);
        AnimateFadeStop(_bottomFadeClearStop, Color.FromArgb(0, from.R, from.G, from.B), Color.FromArgb(0, to.R, to.G, to.B), animate);
        AnimateFadeStop(_bottomFadeOpaqueStop, Color.FromArgb(240, from.R, from.G, from.B), Color.FromArgb(240, to.R, to.G, to.B), animate);
    }

    private static void AnimateFadeStop(GradientStop stop, Color from, Color to, bool animate)
    {
        stop.BeginAnimation(GradientStop.ColorProperty, null);
        stop.Color = to;
        if (animate)
        {
            stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(from, to, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        }
    }

    private Color KaraokeUnlitColor() => BlendColor(_background, _subdued, 0.70);

    private static Color BlendColor(Color from, Color to, double amount) =>
        ClassicLyricsPalette.BlendColor(from, to, amount);

    private Color RowColor(int index)
    {
        if (index == _activeIndex) return Colors.White;
        if (index > _activeIndex) return _subdued;
        return Color.FromRgb((byte)((_subdued.R + _background.R) / 2),
            (byte)((_subdued.G + _background.G) / 2), (byte)((_subdued.B + _background.B) / 2));
    }

    private void SetRowVisual(LyricRow? row, int index, bool animate, bool isHovered = false)
    {
        if (row is null) return;
        if (_isShareSelecting)
        {
            var isSelected = _selectedShareLines.Contains(index);
            var shareHover = _hoveredShareLineIndex == index;
            row.UpdateShareVisualState(isSelected, shareHover, _background, _subdued, animate);
            return;
        }
        var isActive = index == _activeIndex;
        var isPast = index < _activeIndex;
        var color = isHovered ? Colors.White : RowColor(index);
        row.UpdateVisualState(isActive, isPast, color, animate, isHovered);
        if (isActive && !isHovered)
            row.RefreshKaraokeColors(KaraokeUnlitColor(), Colors.White, _settings.CharacterKaraoke);
    }

    private static void SetRowVisual(LyricRow row, bool isActive, bool isPast, Color color, bool animate, bool isHovered = false)
    {
        row.UpdateVisualState(isActive, isPast, color, animate, isHovered);
    }

    private const double OffsetPillCornerRadius = 15;
    private const double OffsetScopeChipCornerRadius = 12;

    private static readonly Brush OffsetPopupBg = CreateOffsetBrush(32, 32, 32);
    private static readonly Brush OffsetPopupBorder = CreateOffsetBrush(64, 64, 64);
    private static readonly Brush OffsetBtnBg = CreateOffsetBrush(42, 42, 42);
    private static readonly Brush OffsetBtnHoverBg = CreateOffsetBrush(56, 56, 56);
    private static readonly Brush OffsetBtnBorder = CreateOffsetBrush(58, 58, 58);
    private static readonly Brush OffsetSegBg = CreateOffsetBrush(22, 22, 22);
    private static readonly Brush OffsetSegActiveBg = CreateOffsetBrush(44, 62, 50);
    private static readonly Brush OffsetSegActiveBorder = CreateOffsetAlphaBrush(80, 30, 215, 96);
    private static readonly Brush OffsetSegHoverBg = CreateOffsetAlphaBrush(22, 255, 255, 255);
    private static readonly System.Windows.Media.Animation.IEasingFunction OffsetScopeEase = CreateScopeEase();

    private readonly Action<double?> _adjustOffset = null!;
    private readonly Action<double?> _adjustTrackOffsetAction = null!;
    private readonly Button _offsetBtn = null!;
    private System.Windows.Shapes.Path _offsetClockPath = null!;
    private TextBlock _offsetBadge = null!;
    private readonly System.Windows.Controls.Primitives.Popup _offsetPopup = null!;
    private TextBlock _offsetValueText = null!;
    private TextBlock _offsetDirText = null!;
    private Grid _offsetScopeTrack = null!;
    private Border _offsetScopeIndicator = null!;
    private TranslateTransform _offsetScopeTransform = null!;
    private Button _offsetScopeTrackBtn = null!;
    private Button _offsetScopeGlobalBtn = null!;
    private Button _offsetResetBtn = null!;
    private bool _adjustScopePerTrack = true;
    private bool _offsetScopeWasPerTrack = true;
    private double _shownTrackOffset = double.NaN;
    private double _shownManualOffset = double.NaN;
    private string? _shownTrackKey;

    private static SolidColorBrush CreateOffsetBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateOffsetAlphaBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Animation.IEasingFunction CreateScopeEase()
    {
        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
        };
        ease.Freeze();
        return ease;
    }

    private static string? TrackOffsetKey(TrackInfo? track) =>
        track is null ? null : Musebase.Core.Search.LyricsCacheStore.MakeKey(track.Title, track.Artist);

    private Button CreateOffsetTriggerButton()
    {
        _offsetClockPath = new System.Windows.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.None,
            Data = Geometry.Parse("M 7,1.75 A 5.25,5.25 0 1 1 6.99,1.75 Z M 7,4.2 L 7,7.2 L 9.4,8.6"),
            Stroke = Muted,
            StrokeThickness = 1.45,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _offsetBadge = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ClassicPlaybackSlider.Accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, -0.5, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(_offsetClockPath);
        content.Children.Add(_offsetBadge);

        var button = new Button
        {
            Content = content,
            MinWidth = 32,
            Height = 36,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(4, 0, 6, 0),
            Foreground = Muted,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Template = TransportTemplate(cornerRadius: 4),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
        };
        SetButtonLabel(button, "音频同步调整");
        button.MouseEnter += (_, _) => UpdateOffsetTriggerAppearance();
        button.MouseLeave += (_, _) => UpdateOffsetTriggerAppearance();
        button.Click += (_, _) => _offsetPopup.IsOpen = !_offsetPopup.IsOpen;
        return button;
    }

    private System.Windows.Controls.Primitives.Popup CreateOffsetPopup()
    {
        _offsetScopeTransform = new TranslateTransform();
        _offsetScopeIndicator = new Border
        {
            CornerRadius = new CornerRadius(OffsetScopeChipCornerRadius),
            Background = OffsetSegActiveBg,
            BorderBrush = OffsetSegActiveBorder,
            BorderThickness = new Thickness(1),
            RenderTransform = _offsetScopeTransform,
            IsHitTestVisible = false,
        };
        Grid.SetColumn(_offsetScopeIndicator, 0);

        _offsetScopeTrackBtn = CreateScopeChip("仅此曲", perTrack: true);
        _offsetScopeGlobalBtn = CreateScopeChip("全部", perTrack: false);
        Grid.SetColumn(_offsetScopeTrackBtn, 0);
        Grid.SetColumn(_offsetScopeGlobalBtn, 1);

        _offsetScopeTrack = new Grid
        {
            MinWidth = 108,
            Height = 24,
        };
        _offsetScopeTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _offsetScopeTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _offsetScopeTrack.Children.Add(_offsetScopeIndicator);
        _offsetScopeTrack.Children.Add(_offsetScopeTrackBtn);
        _offsetScopeTrack.Children.Add(_offsetScopeGlobalBtn);
        _offsetScopeTrack.SizeChanged += (_, _) => UpdateScopeIndicatorPosition(animate: false);

        var scopeHost = new Border
        {
            Height = 30,
            Background = OffsetSegBg,
            BorderBrush = OffsetBtnBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(OffsetPillCornerRadius),
            Padding = new Thickness(2),
            SnapsToDevicePixels = true,
            Child = _offsetScopeTrack,
        };

        var header = new DockPanel { LastChildFill = false };
        var title = new TextBlock
        {
            Text = "音频同步调整",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        DockPanel.SetDock(scopeHost, Dock.Right);
        header.Children.Add(scopeHost);
        header.Children.Add(title);

        _offsetValueText = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _offsetDirText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = Muted,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var readoutStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        readoutStack.Children.Add(_offsetValueText);
        readoutStack.Children.Add(_offsetDirText);

        _offsetResetBtn = new Button
        {
            Content = "归零",
            Height = 30,
            MinWidth = 56,
            Padding = new Thickness(13, 0, 13, 0),
            Background = OffsetBtnBg,
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "重置当前模式时间偏移 (Alt+0)",
            Template = OffsetPopupButtonTemplate(cornerRadius: OffsetPillCornerRadius, hoverBg: OffsetBtnHoverBg),
        };
        System.Windows.Automation.AutomationProperties.SetName(_offsetResetBtn, "重置音频同步偏移");
        _offsetResetBtn.Click += (_, _) => AdjustSelectedOffset(null);

        var statusRow = new DockPanel { Margin = new Thickness(0, 11, 0, 11) };
        DockPanel.SetDock(_offsetResetBtn, Dock.Right);
        statusRow.Children.Add(_offsetResetBtn);
        statusRow.Children.Add(readoutStack);

        var stepRow = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = 4,
            Margin = new Thickness(-3, 0, -3, 0),
        };
        stepRow.Children.Add(CreatePopupStepButton("-0.5s", -0.5, "音频同步延后 0.5 秒"));
        stepRow.Children.Add(CreatePopupStepButton("-0.1s", -0.1, "音频同步延后 0.1 秒 (Alt+←)"));
        stepRow.Children.Add(CreatePopupStepButton("+0.1s", 0.1, "音频同步提前 0.1 秒 (Alt+→)"));
        stepRow.Children.Add(CreatePopupStepButton("+0.5s", 0.5, "音频同步提前 0.5 秒"));

        var body = new StackPanel { MinWidth = 248 };
        body.Children.Add(header);
        body.Children.Add(statusRow);
        body.Children.Add(stepRow);

        var card = new Border
        {
            Background = OffsetPopupBg,
            BorderBrush = OffsetPopupBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 12, 14, 13),
            Margin = new Thickness(8, 8, 8, 10),
            Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = 0.5,
            },
        };
        card.PreviewKeyDown += (_, e) => HandleOffsetKeyDown(e);
        System.Windows.Documents.TextElement.SetFontFamily(card, ClassicLyricsFonts.Body);

        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = _offsetBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
            CustomPopupPlacementCallback = PositionOffsetPopup,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
            Child = card,
        };
        popup.Opened += (_, _) =>
        {
            _offsetBtn.IsHitTestVisible = false;
            UpdateOffsetPopupState(animate: false);
        };
        popup.Closed += (_, _) =>
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _offsetBtn.IsHitTestVisible = true);
        UpdateOffsetPopupState(animate: false);
        return popup;
    }

    private static System.Windows.Controls.Primitives.CustomPopupPlacement[] PositionOffsetPopup(
        Size popupSize, Size targetSize, Point offset) =>
    [
        new System.Windows.Controls.Primitives.CustomPopupPlacement(
            new Point(targetSize.Width - popupSize.Width + 8, -popupSize.Height + 2),
            System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
        new System.Windows.Controls.Primitives.CustomPopupPlacement(
            new Point(targetSize.Width - popupSize.Width + 8, targetSize.Height - 2),
            System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
    ];

    private Button CreateScopeChip(string label, bool perTrack)
    {
        var fgColor = (Muted as SolidColorBrush)?.Color ?? Color.FromRgb(179, 179, 179);
        var btn = new Button
        {
            Content = label,
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(fgColor),
            FontSize = 11.5,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = OffsetPopupButtonTemplate(cornerRadius: OffsetScopeChipCornerRadius, hoverBg: OffsetSegHoverBg, showBorder: false),
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, perTrack ? "仅此曲音频同步" : "全部歌曲音频同步");
        btn.Click += (_, _) =>
        {
            _adjustScopePerTrack = perTrack;
            if (_settings.ClassicOffsetPerTrack != perTrack)
            {
                _settings.ClassicOffsetPerTrack = perTrack;
                _settings.Save();
            }
            UpdateOffsetPopupState(animate: true);
        };
        return btn;
    }

    private Button CreatePopupStepButton(string label, double delta, string description)
    {
        var btn = new Button
        {
            Content = label,
            Height = 30,
            Margin = new Thickness(3, 0, 3, 0),
            Padding = new Thickness(6, 0, 6, 0),
            Background = OffsetBtnBg,
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = description,
            Template = OffsetPopupButtonTemplate(cornerRadius: OffsetPillCornerRadius, hoverBg: OffsetBtnHoverBg),
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, description);
        btn.Click += (_, _) => AdjustSelectedOffset(delta);
        return btn;
    }

    private static ControlTemplate OffsetPopupButtonTemplate(double cornerRadius, Brush hoverBg, bool showBorder = true)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Border");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        if (showBorder)
        {
            border.SetValue(Border.BorderBrushProperty, OffsetBtnBorder);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        }

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Border"));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.42));
        template.Triggers.Add(disabled);
        return template;
    }

    private bool HandleOffsetKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && _offsetPopup.IsOpen)
        {
            _offsetPopup.IsOpen = false;
            _offsetBtn.Focus();
            e.Handled = true;
            return true;
        }
        if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Left or Key.Right or Key.D0 or Key.NumPad0)
        {
            double? delta = key switch
            {
                Key.Left => -0.1,
                Key.Right => 0.1,
                _ => null,
            };
            AdjustSelectedOffset(delta);
            e.Handled = true;
            return true;
        }
        return false;
    }

    private void AdjustSelectedOffset(double? delta)
    {
        var usePerTrack = _adjustScopePerTrack && (_track ?? _coordinator.CurrentTrack) is not null;
        if (usePerTrack) _adjustTrackOffsetAction(delta);
        else AdjustGlobalOffset(delta);
    }

    private void AdjustGlobalOffset(double? delta)
    {
        _adjustOffset(delta);
        var normalized = NormalizeOffset(_coordinator.ManualOffsetSeconds);
        if (Math.Abs(normalized - _coordinator.ManualOffsetSeconds) > 1e-6
            || Math.Abs(normalized - _settings.ManualOffsetSeconds) > 1e-6)
        {
            _coordinator.ManualOffsetSeconds = normalized;
            _settings.ManualOffsetSeconds = normalized;
            _settings.Save();
        }
        UpdateOffsetButton();
        Refresh();
    }

    private void UpdateOffsetButton()
    {
        var trackOff = NormalizeOffset(_coordinator.TrackOffsetSeconds);
        var manualOff = NormalizeOffset(_coordinator.ManualOffsetSeconds);
        var currentTrackKey = TrackOffsetKey(_track ?? _coordinator.CurrentTrack);

        if (trackOff == _shownTrackOffset
            && manualOff == _shownManualOffset
            && string.Equals(currentTrackKey, _shownTrackKey, StringComparison.Ordinal))
        {
            return;
        }

        _shownTrackOffset = trackOff;
        _shownManualOffset = manualOff;
        _shownTrackKey = currentTrackKey;

        UpdateOffsetTriggerAppearance();
        if (_offsetPopup.IsOpen) UpdateOffsetPopupState(animate: false);
    }

    private void UpdateOffsetTriggerAppearance()
    {
        var trackOff = NormalizeOffset(_coordinator.TrackOffsetSeconds);
        var manualOff = NormalizeOffset(_coordinator.ManualOffsetSeconds);
        var total = NormalizeOffset(trackOff + manualOff);
        var hasAny = total != 0 || trackOff != 0 || manualOff != 0;

        if (hasAny)
        {
            _offsetBtn.Padding = new Thickness(7, 0, 7, 0);
            _offsetClockPath.Stroke = ClassicPlaybackSlider.Accent;
            _offsetBadge.Text = FormatCompactOffset(total);
            _offsetBadge.Visibility = Visibility.Visible;
        }
        else
        {
            _offsetBtn.Padding = new Thickness(0);
            _offsetClockPath.Stroke = _offsetBtn.IsMouseOver ? Brushes.White : Muted;
            _offsetBadge.Visibility = Visibility.Collapsed;
        }

        var tooltip = hasAny
            ? $"音频同步调整 {FormatCompactOffset(total)}（此曲 {FormatCompactOffset(trackOff)} / 全局 {FormatCompactOffset(manualOff)} · Alt+←/→）"
            : "音频同步调整（当前 0.0s · Alt+←/→）";
        SetButtonLabel(_offsetBtn, tooltip);
    }

    private void UpdateOffsetPopupState(bool animate = false)
    {
        var track = _track ?? _coordinator.CurrentTrack;
        var hasTrack = track is not null;
        var usePerTrack = _adjustScopePerTrack && hasTrack;
        var scopeChanged = usePerTrack != _offsetScopeWasPerTrack;
        _offsetScopeWasPerTrack = usePerTrack;

        _offsetScopeTrackBtn.IsEnabled = hasTrack;
        _offsetScopeTrackBtn.FontWeight = usePerTrack ? FontWeights.SemiBold : FontWeights.Normal;
        _offsetScopeGlobalBtn.FontWeight = !usePerTrack ? FontWeights.SemiBold : FontWeights.Normal;

        var accentColor = (ClassicPlaybackSlider.Accent as SolidColorBrush)?.Color ?? Color.FromRgb(30, 215, 96);
        var mutedColor = (Muted as SolidColorBrush)?.Color ?? Color.FromRgb(179, 179, 179);
        var shouldAnimate = animate && scopeChanged && SystemParameters.ClientAreaAnimation;
        UpdateScopeChipForeground(_offsetScopeTrackBtn, usePerTrack ? accentColor : mutedColor, shouldAnimate);
        UpdateScopeChipForeground(_offsetScopeGlobalBtn, !usePerTrack ? accentColor : mutedColor, shouldAnimate);
        UpdateScopeIndicatorPosition(shouldAnimate, usePerTrack);

        var trackOff = NormalizeOffset(_coordinator.TrackOffsetSeconds);
        var manualOff = NormalizeOffset(_coordinator.ManualOffsetSeconds);
        var activeVal = usePerTrack ? trackOff : manualOff;

        _offsetValueText.Text = FormatCompactOffset(activeVal);
        _offsetValueText.Foreground = activeVal != 0 ? ClassicPlaybackSlider.Accent : Brushes.White;
        _offsetDirText.Text = activeVal switch
        {
            > 0 => "歌词提前",
            < 0 => "歌词延后",
            _ => "默认同步",
        };
        _offsetResetBtn.IsEnabled = activeVal != 0;
    }

    private void UpdateScopeIndicatorPosition(bool animate, bool? forceUsePerTrack = null)
    {
        var usePerTrack = forceUsePerTrack ?? (_adjustScopePerTrack && (_track ?? _coordinator.CurrentTrack) is not null);
        var trackWidth = _offsetScopeTrack.ActualWidth;
        if (trackWidth <= 0) return;

        var targetX = usePerTrack ? 0d : trackWidth / 2d;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            _offsetScopeTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _offsetScopeTransform.X = targetX;
            return;
        }

        var anim = new System.Windows.Media.Animation.DoubleAnimation(targetX, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = OffsetScopeEase,
        };
        _offsetScopeTransform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private static void UpdateScopeChipForeground(Button button, Color targetColor, bool animate)
    {
        if (button.Foreground is not SolidColorBrush brush || brush.IsFrozen)
        {
            brush = new SolidColorBrush(targetColor);
            button.Foreground = brush;
        }

        if (!animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = targetColor;
            return;
        }

        var anim = new System.Windows.Media.Animation.ColorAnimation(targetColor, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = OffsetScopeEase,
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private static double NormalizeOffset(double value)
    {
        var rounded = Math.Round(value, 2);
        return Math.Abs(rounded) < 0.005 ? 0 : rounded;
    }

    private static string FormatCompactOffset(double value)
    {
        var v = NormalizeOffset(value);
        if (v == 0) return "0.0s";
        var prefix = v > 0 ? "+" : "-";
        var abs = Math.Abs(v);
        var formatted = Math.Abs(abs * 10 - Math.Round(abs * 10)) < 0.005
            ? abs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : abs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"{prefix}{formatted}s";
    }

    private Button ChromeButton(
        ClassicTransportIcon glyph,
        string label,
        Action onClick,
        double height = 36,
        double width = 32,
        double? glyphSize = null,
        double cornerRadius = 4,
        Brush? foreground = null,
        Brush? hoverBackground = null)
    {
        var icon = new ClassicTransportGlyph(glyph);
        if (glyphSize is { } gs)
        {
            icon.Width = gs;
            icon.Height = gs;
        }
        var button = new Button
        {
            Content = icon,
            Width = width,
            Height = height,
            Margin = new Thickness(5, 0, 5, 0),
            Foreground = foreground ?? Muted,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            ToolTip = label,
            Template = TransportTemplate(cornerRadius: cornerRadius, hoverBackground: hoverBackground),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };
        icon.SetBinding(ClassicTransportGlyph.TintProperty, new Binding(nameof(Button.Foreground)) { Source = button });
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Border BuildLiquidGlassCaptionCapsule(StackPanel buttonsPanel)
    {
        var textureImage = new Image
        {
            Source = LiquidGlassCapsule.Texture,
            Width = LiquidGlassCapsule.TextureDipWidth,
            Height = LiquidGlassCapsule.TextureDipHeight,
            Margin = new Thickness(
                -LiquidGlassCapsule.PadLeft,
                -LiquidGlassCapsule.PadTop,
                -LiquidGlassCapsule.PadRight,
                -LiquidGlassCapsule.PadBottom),
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(textureImage, BitmapScalingMode.HighQuality);

        var capsuleHost = new Grid
        {
            Width = LiquidGlassCapsule.CapsuleWidth,
            Height = LiquidGlassCapsule.CapsuleHeight,
            UseLayoutRounding = true,
        };
        capsuleHost.Children.Add(textureImage);
        capsuleHost.Children.Add(buttonsPanel);

        return new Border
        {
            Child = capsuleHost,
            Width = LiquidGlassCapsule.CapsuleWidth,
            Height = LiquidGlassCapsule.CapsuleHeight,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 18, 0),
            Opacity = 0,
            IsHitTestVisible = false,
            UseLayoutRounding = true,
        };
    }

    private void RevealCaptionBar(bool animate = true)
    {
        _captionHideTimer?.Stop();
        if (_isShareSelecting)
        {
            HideCaptionBar(animate: false);
            return;
        }
        SetCaptionBarVisible(true, animate);
    }

    private void ScheduleCaptionBarHide(int delayMs = 450)
    {
        if (!_captionVisible) return;
        _captionHideTimer ??= new DispatcherTimer();
        _captionHideTimer.Stop();
        _captionHideTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _captionHideTimer.Tick -= OnCaptionHideTimerTick;
        _captionHideTimer.Tick += OnCaptionHideTimerTick;
        _captionHideTimer.Start();
    }

    private void OnCaptionHideTimerTick(object? sender, EventArgs e)
    {
        _captionHideTimer?.Stop();
        if (_captionBar.IsMouseOver && !_isShareSelecting) return;
        HideCaptionBar(animate: true);
    }

    private void HideCaptionBar(bool animate = true)
    {
        _captionHideTimer?.Stop();
        SetCaptionBarVisible(false, animate);
    }

    private void SetCaptionBarVisible(bool visible, bool animate)
    {
        if (_captionVisible == visible && _captionBar.IsHitTestVisible == visible) return;
        _captionVisible = visible;
        _captionBar.IsHitTestVisible = visible;
        _captionBar.BeginAnimation(OpacityProperty, null);
        var targetOpacity = visible ? 1.0 : 0.0;
        if (animate && SystemParameters.ClientAreaAnimation)
        {
            _captionBar.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            _captionBar.Opacity = targetOpacity;
        }
    }

    private void ShowTransportFeedback(string message)
    {
        _transportToastText.Text = message;
        _transportToast.Visibility = Visibility.Visible;
        _transportToastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _transportToastTimer.Stop();
        _transportToastTimer.Tick -= OnTransportToastTimerTick;
        _transportToastTimer.Tick += OnTransportToastTimerTick;
        _transportToastTimer.Start();
    }

    private void OnTransportToastTimerTick(object? sender, EventArgs e)
    {
        _transportToastTimer?.Stop();
        _transportToast.Visibility = Visibility.Collapsed;
    }

    /// <summary>歌词面板上、左、右三条边缘留白宽度（0 表示完全贴边占满）。</summary>
    private const double PanelChromeGap = 0;

    /// <summary>
    /// 歌词面板与窗口边缘之间的留白（上、左、右贴边占满，底边紧贴播放控制条；
    /// 圆角保持 0，避免与窗口圆角叠加露出额外暗角）。
    /// </summary>
    private void UpdatePanelFrame()
    {
        _panel.Margin = new Thickness(PanelChromeGap, PanelChromeGap, PanelChromeGap, 0);
        _panel.CornerRadius = new CornerRadius(0);
    }

    private void ToggleFullscreen() => SetFullscreen(!_isFullscreen);

    private void SetFullscreen(bool fullscreen)
    {
        if (_isFullscreen == fullscreen) return;
        _pressedRowIndex = null;
        _isFullscreen = fullscreen;
        if (fullscreen)
        {
            _wasMaximized = WindowState == WindowState.Maximized;
            _restoreBounds = _wasMaximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            if (_wasMaximized) WindowState = WindowState.Normal;
            var monitor = MonitorWorkArea.MonitorFor(this);
            _chrome.ResizeBorderThickness = new Thickness(0);
            UpdatePanelFrame();
            Left = monitor.Left;
            Top = monitor.Top;
            Width = monitor.Width;
            Height = monitor.Height;
        }
        else
        {
            _chrome.ResizeBorderThickness = new Thickness(5, 4, 5, 5);
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            if (_wasMaximized) WindowState = WindowState.Maximized;
            UpdatePanelFrame();
        }
        UpdateWindowCornerPreference();
        _relayoutFooter?.Invoke();
        ((ClassicTransportGlyph)_fullscreen.Content).SetState(
            fullscreen ? ClassicTransportIcon.ExitFullscreen : ClassicTransportIcon.Fullscreen);
        SetButtonLabel(_fullscreen, fullscreen ? "退出全屏 (F11 / Esc)" : "全屏 (F11)");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmExitSizeMove) SavePlacement();
        return IntPtr.Zero;
    }

    private void UpdateWindowCornerPreference()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var preference = _isFullscreen ? DwmwcpDoNotRound : DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // 旧版 Windows 等不支持 DWM 属性的环境下静默忽略
        }
    }

    private void RestorePlacement()
    {
        var maxWidth = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
        var maxHeight = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
        Width = Math.Clamp(_settings.ClassicLyricsWidth, MinWidth, maxWidth);
        Height = Math.Clamp(_settings.ClassicLyricsHeight, MinHeight, maxHeight);

        if (_settings.ClassicLyricsX is { } x && _settings.ClassicLyricsY is { } y && OnSomeScreen(x, y, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = x;
            Top = y;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void SavePlacement()
    {
        if (_isFullscreen)
        {
            if (_restoreBounds.Width >= MinWidth && _restoreBounds.Height >= MinHeight)
            {
                _settings.ClassicLyricsX = _restoreBounds.Left;
                _settings.ClassicLyricsY = _restoreBounds.Top;
                _settings.ClassicLyricsWidth = _restoreBounds.Width;
                _settings.ClassicLyricsHeight = _restoreBounds.Height;
                _settings.Save();
            }
            return;
        }
        if (WindowState != WindowState.Normal) return;

        KeepOnScreen();
        _settings.ClassicLyricsX = Left;
        _settings.ClassicLyricsY = Top;
        _settings.ClassicLyricsWidth = Math.Max(MinWidth, ActualWidth > 0 ? ActualWidth : Width);
        _settings.ClassicLyricsHeight = Math.Max(MinHeight, ActualHeight > 0 ? ActualHeight : Height);
        _settings.Save();
    }

    private void KeepOnScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;

        Left = Math.Clamp(Left, left, Math.Max(left, right - w));
        Top = Math.Clamp(Top, top, Math.Max(top, bottom - h));
    }

    private static bool OnSomeScreen(double x, double y, double width, double height)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        return virtualScreen.IntersectsWith(new Rect(x, y, width, height))
            && x + width > virtualScreen.Left + 40
            && y + height > virtualScreen.Top + 40
            && x < virtualScreen.Right - 40
            && y < virtualScreen.Bottom - 40;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    internal bool IsShareSelectingForTest => _isShareSelecting;
    internal IReadOnlyList<int> SelectedShareLineIndicesForTest => _selectedShareLines.OrderBy(i => i).ToList();
    internal string ShareCountTextForTest => _shareCountText.Text;
    internal string ShareLimitHintForTest => _shareLimitHintText.Visibility == Visibility.Visible ? _shareLimitHintText.Text : "";
    internal bool CanContinueShareForTest => _shareContinueBtn.IsEnabled;
    internal bool IsShareButtonEnabledForTest => _shareBtn.IsEnabled;
    internal bool IsCaptionBarVisibleForTest => _captionVisible && _captionBar.Opacity > 0.5 && _captionBar.IsHitTestVisible;
    internal void SetCaptionBarRevealForTest(bool visible) => SetCaptionBarVisible(visible, animate: false);
    internal void TriggerShareButtonForTest() => ToggleShareSelectionFromButton();
    internal void EnterShareSelectionModeForTest(int? lineIndex = null) => EnterShareSelectionMode(lineIndex);
    internal void ToggleShareRowForTest(int lineIndex) => ToggleShareRowSelection(lineIndex);
    internal void ExitShareSelectionModeForTest(bool resumeAutoScroll = true) => ExitShareSelectionMode(resumeAutoScroll);
    internal LyricSharePayload? CreateSharePayloadForTest() => BuildSharePayloadSnapshot();

    private (Border Header, Button CancelBtn, TextBlock CountText, TextBlock LimitHint) BuildShareHeaderBar()
    {
        var cancelBtn = new Button
        {
            Content = "取消",
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 14, 0),
            Background = CreateOffsetAlphaBrush(28, 255, 255, 255),
            Foreground = Brushes.White,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "退出分享选择 (Esc)",
            VerticalAlignment = VerticalAlignment.Center,
            Template = TransportTemplate(cornerRadius: 15),
        };
        AutomationProperties.SetName(cancelBtn, "取消分享选择");
        cancelBtn.Click += (_, _) => ExitShareSelectionMode(resumeAutoScroll: true);

        var titleText = new TextBlock
        {
            Text = "选择歌词",
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
        };

        var countText = new TextBlock
        {
            Text = "已选择 0 / 6 行歌词",
            FontSize = 11.5,
            Foreground = CreateOffsetAlphaBrush(215, 255, 255, 255),
            Margin = new Thickness(0, 1, 0, 0),
        };

        var limitHint = new TextBlock
        {
            Text = $"最多可选择 {MaxShareSelectionCount} 行歌词",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateOffsetBrush(255, 214, 102),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(titleText);
        textStack.Children.Add(countText);

        var leftRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftRow.Children.Add(cancelBtn);
        leftRow.Children.Add(textStack);
        leftRow.Children.Add(limitHint);

        var bar = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            Background = CreateOffsetAlphaBrush(228, 18, 18, 18),
            BorderBrush = CreateOffsetAlphaBrush(36, 255, 255, 255),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 10, 148, 10),
            Visibility = Visibility.Collapsed,
            Child = leftRow,
        };
        Panel.SetZIndex(bar, 15);
        return (bar, cancelBtn, countText, limitHint);
    }

    private (Border Host, Button ContinueBtn) BuildShareActionBar()
    {
        var continueBtn = new Button
        {
            Content = "继续",
            MinWidth = 136,
            Height = 44,
            Padding = new Thickness(28, 0, 28, 0),
            Background = Brushes.White,
            Foreground = Chrome,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "生成并预览歌词卡片",
            Template = TransportTemplate(cornerRadius: 22),
        };
        AutomationProperties.SetName(continueBtn, "继续生成歌词分享卡片");
        continueBtn.Click += (_, _) => OpenLyricSharePreview();

        var host = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 26),
            Visibility = Visibility.Collapsed,
            Child = continueBtn,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.48,
            },
        };
        Panel.SetZIndex(host, 15);
        return (host, continueBtn);
    }

    private ContextMenu CreateRowContextMenu(int lineIndex)
    {
        var menu = new ContextMenu
        {
            FontFamily = ClassicLyricsFonts.Body,
            Background = OffsetPopupBg,
            BorderBrush = OffsetPopupBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Template = DarkContextMenuTemplate(),
        };

        var itemTemplate = DarkMenuItemTemplate();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            if (!_isShareSelecting)
            {
                var shareItem = new MenuItem
                {
                    Header = "分享歌词…",
                    Foreground = Brushes.White,
                    Template = itemTemplate,
                };
                shareItem.Click += (_, _) => EnterShareSelectionMode(lineIndex);
                menu.Items.Add(shareItem);
                return;
            }

            var isSelected = _selectedShareLines.Contains(lineIndex);
            var toggleItem = new MenuItem
            {
                Header = isSelected ? "取消选中此行" : "选中此行",
                Foreground = Brushes.White,
                Template = itemTemplate,
            };
            toggleItem.Click += (_, _) => ToggleShareRowSelection(lineIndex);
            menu.Items.Add(toggleItem);

            var previewItem = new MenuItem
            {
                Header = "继续生成卡片…",
                IsEnabled = _selectedShareLines.Count > 0,
                Foreground = Brushes.White,
                Template = itemTemplate,
            };
            previewItem.Click += (_, _) => OpenLyricSharePreview();
            menu.Items.Add(previewItem);

            var cancelItem = new MenuItem
            {
                Header = "取消分享选择",
                Foreground = Brushes.White,
                Template = itemTemplate,
            };
            cancelItem.Click += (_, _) => ExitShareSelectionMode(resumeAutoScroll: true);
            menu.Items.Add(cancelItem);
        };
        return menu;
    }

    private static ControlTemplate DarkContextMenuTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, OffsetPopupBg);
        border.SetValue(Border.BorderBrushProperty, OffsetPopupBorder);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new Thickness(4));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(ContextMenu)) { VisualTree = border };
    }

    private static ControlTemplate DarkMenuItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bg");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new Thickness(12, 7, 14, 7));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = border };
        var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, OffsetBtnBg, "Bg"));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.42));
        template.Triggers.Add(disabled);
        return template;
    }

    private void OnLyricRowMouseEnter(int lineIndex)
    {
        if (!_isShareSelecting || !_rows.TryGetValue(lineIndex, out var row)) return;
        _hoveredShareLineIndex = lineIndex;
        SetRowVisual(row, lineIndex, animate: false);
    }

    private void OnLyricRowMouseLeave(int lineIndex)
    {
        if (!_isShareSelecting) return;
        if (_hoveredShareLineIndex == lineIndex)
            _hoveredShareLineIndex = null;
        if (_rows.TryGetValue(lineIndex, out var row))
            SetRowVisual(row, lineIndex, animate: false);
    }

    private void ToggleShareSelectionFromButton()
    {
        if (_isShareSelecting)
        {
            ExitShareSelectionMode(resumeAutoScroll: true);
            return;
        }
        if (_rows.Count == 0) return;
        EnterShareSelectionMode(initialLineIndex: null);
    }

    private void UpdateShareButton()
    {
        if (_shareBtn is null) return;
        var hasLyrics = _rows.Count > 0;
        _shareBtn.IsEnabled = hasLyrics || _isShareSelecting;
        ((ClassicTransportGlyph)_shareBtn.Content).SetState(ClassicTransportIcon.Share, active: _isShareSelecting);
        var label = !hasLyrics && !_isShareSelecting
            ? "暂无歌词可分享"
            : _isShareSelecting
                ? "退出歌词选择 (Esc)"
                : "分享歌词…";
        SetButtonLabel(_shareBtn, label);
    }

    private void EnterShareSelectionMode(int? initialLineIndex)
    {
        if (_lyrics is null || _rows.Count == 0) return;
        if (initialLineIndex is { } initial && !_rows.ContainsKey(initial)) return;

        _offsetPopup.IsOpen = false;
        CancelScrollAnimation();
        var keepManualOffset = initialLineIndex is null && _manualScroll;
        _manualScroll = false;
        _resume.Visibility = Visibility.Collapsed;
        _resumeScrollPending = false;

        _isShareSelecting = true;
        HideCaptionBar(animate: false);
        _shareSelectionLyrics = _lyrics;
        _hoveredShareLineIndex = null;
        _selectedShareLines.Clear();
        if (initialLineIndex is { } preselected)
            _selectedShareLines.Add(preselected);
        HideShareLimitHint();

        foreach (var row in _rows.Values)
            row.SetShareMode(true);

        ResizeLyrics();
        UpdateShareSelectionChrome();
        UpdateShareButton();
        foreach (var (idx, row) in _rows)
            SetRowVisual(row, idx, animate: true);

        var focusLine = initialLineIndex ?? (!keepManualOffset && _activeIndex >= 0 && _rows.ContainsKey(_activeIndex) ? _activeIndex : (int?)null);
        if (focusLine is { } targetLineIndex)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!_isShareSelecting || !_rows.TryGetValue(targetLineIndex, out var targetRow)) return;
                if (_scroll.ViewportHeight <= 0) return;

                var y = targetRow.Main.TranslatePoint(new Point(0, 0), _scroll).Y;
                var h = targetRow.ActualHeight > 0 ? targetRow.ActualHeight : targetRow.Main.ActualHeight;
                const double topPad = 86;
                const double bottomPad = 92;
                if (y < topPad)
                {
                    var next = Math.Max(0, _scroll.VerticalOffset + y - topPad);
                    _scroll.ScrollToVerticalOffset(next);
                }
                else if (y + h > _scroll.ViewportHeight - bottomPad)
                {
                    var next = Math.Clamp(_scroll.VerticalOffset + (y + h - (_scroll.ViewportHeight - bottomPad)), 0, _scroll.ScrollableHeight);
                    _scroll.ScrollToVerticalOffset(next);
                }
            });
        }
    }

    private void ToggleShareRowSelection(int lineIndex)
    {
        if (!_isShareSelecting || !_rows.TryGetValue(lineIndex, out var row)) return;

        if (_selectedShareLines.Contains(lineIndex))
        {
            _selectedShareLines.Remove(lineIndex);
            HideShareLimitHint();
        }
        else
        {
            if (_selectedShareLines.Count >= MaxShareSelectionCount)
            {
                ShowShareLimitHint();
                return;
            }
            _selectedShareLines.Add(lineIndex);
            if (_selectedShareLines.Count < MaxShareSelectionCount)
                HideShareLimitHint();
        }

        UpdateShareSelectionChrome();
        SetRowVisual(row, lineIndex, animate: true);
    }

    private void ExitShareSelectionMode(bool resumeAutoScroll)
    {
        if (!_isShareSelecting && _selectedShareLines.Count == 0) return;

        _isShareSelecting = false;
        _shareSelectionLyrics = null;
        _hoveredShareLineIndex = null;
        _selectedShareLines.Clear();
        HideShareLimitHint();

        _shareHeaderBar.Visibility = Visibility.Collapsed;
        _shareActionBar.Visibility = Visibility.Collapsed;

        foreach (var row in _rows.Values)
            row.SetShareMode(false);

        ResizeLyrics();
        UpdateShareButton();
        foreach (var (idx, row) in _rows)
            SetRowVisual(row, idx, animate: resumeAutoScroll);

        if (resumeAutoScroll)
        {
            _manualScroll = false;
            _resume.Visibility = Visibility.Collapsed;
            _resumeScrollPending = false;
            FollowActiveLine(animate: true);
            Refresh();
        }
    }

    private void UpdateShareSelectionChrome()
    {
        _shareHeaderBar.Visibility = _isShareSelecting ? Visibility.Visible : Visibility.Collapsed;
        _shareActionBar.Visibility = _isShareSelecting ? Visibility.Visible : Visibility.Collapsed;
        var count = _selectedShareLines.Count;
        _shareCountText.Text = $"已选择 {count} / {MaxShareSelectionCount} 行歌词";
        _shareContinueBtn.IsEnabled = count > 0;
        if (_isShareSelecting)
        {
            _shareContinueBtn.Background = count > 0
                ? Brushes.White
                : new SolidColorBrush(BlendColor(Colors.White, _subdued, 0.25));
            _shareHeaderBar.Background = new SolidColorBrush(Color.FromArgb(
                232,
                (byte)Math.Clamp((int)Math.Round(_background.R * 0.55), 14, 72),
                (byte)Math.Clamp((int)Math.Round(_background.G * 0.55), 14, 72),
                (byte)Math.Clamp((int)Math.Round(_background.B * 0.55), 14, 72)));
        }
    }

    private void ShowShareLimitHint()
    {
        _shareLimitHintText.Text = $"最多可选择 {MaxShareSelectionCount} 行歌词";
        _shareLimitHintText.Visibility = Visibility.Visible;
        _shareLimitHintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _shareLimitHintTimer.Stop();
        _shareLimitHintTimer.Tick -= OnShareLimitHintTick;
        _shareLimitHintTimer.Tick += OnShareLimitHintTick;
        _shareLimitHintTimer.Start();
    }

    private void OnShareLimitHintTick(object? sender, EventArgs e) => HideShareLimitHint();

    private void HideShareLimitHint()
    {
        _shareLimitHintTimer?.Stop();
        _shareLimitHintText.Visibility = Visibility.Collapsed;
    }

    private LyricSharePayload? BuildSharePayloadSnapshot()
    {
        if (!_isShareSelecting || _lyrics is null || _selectedShareLines.Count == 0)
            return null;

        var selectedLines = _selectedShareLines
            .Where(idx => idx >= 0 && idx < _lyrics.Lines.Count && _rows.ContainsKey(idx))
            .OrderBy(idx => idx)
            .Select(idx =>
            {
                var line = _lyrics.Lines[idx];
                var text = string.IsNullOrWhiteSpace(line.Content) ? "♪" : line.Content.Trim();
                return new ShareLyricLine(text, ExtractTranslation(line));
            })
            .ToList();

        if (selectedLines.Count == 0) return null;

        BitmapSource? coverSnapshot = null;
        if (_coverBytes is { Length: > 0 } bytes)
        {
            try { coverSnapshot = WindowsShareService.DecodeCoverBytes(bytes, maxDimension: 1600); }
            catch { coverSnapshot = null; }
        }
        coverSnapshot ??= _cover.Source as BitmapSource;
        var brightCoverColor = ClassicLyricsPalette.ShareCardColorFromCover(coverSnapshot, _coverBackground);

        return new LyricSharePayload(
            _track?.Title,
            _track?.Artist,
            coverSnapshot,
            brightCoverColor,
            selectedLines,
            _settings.ClassicShowTranslation,
            TextElement.GetFontFamily(_lines),
            _settings.ClassicLyricsFont);
    }

    private void OpenLyricSharePreview()
    {
        var payload = BuildSharePayloadSnapshot();
        if (payload is null) return;

        if (_shareWindow is { IsLoaded: true })
        {
            try { _shareWindow.Close(); } catch { }
        }

        // 进入二级分享卡片编辑窗时，主歌词界面立即恢复正常播放跟随状态
        ExitShareSelectionMode(resumeAutoScroll: true);

        _shareWindow = new LyricShareWindow(
            payload,
            _settings,
            onClosed: _ =>
            {
                _shareWindow = null;
            })
        {
            Owner = this,
        };
        _shareWindow.Show();
    }

    private LyricRow? FindAncestorLyricRow(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is LyricRow row && _rows.ContainsValue(row)) return row;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private bool IsInteractiveTarget(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or RangeBase) return true;
            if (ReferenceEquals(source, _captionBar)) return true;
            if (ReferenceEquals(source, _shareHeaderBar) || ReferenceEquals(source, _shareActionBar)) return true;
            if (source is LyricRow row && _rows.ContainsValue(row)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static string FormatTime(TimeSpan time) => $"{Math.Max(0, (int)time.TotalMinutes)}:{Math.Max(0, time.Seconds):00}";

    internal readonly record struct KaraokeSegment(int Start, int End, double StartTime, double EndTime, bool IsWhitespace);

    internal static IReadOnlyList<KaraokeSegment> BuildKaraokeSegments(string text, InlineTimeTags? timeTags)
    {
        if (string.IsNullOrEmpty(text) || timeTags is null || timeTags.Tags.Count == 0)
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
            var tailEnd = timeTags.Duration is { } dur && double.IsFinite(dur)
                ? Math.Max(prevTime, dur)
                : prevTime;
            AddRange(cursor, text.Length, prevTime, tailEnd);
        }

        var hasTiming = false;
        foreach (var seg in segments)
        {
            if (!seg.IsWhitespace && (seg.EndTime > seg.StartTime || seg.EndTime > 0))
            {
                hasTiming = true;
                break;
            }
        }
        return hasTiming ? segments : Array.Empty<KaraokeSegment>();
    }

    internal sealed class LyricRow : StackPanel
    {
        public SolidColorBrush ForegroundBrush { get; }
        public TextBlock Main { get; }
        public string? TranslationText { get; private set; }
        public bool HasTranslation => _slot is not null;
        public bool HasKaraoke => _karaokeRuns is not null;

        private readonly ScaleTransform _scale = new(1.0, 1.0);
        private readonly Border _cardBorder;
        private readonly StackPanel _cardContent;
        private TranslationSlot? _slot;
        private TextBlock? _sub;
        private TranslateTransform? _subTranslate;
        private double _mainSize = 24;
        private bool _isShareMode;

        private readonly IReadOnlyList<KaraokeSegment> _karaokeSegments;
        private readonly Run[]? _karaokeRuns;
        private SolidColorBrush? _unlitBrush;
        private SolidColorBrush? _litBrush;
        private SolidColorBrush? _fallbackActiveBrush;
        private LinearGradientBrush? _sweepBrush;
        private GradientStop? _sweepLitStop;
        private GradientStop? _sweepUnlitStop;
        private int _cachedSweepSegment = -1;
        private bool _cachedSweepCanSweep;
        private double _cachedSweepSpanPx;
        private bool _sweepGeometryDirty;
        private bool _isKaraokeRendered;
        private bool _hasLocalKaraokeBrushes;
        private double _lastKaraokeElapsed;
        private Color _lastUnlitColor;
        private Color _lastLitColor;

        /// <summary>用户在设置里选的行距档位倍率（1.0 = 标准）。</summary>
        public double LineHeightScale { get; set; } = 1.0;
        public double GapScale { get; set; } = 1.0;

        private static readonly IEasingFunction VisualEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        private static readonly TimeSpan VisualDuration = TimeSpan.FromMilliseconds(160);

        public LyricRow(
            string text,
            string? translation,
            Color initialColor,
            double progress,
            InlineTimeTags? timeTags = null)
        {
            Background = Brushes.Transparent;
            RenderTransformOrigin = new Point(0, 0.5);
            RenderTransform = _scale;
            Opacity = 1.0;
            ForegroundBrush = new SolidColorBrush(initialColor);
            Main = new TextBlock
            {
                Foreground = ForegroundBrush,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
            };

            _karaokeSegments = BuildKaraokeSegments(text, timeTags);
            if (_karaokeSegments.Count > 0)
            {
                _karaokeRuns = new Run[_karaokeSegments.Count];
                for (var i = 0; i < _karaokeSegments.Count; i++)
                {
                    var seg = _karaokeSegments[i];
                    var run = new Run(text[seg.Start..seg.End]);
                    _karaokeRuns[i] = run;
                    Main.Inlines.Add(run);
                }
                Main.SizeChanged += (_, _) =>
                {
                    _cachedSweepSegment = -1;
                    _sweepGeometryDirty = true;
                };
            }
            else
            {
                Main.Text = text;
            }

            _cardContent = new StackPanel();
            _cardContent.Children.Add(Main);
            _cardBorder = new Border
            {
                Background = Brushes.Transparent,
                Child = _cardContent,
            };
            Children.Add(_cardBorder);
            SetTranslation(translation, progress);
        }

        public void SetShareMode(bool active)
        {
            if (_isShareMode == active) return;
            _isShareMode = active;
            if (active)
            {
                _cardBorder.CornerRadius = new CornerRadius(14);
                _cardBorder.Padding = new Thickness(18, 11, 18, 11);
                _cardBorder.BorderThickness = new Thickness(0);
            }
            else
            {
                _cardBorder.CornerRadius = new CornerRadius(0);
                _cardBorder.Padding = new Thickness(0);
                _cardBorder.BorderThickness = new Thickness(0);
                _cardBorder.Background = Brushes.Transparent;
                _cardBorder.BorderBrush = Brushes.Transparent;
            }
            ApplySpacing(_mainSize);
        }

        public void UpdateShareVisualState(bool isSelected, bool isHovered, Color background, Color subdued, bool animate)
        {
            ClearKaraokeProgress();
            const double targetScale = 1.0;
            var targetOpacity = isSelected ? 1.0 : (isHovered ? 0.96 : 0.86);
            var targetColor = isSelected
                ? ClassicLyricsPalette.HighContrastDarkInk(background)
                : isHovered
                    ? Colors.White
                    : BlendColor(subdued, Colors.White, 0.58);

            var cardFill = isSelected
                ? Brushes.White
                : isHovered
                    ? new SolidColorBrush(Color.FromArgb(46, 255, 255, 255))
                    : Brushes.Transparent;
            _cardBorder.Background = cardFill;
            _cardBorder.BorderBrush = Brushes.Transparent;

            ApplyVisualTargets(targetScale, targetOpacity, targetColor, animate);
        }

        public void UpdateVisualState(bool isActive, bool isPast, Color targetColor, bool animate, bool isHovered = false)
        {
            if (!isActive || isHovered)
                ClearKaraokeProgress();

            const double targetScale = 1.0;
            const double targetOpacity = 1.0;
            ApplyVisualTargets(targetScale, targetOpacity, targetColor, animate);
        }

        private void ApplyVisualTargets(double targetScale, double targetOpacity, Color targetColor, bool animate)
        {
            if (animate && SystemParameters.ClientAreaAnimation)
            {
                if (Math.Abs(_scale.ScaleX - targetScale) > 0.001)
                {
                    var scaleAnim = new DoubleAnimation(_scale.ScaleX, targetScale, VisualDuration)
                    {
                        EasingFunction = VisualEase
                    };
                    _scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                    _scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                }

                if (Math.Abs(Opacity - targetOpacity) > 0.001)
                {
                    var opacityAnim = new DoubleAnimation(Opacity, targetOpacity, VisualDuration)
                    {
                        EasingFunction = VisualEase
                    };
                    BeginAnimation(OpacityProperty, opacityAnim);
                }

                var current = ForegroundBrush.Color;
                if (current != targetColor)
                {
                    ForegroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    ForegroundBrush.Color = targetColor;
                    var colorAnim = new ColorAnimation(current, targetColor, VisualDuration)
                    {
                        EasingFunction = VisualEase,
                        FillBehavior = FillBehavior.Stop,
                    };
                    ForegroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
                }
            }
            else
            {
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _scale.ScaleX = targetScale;
                _scale.ScaleY = targetScale;

                BeginAnimation(OpacityProperty, null);
                Opacity = targetOpacity;

                ForegroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                ForegroundBrush.Color = targetColor;
            }
        }

        public void RefreshKaraokeColors(Color unlitColor, Color litColor, bool enabled)
        {
            if (!enabled)
            {
                ClearKaraokeProgress();
                return;
            }
            if (!_isKaraokeRendered || _karaokeRuns is null) return;
            UpdateKaraokeProgress(_lastKaraokeElapsed, unlitColor, litColor, enabled: true, forceUpdate: true);
        }

        public void UpdateKaraokeProgress(double elapsed, Color unlitColor, Color litColor, bool enabled, bool forceUpdate = false)
        {
            if (_karaokeRuns is null || !enabled)
            {
                ClearKaraokeProgress();
                return;
            }

            if (!forceUpdate
                && _isKaraokeRendered
                && !_sweepGeometryDirty
                && Math.Abs(elapsed - _lastKaraokeElapsed) < 1e-6
                && unlitColor == _lastUnlitColor
                && litColor == _lastLitColor)
            {
                return;
            }

            _lastKaraokeElapsed = elapsed;
            _lastUnlitColor = unlitColor;
            _lastLitColor = litColor;
            EnsureKaraokeBrushes(unlitColor, litColor);

            var sweepGeometryDirty = false;
            for (var i = 0; i < _karaokeSegments.Count; i++)
            {
                var seg = _karaokeSegments[i];
                var run = _karaokeRuns[i];

                if (elapsed >= seg.EndTime)
                {
                    if (!ReferenceEquals(run.Foreground, _litBrush))
                        run.Foreground = _litBrush;
                }
                else if (elapsed <= seg.StartTime)
                {
                    if (!ReferenceEquals(run.Foreground, _unlitBrush))
                        run.Foreground = _unlitBrush;
                }
                else if (seg.IsWhitespace)
                {
                    if (!ReferenceEquals(run.Foreground, _litBrush))
                        run.Foreground = _litBrush;
                }
                else
                {
                    var dur = seg.EndTime - seg.StartTime;
                    var progress = dur > 0 ? Math.Clamp((elapsed - seg.StartTime) / dur, 0.0, 1.0) : 1.0;

                    if (!Main.IsArrangeValid || Main.ActualWidth <= 0)
                    {
                        _cachedSweepSegment = -1;
                        _cachedSweepCanSweep = false;
                        sweepGeometryDirty = true;
                    }
                    else if (_cachedSweepSegment != i || _sweepGeometryDirty)
                    {
                        var startRect = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                        var endRect = run.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                        var sameLine = Math.Abs(startRect.Top - endRect.Top) < Math.Max(4.0, _mainSize * 0.45);
                        var spanPx = Math.Abs(endRect.Left - startRect.Left);
                        if (sameLine && spanPx >= 1.0)
                        {
                            _sweepBrush!.StartPoint = new Point(startRect.Left, 0);
                            _sweepBrush.EndPoint = new Point(endRect.Left, 0);
                            _cachedSweepCanSweep = true;
                            _cachedSweepSpanPx = spanPx;
                        }
                        else
                        {
                            _cachedSweepCanSweep = false;
                        }
                        _cachedSweepSegment = i;
                    }

                    if (_cachedSweepCanSweep)
                    {
                        var feather = Math.Clamp((_mainSize * 0.35) / _cachedSweepSpanPx, 0.06, 0.28);
                        var edge0 = Math.Clamp(progress * (1.0 + feather) - feather, 0.0, 1.0);
                        var edge1 = Math.Clamp(progress * (1.0 + feather), 0.0, 1.0);
                        _sweepLitStop!.Offset = edge0;
                        _sweepUnlitStop!.Offset = edge1;
                        if (!ReferenceEquals(run.Foreground, _sweepBrush))
                            run.Foreground = _sweepBrush;
                    }
                    else
                    {
                        _fallbackActiveBrush!.Color = BlendColor(unlitColor, litColor, progress);
                        if (!ReferenceEquals(run.Foreground, _fallbackActiveBrush))
                            run.Foreground = _fallbackActiveBrush;
                    }
                }
            }

            _sweepGeometryDirty = sweepGeometryDirty;
            _isKaraokeRendered = true;
            _hasLocalKaraokeBrushes = true;
        }

        private void ClearKaraokeProgress()
        {
            _isKaraokeRendered = false;
            _cachedSweepSegment = -1;
            _sweepGeometryDirty = false;
            if (_karaokeRuns is null || !_hasLocalKaraokeBrushes) return;
            foreach (var run in _karaokeRuns)
                run.ClearValue(TextElement.ForegroundProperty);
            _hasLocalKaraokeBrushes = false;
        }

        private void EnsureKaraokeBrushes(Color unlitColor, Color litColor)
        {
            if (_unlitBrush is null)
            {
                _unlitBrush = new SolidColorBrush(unlitColor);
                _litBrush = new SolidColorBrush(litColor);
                _fallbackActiveBrush = new SolidColorBrush(unlitColor);
                _sweepLitStop = new GradientStop(litColor, 0.0);
                _sweepUnlitStop = new GradientStop(unlitColor, 0.0);
                _sweepBrush = new LinearGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute,
                    SpreadMethod = GradientSpreadMethod.Pad,
                };
                _sweepBrush.GradientStops.Add(_sweepLitStop);
                _sweepBrush.GradientStops.Add(_sweepUnlitStop);
            }
            else
            {
                if (_unlitBrush.Color != unlitColor)
                {
                    _unlitBrush.Color = unlitColor;
                    _sweepUnlitStop!.Color = unlitColor;
                }
                if (_litBrush!.Color != litColor)
                {
                    _litBrush.Color = litColor;
                    _sweepLitStop!.Color = litColor;
                }
            }
        }

        public void SetTranslation(string? translation, double progress)
        {
            if (string.IsNullOrWhiteSpace(translation))
            {
                TranslationText = null;
                if (_slot is not null)
                {
                    _cardContent.Children.Remove(_slot);
                    _slot = null;
                    _sub = null;
                    _subTranslate = null;
                    ApplySpacing(_mainSize);
                }
                return;
            }

            TranslationText = translation;
            if (_sub is not null && _slot is not null)
            {
                _sub.Text = translation;
                ApplyProgress(progress);
                return;
            }

            _subTranslate = new TranslateTransform(0, (1 - progress) * -6);
            _sub = new TextBlock
            {
                Text = translation,
                Foreground = ForegroundBrush,
                FontWeight = FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                RenderTransform = _subTranslate,
            };
            _slot = new TranslationSlot
            {
                Child = _sub,
                ClipToBounds = true,
            };
            ApplyProgress(progress);
            _cardContent.Children.Add(_slot);
            ApplySpacing(_mainSize);
        }

        public void ApplyProgress(double progress)
        {
            if (_slot is null || _sub is null || _subTranslate is null) return;
            var clamped = Math.Clamp(progress, 0d, 1d);
            if (clamped <= 0.001)
            {
                if (_slot.Visibility != Visibility.Visible)
                    _slot.Visibility = Visibility.Visible;
                _slot.Progress = 0d;
                _sub.Opacity = 0d;
                _subTranslate.Y = -6d;
            }
            else
            {
                if (_slot.Visibility != Visibility.Visible)
                    _slot.Visibility = Visibility.Visible;
                _slot.Progress = clamped;
                _sub.Opacity = Math.Clamp((clamped - 0.12) / 0.88, 0d, 1d);
                _subTranslate.Y = (1d - clamped) * -6d;
            }
        }

        /// <summary>
        /// 采用经典的紧凑歌词版式基准（正文 1.2 行高、译文 1.3 行高、0.62 字号行距），
        /// 并支持乘上用户在设置里调整的行距档位倍率。
        /// </summary>
        public void ApplySizing(double mainSize)
        {
            _mainSize = mainSize;
            _cachedSweepSegment = -1;
            _sweepGeometryDirty = true;
            Main.FontSize = mainSize;
            Main.LineHeight = mainSize * 1.2 * LineHeightScale;
            if (_sub is not null)
            {
                var subSize = Math.Max(14, Math.Round(mainSize * 0.56));
                _sub.FontSize = subSize;
                _sub.LineHeight = subSize * 1.3 * LineHeightScale;
            }
            ApplySpacing(mainSize);
        }

        /// <summary>
        /// 行间距放在“行内最后一个子元素”的下边距上，而不是 <see cref="FrameworkElement.Margin"/>
        /// （那一份不属于本行的命中区域）。否则两行之间那条空白会落到窗口背景上，
        /// 拖窗口与点击的分界就会错位。
        /// </summary>
        private void ApplySpacing(double mainSize)
        {
            Margin = new Thickness(0);
            Main.Margin = new Thickness(0);
            if (_isShareMode)
            {
                _cardBorder.Margin = new Thickness(0, 0, 0, 6);
                if (_sub is not null && _slot is not null)
                {
                    _sub.Margin = new Thickness(0, Math.Max(2, Math.Round(mainSize * 0.08)), 0, 0);
                    _slot.Margin = new Thickness(0);
                }
                return;
            }

            _cardBorder.Margin = new Thickness(0);
            var gap = Math.Max(4, mainSize * 0.62 * GapScale);
            if (_sub is not null && _slot is not null)
            {
                _sub.Margin = new Thickness(0, Math.Max(2, Math.Round(mainSize * 0.08)), 0, 0);
                _slot.Margin = new Thickness(0, 0, 0, gap);
            }
            else if (mainSize > 0)
            {
                Main.Margin = new Thickness(0, 0, 0, gap);
            }
        }

        public void SetUnderline(bool underline)
        {
            var decorations = underline ? TextDecorations.Underline : null;
            Main.TextDecorations = decorations;
            if (_sub is not null) _sub.TextDecorations = decorations;
        }
    }

    private sealed class TranslationSlot : Decorator
    {
        private double _progress = 1d;

        public double Progress
        {
            get => _progress;
            set
            {
                if (Math.Abs(_progress - value) < 0.0005) return;
                _progress = value;
                InvalidateMeasure();
            }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child is null || _progress <= 0.001) return default;
            Child.Measure(constraint);
            var desired = Child.DesiredSize;
            return new Size(desired.Width, desired.Height * _progress);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            if (Child is null) return arrangeSize;
            var desired = Child.DesiredSize;
            Child.Arrange(new Rect(0, 0, Math.Max(arrangeSize.Width, desired.Width), desired.Height));
            return arrangeSize;
        }
    }
}
