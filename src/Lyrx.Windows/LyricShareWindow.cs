using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Musebase.Windows.Services;
using IOPath = System.IO.Path;

namespace Musebase.Windows;

internal sealed record LyricSharePayload(
    string? TrackTitle,
    string? ArtistName,
    BitmapSource? CoverBitmap,
    Color CoverBackground,
    IReadOnlyList<ShareLyricLine> Lines,
    bool DefaultShowTranslation,
    FontFamily? LyricsFontFamily = null,
    string? InitialFontId = null);

/// <summary>
/// 歌词卡片预览与分享编辑窗口：
/// 顶部支持 Spotify / Apple Music 样式切换与译文显示开关，左侧实时高分辨率预览，
/// 右侧提供预设配色、本地图片背景、字体切换，以及保存高清 PNG、复制图片与系统分享。
/// </summary>
internal sealed class LyricShareWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private static readonly Brush BgShell = FrozenBrush(0, 0, 0);
    private static readonly Brush BgSurface = FrozenBrush(18, 18, 18);
    private static readonly Brush BgElevated = FrozenBrush(32, 32, 35);
    private static readonly Brush BgControl = FrozenBrush(38, 38, 42);
    private static readonly Brush BgControlHover = FrozenBrush(52, 52, 56);
    private static readonly Brush BgPopup = FrozenBrush(40, 40, 44);
    private static readonly Brush HairlineSubtle = FrozenBrush(Color.FromArgb(20, 255, 255, 255));
    private static readonly Brush BorderPopup = FrozenBrush(Color.FromArgb(36, 255, 255, 255));
    private static readonly Brush TextPrimary = Brushes.White;
    private static readonly Brush TextMuted = FrozenBrush(167, 167, 167);
    private static readonly Brush TextSubtle = FrozenBrush(122, 122, 128);
    private static readonly Brush AccentGreen = FrozenBrush(30, 215, 96);
    private static readonly Brush AccentGreenHover = FrozenBrush(59, 228, 119);

    private readonly LyricSharePayload _payload;
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<SharePalettePreset> _presets;
    private readonly IReadOnlyList<ClassicLyricsFonts.Choice> _fontChoices;
    private readonly bool _hasAnyTranslation;
    private readonly Action<bool>? _onClosed;

    private LyricShareStyle _style;
    private ShareBackgroundMode _backgroundMode;
    private ShareBackgroundMode _nonCustomBgMode;
    private int _colorIndex;
    private bool _showTranslation;
    private double _fontSizeScale;
    private ShareCoverEffectSettings _coverEffect;
    private bool _clipboardKeepBackdrop;
    private bool _hasAlbumCover;
    private BitmapSource? _customBgBitmap;
    private Color _customBgThemeColor;
    private string? _customBgLabel;
    private FontFamily _selectedLyricsFont;
    private BitmapSource? _cachedPreviewBitmap;
    private bool _hasExported;
    private bool _suppressFontSliderEvent;
    private bool _suppressEffectSliderEvent;

    private readonly Border _previewStage;
    private readonly LinearGradientBrush _stageBgBrush;
    private readonly GradientStop _stageMidStop;
    private readonly GradientStop _stageBottomStop;
    private readonly Border _previewShadowBackdrop;
    private readonly Border _previewCardClipFrame;
    private readonly Viewbox _previewViewbox;

    private readonly Grid _stylePillTrack;
    private readonly Border _styleThumb;
    private readonly TranslateTransform _styleThumbTransform;
    private readonly TextBlock _spotifyLabel;
    private readonly TextBlock _appleLabel;
    private Button? _translationBtn;
    private ClassicTransportGlyph? _translationGlyph;
    private TextBlock? _translationBtnLabel;

    private readonly Grid _bgModePillTrack;
    private readonly Border _bgModeThumb;
    private readonly TranslateTransform _bgModeThumbTransform;
    private readonly TextBlock _bgGradientLabel;
    private readonly TextBlock _bgSolidLabel;
    private readonly TextBlock _bgAlbumLabel;
    private readonly Border _bgAlbumSegment;
    private readonly TextBlock _swatchHintText;
    private readonly List<Border> _swatchOuterRings = new();
    private readonly List<TextBlock> _swatchLabels = new();
    private readonly Border _coverEffectCard;
    private readonly Dictionary<ShareCoverEffect, (Border Pill, TextBlock Label)> _effectPresetPills = new();
    private readonly Slider _blurSlider;
    private readonly TextBlock _blurValueText;
    private readonly Slider _dimSlider;
    private readonly TextBlock _dimValueText;
    private readonly Slider _fadeSlider;
    private readonly TextBlock _fadeValueText;
    private readonly Slider _tintSlider;
    private readonly TextBlock _tintValueText;
    private readonly Button _resetCoverEffectBtn;
    private readonly Button _customImageBtn;
    private readonly ClassicTransportGlyph _customImageBtnGlyph;
    private readonly TextBlock _customImageBtnLabel;
    private readonly TextBlock _customImageStatusText;
    private readonly Button _clearCustomImageBtn;
    private readonly Slider _fontScaleSlider;
    private readonly TextBlock _fontScaleValueText;
    private readonly Button _resetFontScaleBtn;
    private readonly Border _keepBackdropTrack;
    private readonly Border _keepBackdropKnob;
    private readonly TranslateTransform _keepBackdropKnobTransform;
    private readonly Border _feedbackBanner;
    private readonly TextBlock _feedbackText;

    internal ShareCardOptions CurrentOptionsForTest => BuildCurrentOptions();
    internal BitmapSource? PreviewBitmapForTest => _cachedPreviewBitmap ??= LyricShareCardRenderer.RenderBitmap(BuildCurrentOptions());
    internal BitmapSource ClipboardBitmapForTest() => LyricShareCardRenderer.RenderClipboardPreviewBitmap(BuildCurrentOptions(), _clipboardKeepBackdrop, ResolveThemeColor());
    internal string FeedbackTextForTest => _feedbackText.Text;
    internal bool HasExportedForTest => _hasExported;

    public LyricShareWindow(
        LyricSharePayload payload,
        AppSettings settings,
        Action<bool>? onClosed = null)
    {
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _onClosed = onClosed;

        if (_payload.Lines.Count == 0)
            throw new ArgumentException("至少需要一行歌词才能打开分享预览。", nameof(payload));

        _hasAlbumCover = _payload.CoverBitmap is { PixelWidth: > 0, PixelHeight: > 0 };
        var dynamicColor = _hasAlbumCover
            ? ClassicLyricsPalette.ShareCardColorFromCover(_payload.CoverBitmap, _payload.CoverBackground)
            : ClassicLyricsPalette.LiftToShareBrightness(_payload.CoverBackground, minLuminance: 0.065, maxLuminance: 0.175);
        _presets = LyricShareCardRenderer.BuildPresets(dynamicColor);
        _style = string.Equals(_settings.LyricShareStyle, "apple-music", StringComparison.OrdinalIgnoreCase)
            ? LyricShareStyle.AppleMusic
            : LyricShareStyle.Spotify;
        _nonCustomBgMode = ParseBackgroundMode(_settings.LyricShareBackgroundMode, _hasAlbumCover);
        _backgroundMode = _nonCustomBgMode;
        var initialScale = double.IsFinite(_settings.LyricShareFontScale) ? _settings.LyricShareFontScale : 1.0;
        _fontSizeScale = Math.Clamp(Math.Round(initialScale, 2), LyricShareCardRenderer.MinFontScale, LyricShareCardRenderer.MaxFontScale);
        _coverEffect = LoadCoverEffectSettings(_settings);
        _clipboardKeepBackdrop = _settings.LyricShareClipboardKeepBackdrop;
        _hasAnyTranslation = _payload.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation));
        _showTranslation = _hasAnyTranslation && _payload.DefaultShowTranslation;

        _fontChoices = ClassicLyricsFonts.GetChoices();
        var initialFontId = _payload.InitialFontId ?? _settings.ClassicLyricsFont;
        var initialFontIdx = Math.Max(0, _fontChoices.ToList().FindIndex(c => string.Equals(c.Id, initialFontId, StringComparison.Ordinal)));
        _selectedLyricsFont = _payload.LyricsFontFamily ?? _fontChoices[initialFontIdx].Font;

        Title = "分享歌词卡片 · Lyrx";
        Icon = TrayIcon.LoadAppIcon();
        Width = 1040;
        Height = 800;
        MinWidth = 780;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = BgShell;
        Foreground = TextPrimary;
        FontFamily = ClassicLyricsFonts.Body;

        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(5, 4, 5, 5),
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
            BorderBrush = FrozenBrush(Color.FromArgb(28, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Background = BgShell,
            ClipToBounds = true,
        };

        var rootDock = new DockPanel { Background = BgShell };
        outerBorder.Child = rootDock;
        Content = outerBorder;

        // ---- 1. 顶部极简 Spotify 顶栏（封面+曲目摘要 | 风格胶囊 | 译文开关 + 圆形关闭按钮） ----
        var headerGrid = new Grid
        {
            Height = 58,
            Background = BgShell,
            Margin = new Thickness(14, 0, 14, 0),
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        headerGrid.MouseLeftButtonDown += (_, e) =>
        {
            if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        };

        var headerLeft = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 12, 0),
        };
        headerLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var miniCoverHost = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(6),
            Background = BgElevated,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        if (_hasAlbumCover && _payload.CoverBitmap is { } headerCoverBmp)
        {
            var img = new Image
            {
                Source = headerCoverBmp,
                Stretch = Stretch.UniformToFill,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            miniCoverHost.Child = img;
        }
        else
        {
            miniCoverHost.Child = new TextBlock
            {
                Text = "♪",
                FontSize = 15,
                Foreground = TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        Grid.SetColumn(miniCoverHost, 0);
        headerLeft.Children.Add(miniCoverHost);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_payload.TrackTitle) ? "分享歌词卡片" : _payload.TrackTitle.Trim(),
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = BuildTrackSubtitleText(),
            FontSize = 11.5,
            Foreground = TextMuted,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        Grid.SetColumn(titleStack, 1);
        headerLeft.Children.Add(titleStack);
        Grid.SetColumn(headerLeft, 0);
        headerGrid.Children.Add(headerLeft);

        // 中央无边框风格切换胶囊
        _styleThumbTransform = new TranslateTransform();
        _styleThumb = new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = Brushes.White,
            RenderTransform = _styleThumbTransform,
            IsHitTestVisible = false,
        };
        Grid.SetColumn(_styleThumb, 0);

        _spotifyLabel = MakePillSegmentLabel("Spotify");
        _appleLabel = MakePillSegmentLabel("Apple Music");

        var spotifySeg = WrapPillSegment(_spotifyLabel, () => SwitchStyle(LyricShareStyle.Spotify));
        var appleSeg = WrapPillSegment(_appleLabel, () => SwitchStyle(LyricShareStyle.AppleMusic));
        Grid.SetColumn(spotifySeg, 0);
        Grid.SetColumn(appleSeg, 1);

        _stylePillTrack = new Grid { Width = 216, Height = 30 };
        _stylePillTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _stylePillTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _stylePillTrack.Children.Add(_styleThumb);
        _stylePillTrack.Children.Add(spotifySeg);
        _stylePillTrack.Children.Add(appleSeg);
        _stylePillTrack.SizeChanged += (_, _) => UpdateStylePillVisual(animate: false);

        var stylePillHost = new Border
        {
            Height = 36,
            Padding = new Thickness(3),
            CornerRadius = new CornerRadius(18),
            Background = BgSurface,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _stylePillTrack,
        };
        Grid.SetColumn(stylePillHost, 1);
        headerGrid.Children.Add(stylePillHost);

        // 顶部右侧：译文切换 + Spotify 圆形关闭按钮
        var headerRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (_hasAnyTranslation)
        {
            _translationGlyph = new ClassicTransportGlyph(ClassicTransportIcon.Bilingual)
            {
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            _translationBtnLabel = new TextBlock
            {
                Text = "译文",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var trContent = new StackPanel { Orientation = Orientation.Horizontal };
            trContent.Children.Add(_translationGlyph);
            trContent.Children.Add(_translationBtnLabel);

            _translationBtn = new Button
            {
                Content = trContent,
                Height = 32,
                Padding = new Thickness(12, 0, 13, 0),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                ToolTip = "显示或隐藏歌词译文",
                Template = RoundedPillTemplate(16, hasBorder: false),
            };
            AutomationProperties.SetName(_translationBtn, "显示或隐藏歌词译文");
            _translationBtn.Click += (_, _) =>
            {
                _showTranslation = !_showTranslation;
                RefreshAllVisuals();
            };
            headerRight.Children.Add(_translationBtn);
        }

        var closeBtn = new Button
        {
            Content = new ClassicTransportGlyph(ClassicTransportIcon.Close) { Width = 14, Height = 14 },
            Width = 32,
            Height = 32,
            Foreground = TextMuted,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "关闭 (Esc)",
            Template = CircularIconTemplate(),
        };
        ((ClassicTransportGlyph)closeBtn.Content).SetBinding(
            ClassicTransportGlyph.TintProperty,
            new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = closeBtn });
        AutomationProperties.SetName(closeBtn, "关闭分享窗口");
        closeBtn.Click += (_, _) => Close();
        headerRight.Children.Add(closeBtn);

        Grid.SetColumn(headerRight, 2);
        headerGrid.Children.Add(headerRight);

        DockPanel.SetDock(headerGrid, Dock.Top);
        rootDock.Children.Add(headerGrid);

        // ---- 2. Spotify 双浮岛主体（#000000 外框 + 8px 间距 + 圆角舞台 & #121212 编辑面板） ----
        var bodyGrid = new Grid
        {
            Margin = new Thickness(8, 0, 8, 8),
        };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(336) });
        rootDock.Children.Add(bodyGrid);

        // 2a. 左侧实时高清矢量预览舞台
        _stageMidStop = new GradientStop(Color.FromRgb(20, 38, 28), 0.52);
        _stageBottomStop = new GradientStop(Color.FromRgb(28, 72, 42), 1.0);
        _stageBgBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(16, 16, 18), 0.0),
                _stageMidStop,
                _stageBottomStop,
            },
        };

        _previewViewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(_previewViewbox, BitmapScalingMode.HighQuality);

        _previewCardClipFrame = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _previewViewbox,
        };
        _previewCardClipFrame.SizeChanged += (_, _) => UpdatePreviewClipRadius();

        _previewShadowBackdrop = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = FrozenBrush(Colors.Black),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 40,
                ShadowDepth = 12,
                Direction = 270,
                Opacity = 0.50,
            },
        };

        var previewHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        previewHost.Children.Add(_previewShadowBackdrop);
        previewHost.Children.Add(_previewCardClipFrame);

        _previewStage = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = _stageBgBrush,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(32, 26, 32, 26),
            ClipToBounds = true,
            AllowDrop = true,
            Child = previewHost,
        };
        _previewStage.PreviewDragOver += OnStageDragOver;
        _previewStage.PreviewDragLeave += (_, _) => _previewStage.BorderBrush = Brushes.Transparent;
        _previewStage.PreviewDrop += OnStageDrop;
        Grid.SetColumn(_previewStage, 0);
        bodyGrid.Children.Add(_previewStage);

        // 2b. 右侧 Spotify 风格无框悬浮编辑面板 (#121212)
        var sidebarDock = new DockPanel { Background = BgSurface };
        var sidebarCard = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = BgSurface,
            ClipToBounds = true,
            Child = sidebarDock,
        };
        Grid.SetColumn(sidebarCard, 2);
        bodyGrid.Children.Add(sidebarCard);

        // 底部操作区（固定于面板底部）
        var actionContainer = new Border
        {
            BorderBrush = HairlineSubtle,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 12, 18, 14),
        };
        var actionArea = new StackPanel();
        actionContainer.Child = actionArea;

        _feedbackText = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextPrimary,
        };
        _feedbackBanner = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Visibility = Visibility.Collapsed,
            Child = _feedbackText,
        };
        actionArea.Children.Add(_feedbackBanner);

        // 复制时是否保留卡片外背景开关（无框极简开关行）
        _keepBackdropKnobTransform = new TranslateTransform(0, 0);
        _keepBackdropKnob = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            RenderTransform = _keepBackdropKnobTransform,
        };
        _keepBackdropTrack = new Border
        {
            Width = 34,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = AccentGreen,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _keepBackdropKnob,
        };

        var keepBackdropRowGrid = new Grid();
        keepBackdropRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keepBackdropRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var keepBackdropLabel = new TextBlock
        {
            Text = "复制时保留卡片外背景",
            FontSize = 12,
            Foreground = FrozenBrush(226, 226, 230),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(keepBackdropLabel, 0);
        keepBackdropRowGrid.Children.Add(keepBackdropLabel);

        Grid.SetColumn(_keepBackdropTrack, 1);
        keepBackdropRowGrid.Children.Add(_keepBackdropTrack);

        var keepBackdropRow = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 6, 6, 6),
            Margin = new Thickness(-6, 0, -6, 8),
            Cursor = Cursors.Hand,
            ToolTip = "开启：复制圆角卡片与预览舞台颜色背景；关闭：仅复制透明底圆角卡片",
            Child = keepBackdropRowGrid,
        };
        keepBackdropRow.MouseEnter += (_, _) => keepBackdropRow.Background = BgElevated;
        keepBackdropRow.MouseLeave += (_, _) => keepBackdropRow.Background = Brushes.Transparent;
        keepBackdropRow.MouseLeftButtonUp += (_, _) =>
        {
            SetClipboardKeepBackdrop(!_clipboardKeepBackdrop, saveSettings: true);
        };
        actionArea.Children.Add(keepBackdropRow);

        var primaryRow = new UniformGrid { Columns = 2, Margin = new Thickness(-4, 0, -4, 6) };

        var saveBtn = BuildActionPill(
            ClassicTransportIcon.Download,
            "保存图片",
            bg: BgControl,
            hoverBg: BgControlHover,
            fg: Brushes.White,
            onClick: ExportPngViaDialog);
        saveBtn.Margin = new Thickness(4, 0, 4, 0);

        var copyBtn = BuildActionPill(
            ClassicTransportIcon.Copy,
            "复制图片",
            bg: AccentGreen,
            hoverBg: AccentGreenHover,
            fg: FrozenBrush(14, 14, 14),
            onClick: () => CopyCardToClipboard());
        copyBtn.Margin = new Thickness(4, 0, 4, 0);

        primaryRow.Children.Add(saveBtn);
        primaryRow.Children.Add(copyBtn);
        actionArea.Children.Add(primaryRow);

        var sysShareBtn = BuildTertiaryShareButton(ShareViaWindowsSystemUi);
        actionArea.Children.Add(sysShareBtn);

        DockPanel.SetDock(actionContainer, Dock.Bottom);
        sidebarDock.Children.Add(actionContainer);

        // 上方编辑区（Spotify 无框分层设计，默认尺寸下无需滚动即可展示全部选项）
        var controlsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
            Resources = { [typeof(ScrollBar)] = ClassicSettingsWindow.CreateDarkScrollBarStyle() },
        };
        var controlsStack = new StackPanel { Margin = new Thickness(18, 16, 18, 12) };
        controlsScroll.Content = controlsStack;
        sidebarDock.Children.Add(controlsScroll);

        // ---- 编辑区 1：背景样式与色调 ----
        var colorSection = new StackPanel();

        var bgTitleRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        bgTitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bgTitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bgTitle = new TextBlock
        {
            Text = "背景",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(bgTitle, 0);
        bgTitleRow.Children.Add(bgTitle);

        _customImageStatusText = new TextBlock
        {
            FontSize = 11,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 170,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(_customImageStatusText, 1);
        bgTitleRow.Children.Add(_customImageStatusText);
        colorSection.Children.Add(bgTitleRow);

        // 背景来源行：[渐变 | 纯色 | 封面] 滑块胶囊 + [图片…] (+ 清除 ×)
        var bgModeRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        bgModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bgModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        bgModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _bgGradientLabel = MakePillSegmentLabel("渐变");
        _bgSolidLabel = MakePillSegmentLabel("纯色");
        _bgAlbumLabel = MakePillSegmentLabel("封面");

        _bgModeThumbTransform = new TranslateTransform(0, 0);
        _bgModeThumb = new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = Brushes.White,
            RenderTransform = _bgModeThumbTransform,
            IsHitTestVisible = false,
        };

        _bgModePillTrack = new Grid { Height = 26 };
        _bgModePillTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bgModePillTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bgModePillTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_bgModeThumb, 0);
        _bgModePillTrack.Children.Add(_bgModeThumb);

        var bgGradientSeg = WrapPillSegment(_bgGradientLabel, () => SwitchBackgroundMode(ShareBackgroundMode.Gradient));
        Grid.SetColumn(bgGradientSeg, 0);
        _bgModePillTrack.Children.Add(bgGradientSeg);

        var bgSolidSeg = WrapPillSegment(_bgSolidLabel, () => SwitchBackgroundMode(ShareBackgroundMode.Solid));
        Grid.SetColumn(bgSolidSeg, 1);
        _bgModePillTrack.Children.Add(bgSolidSeg);

        _bgAlbumSegment = WrapPillSegment(_bgAlbumLabel, () =>
        {
            if (_hasAlbumCover)
                SwitchBackgroundMode(ShareBackgroundMode.AlbumCover);
        });
        if (!_hasAlbumCover)
        {
            _bgAlbumSegment.Cursor = Cursors.Arrow;
            _bgAlbumSegment.Opacity = 0.36;
            _bgAlbumSegment.ToolTip = "当前歌曲暂无可用专辑封面";
        }
        Grid.SetColumn(_bgAlbumSegment, 2);
        _bgModePillTrack.Children.Add(_bgAlbumSegment);
        _bgModePillTrack.SizeChanged += (_, _) => UpdateBackgroundModePillVisual(animate: false);

        var bgModePillFrame = new Border
        {
            Height = 32,
            Background = BgElevated,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(3),
            ClipToBounds = true,
            Child = _bgModePillTrack,
        };
        Grid.SetColumn(bgModePillFrame, 0);
        bgModeRow.Children.Add(bgModePillFrame);

        var imageBtnGroup = new StackPanel { Orientation = Orientation.Horizontal };
        _customImageBtnGlyph = new ClassicTransportGlyph(ClassicTransportIcon.Image)
        {
            Width = 13,
            Height = 13,
            Tint = Brushes.White,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _customImageBtnLabel = new TextBlock
        {
            Text = "选图",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var customBtnInner = new StackPanel { Orientation = Orientation.Horizontal };
        customBtnInner.Children.Add(_customImageBtnGlyph);
        customBtnInner.Children.Add(_customImageBtnLabel);

        _customImageBtn = new Button
        {
            Content = customBtnInner,
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            Background = BgControl,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "选择本地图片作为背景（也支持直接拖入左侧预览区）",
            Template = RoundedPillTemplate(16, hasBorder: false),
        };
        _customImageBtn.Click += (_, _) => OpenBackgroundImageDialog();
        imageBtnGroup.Children.Add(_customImageBtn);

        _clearCustomImageBtn = new Button
        {
            Content = new ClassicTransportGlyph(ClassicTransportIcon.Close)
            {
                Width = 10,
                Height = 10,
                Tint = TextMuted,
            },
            Width = 28,
            Height = 32,
            Margin = new Thickness(4, 0, 0, 0),
            Background = BgControl,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "清除自定义背景图片",
            Visibility = Visibility.Collapsed,
            Template = RoundedPillTemplate(14, hasBorder: false),
        };
        _clearCustomImageBtn.Click += (_, _) =>
        {
            _customBgBitmap = null;
            _customBgLabel = null;
            _backgroundMode = _nonCustomBgMode;
            ClearFeedback();
            RefreshAllVisuals();
        };
        imageBtnGroup.Children.Add(_clearCustomImageBtn);

        Grid.SetColumn(imageBtnGroup, 2);
        bgModeRow.Children.Add(imageBtnGroup);
        colorSection.Children.Add(bgModeRow);

        _swatchHintText = new TextBlock
        {
            Text = "色调预设",
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = TextMuted,
            Margin = new Thickness(0, 0, 0, 6),
        };
        colorSection.Children.Add(_swatchHintText);

        var swatchGrid = new UniformGrid { Columns = 4, Margin = new Thickness(-2, 0, -2, 0) };
        for (var i = 0; i < _presets.Count; i++)
        {
            var capturedIdx = i;
            var preset = _presets[i];

            var dotBrush = preset.AccentEndColor is { } endCol
                ? (Brush)new LinearGradientBrush(preset.BaseColor, endCol, new Point(0.1, 0), new Point(0.9, 1))
                : new SolidColorBrush(preset.BaseColor);
            dotBrush.Freeze();

            var isVeryDark = ClassicLyricsPalette.RelativeLuminance(preset.BaseColor) < 0.025;
            var dot = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = dotBrush,
                BorderBrush = isVeryDark ? FrozenBrush(Color.FromArgb(46, 255, 255, 255)) : Brushes.Transparent,
                BorderThickness = new Thickness(isVeryDark ? 1 : 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var ring = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(2),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = dot,
            };
            _swatchOuterRings.Add(ring);

            var label = new TextBlock
            {
                Text = preset.Label,
                FontSize = 10.5,
                Foreground = TextSubtle,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _swatchLabels.Add(label);

            var cell = new StackPanel
            {
                Margin = new Thickness(2, 2, 2, 6),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                ToolTip = preset.Label,
            };
            cell.Children.Add(ring);
            cell.Children.Add(label);
            cell.MouseEnter += (_, _) =>
            {
                if (_colorIndex != capturedIdx)
                    ring.BorderBrush = FrozenBrush(Color.FromArgb(64, 255, 255, 255));
            };
            cell.MouseLeave += (_, _) =>
            {
                if (_colorIndex != capturedIdx)
                    ring.BorderBrush = Brushes.Transparent;
            };
            cell.MouseLeftButtonUp += (_, _) =>
            {
                _colorIndex = capturedIdx;
                if (_backgroundMode != ShareBackgroundMode.AlbumCover && _backgroundMode != ShareBackgroundMode.CustomImage)
                {
                    _customBgBitmap = null;
                    _customBgLabel = null;
                    _backgroundMode = _nonCustomBgMode == ShareBackgroundMode.Solid
                        ? ShareBackgroundMode.Solid
                        : ShareBackgroundMode.Gradient;
                    _nonCustomBgMode = _backgroundMode;
                    _settings.LyricShareBackgroundMode = ToSettingString(_backgroundMode);
                    _settings.Save();
                }
                ClearFeedback();
                RefreshAllVisuals();
            };
            swatchGrid.Children.Add(cell);
        }
        colorSection.Children.Add(swatchGrid);
        controlsStack.Children.Add(colorSection);

        // ---- 编辑区 2：封面 / 图片背景画面质感（仅图片背景时展开，紧凑 2×2 滑块 + 无框芯片） ----
        var effectSection = new StackPanel();
        var effectHeader = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        effectHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        effectHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var effectTitle = new TextBlock
        {
            Text = "画面质感",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(effectTitle, 0);
        effectHeader.Children.Add(effectTitle);

        _resetCoverEffectBtn = BuildMicroPillButton("恢复预设", () =>
            ApplyCoverEffectPreset(_coverEffect.Preset, saveSettings: true));
        Grid.SetColumn(_resetCoverEffectBtn, 1);
        effectHeader.Children.Add(_resetCoverEffectBtn);
        effectSection.Children.Add(effectHeader);

        var effectPresetGrid = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 0, -3, 10) };
        var effectPresets = new (ShareCoverEffect Effect, string Label)[]
        {
            (ShareCoverEffect.Clean, "清晰原图"),
            (ShareCoverEffect.SoftBlur, "柔和虚化"),
            (ShareCoverEffect.DreamyBlur, "弥散光斑"),
            (ShareCoverEffect.ColorFade, "渐变融色"),
            (ShareCoverEffect.ColorTint, "氛围染印"),
            (ShareCoverEffect.Cinematic, "暗调电影"),
        };
        foreach (var (fxKind, fxLabel) in effectPresets)
        {
            var capturedFx = fxKind;
            var lbl = new TextBlock
            {
                Text = fxLabel,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextMuted,
            };
            var pill = new Border
            {
                Height = 28,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(14),
                Background = BgElevated,
                Cursor = Cursors.Hand,
                Child = lbl,
            };
            pill.MouseEnter += (_, _) =>
            {
                if (_coverEffect.Preset != capturedFx) pill.Background = BgControlHover;
            };
            pill.MouseLeave += (_, _) =>
            {
                if (_coverEffect.Preset != capturedFx) pill.Background = BgElevated;
            };
            pill.MouseLeftButtonUp += (_, _) => ApplyCoverEffectPreset(capturedFx, saveSettings: true);
            _effectPresetPills[fxKind] = (pill, lbl);
            effectPresetGrid.Children.Add(pill);
        }
        effectSection.Children.Add(effectPresetGrid);

        var slidersGrid = new Grid();
        slidersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        slidersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        slidersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        slidersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        slidersGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        slidersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        (_blurSlider, _blurValueText) = BuildEffectSliderCell(
            slidersGrid, 0, 0, "模糊", 0.0, 1.0, _coverEffect.BlurAmount,
            v => UpdateCoverEffectValues(_coverEffect with { BlurAmount = v }));
        (_dimSlider, _dimValueText) = BuildEffectSliderCell(
            slidersGrid, 0, 2, "暗度", 0.0, 0.85, _coverEffect.DimAmount,
            v => UpdateCoverEffectValues(_coverEffect with { DimAmount = v }));
        (_fadeSlider, _fadeValueText) = BuildEffectSliderCell(
            slidersGrid, 2, 0, "融色", 0.0, 1.0, _coverEffect.GradientFadeAmount,
            v => UpdateCoverEffectValues(_coverEffect with { GradientFadeAmount = v }));
        (_tintSlider, _tintValueText) = BuildEffectSliderCell(
            slidersGrid, 2, 2, "染印", 0.0, 1.0, _coverEffect.ColorTintAmount,
            v => UpdateCoverEffectValues(_coverEffect with { ColorTintAmount = v }));
        effectSection.Children.Add(slidersGrid);

        _coverEffectCard = new Border
        {
            BorderBrush = HairlineSubtle,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            Margin = new Thickness(0, 8, 0, 0),
            Child = effectSection,
        };
        controlsStack.Children.Add(_coverEffectCard);

        // ---- 编辑区 3：排版（字体与字号缩放） ----
        var fontSection = new StackPanel();
        var fontHeader = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        fontHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fontHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fontTitle = new TextBlock
        {
            Text = "排版",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(fontTitle, 0);
        fontHeader.Children.Add(fontTitle);

        var fontSizeRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fontScaleValueText = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        fontSizeRight.Children.Add(_fontScaleValueText);

        _resetFontScaleBtn = BuildMicroPillButton("恢复自动", () => UpdateFontScale(1.0, saveSettings: true));
        _resetFontScaleBtn.Margin = new Thickness(8, 0, 0, 0);
        fontSizeRight.Children.Add(_resetFontScaleBtn);

        Grid.SetColumn(fontSizeRight, 1);
        fontHeader.Children.Add(fontSizeRight);
        fontSection.Children.Add(fontHeader);

        var fontLabels = _fontChoices.Select(c => c.Label).ToList();
        var fontDropdown = CreateDarkFontDropdown(fontLabels, initialFontIdx, idx =>
        {
            _selectedLyricsFont = _fontChoices[Math.Clamp(idx, 0, _fontChoices.Count - 1)].Font;
            ClearFeedback();
            RefreshAllVisuals();
        });
        fontSection.Children.Add(fontDropdown);

        var fontSliderRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        fontSliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fontSliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        fontSliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var fontSizeCaption = new TextBlock
        {
            Text = "字号",
            FontSize = 11.5,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(fontSizeCaption, 0);
        fontSliderRow.Children.Add(fontSizeCaption);

        _fontScaleSlider = new Slider
        {
            Minimum = LyricShareCardRenderer.MinFontScale,
            Maximum = LyricShareCardRenderer.MaxFontScale,
            SmallChange = 0.05,
            LargeChange = 0.10,
            IsMoveToPointEnabled = true,
            Height = 20,
            Cursor = Cursors.Hand,
            Style = CreateSpotifySliderStyle(),
            Value = _fontSizeScale,
        };
        _fontScaleSlider.ValueChanged += (_, e) =>
        {
            if (_suppressFontSliderEvent) return;
            var nextScale = Math.Round(e.NewValue, 2);
            if (Math.Abs(nextScale - 1.0) <= 0.015) nextScale = 1.0;
            if (Math.Abs(nextScale - _fontSizeScale) < 0.005) return;
            UpdateFontScale(nextScale, saveSettings: false);
        };
        _fontScaleSlider.LostMouseCapture += (_, _) => PersistFontScaleIfChanged();
        _fontScaleSlider.KeyUp += (_, _) => PersistFontScaleIfChanged();
        Grid.SetColumn(_fontScaleSlider, 2);
        fontSliderRow.Children.Add(_fontScaleSlider);
        fontSection.Children.Add(fontSliderRow);

        var fontSectionHost = new Border
        {
            BorderBrush = HairlineSubtle,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 2),
            Margin = new Thickness(0, 12, 0, 0),
            Child = fontSection,
        };
        controlsStack.Children.Add(fontSectionHost);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        Loaded += (_, _) =>
        {
            UpdatePreviewClipRadius();
            UpdateStylePillVisual(animate: false);
            UpdateBackgroundModePillVisual(animate: false);
        };
        Closed += (_, _) =>
        {
            PersistPreferences();
            _onClosed?.Invoke(_hasExported);
        };

        RefreshAllVisuals();
    }

    internal void SelectStyleForTest(LyricShareStyle style) => SwitchStyle(style);
    internal void SetShowTranslationForTest(bool show)
    {
        if (!_hasAnyTranslation) return;
        _showTranslation = show;
        RefreshAllVisuals();
    }
    internal void SelectColorPresetForTest(int index)
    {
        _customBgBitmap = null;
        _customBgLabel = null;
        _colorIndex = Math.Clamp(index, 0, _presets.Count - 1);
        _backgroundMode = _nonCustomBgMode == ShareBackgroundMode.Solid
            ? ShareBackgroundMode.Solid
            : ShareBackgroundMode.Gradient;
        _nonCustomBgMode = _backgroundMode;
        RefreshAllVisuals();
    }
    internal void SetBackgroundModeForTest(ShareBackgroundMode mode)
    {
        if (mode == ShareBackgroundMode.CustomImage) return;
        if (mode == ShareBackgroundMode.AlbumCover && !_hasAlbumCover) return;
        SwitchBackgroundMode(mode);
    }
    internal void SetFontSizeScaleForTest(double scale) =>
        UpdateFontScale(Math.Clamp(Math.Round(scale, 2), LyricShareCardRenderer.MinFontScale, LyricShareCardRenderer.MaxFontScale), saveSettings: false);
    internal void SetCoverEffectPresetForTest(ShareCoverEffect preset) =>
        ApplyCoverEffectPreset(preset, saveSettings: false);
    internal void SetCoverEffectSettingsForTest(ShareCoverEffectSettings settings) =>
        UpdateCoverEffectValues(settings);
    internal void SetClipboardKeepBackdropForTest(bool keep) =>
        SetClipboardKeepBackdrop(keep, saveSettings: false);
    internal void SetCustomBackgroundForTest(string filePath) => LoadCustomBackgroundFromFile(filePath);
    internal bool SaveToPathForTest(string filePath) => TrySavePngToPath(filePath);
    internal bool CopyImageForTest() => CopyCardToClipboard();

    private void SetClipboardKeepBackdrop(bool keep, bool saveSettings)
    {
        if (_clipboardKeepBackdrop == keep) return;
        _clipboardKeepBackdrop = keep;
        _settings.LyricShareClipboardKeepBackdrop = keep;
        if (saveSettings)
            _settings.Save();
        UpdateKeepBackdropSwitchVisual(animate: IsLoaded);
    }

    private void UpdateKeepBackdropSwitchVisual(bool animate)
    {
        _keepBackdropTrack.Background = _clipboardKeepBackdrop ? AccentGreen : FrozenBrush(66, 66, 72);
        var targetX = _clipboardKeepBackdrop ? 16.0 : 0.0;

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            _keepBackdropKnobTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _keepBackdropKnobTransform.X = targetX;
            return;
        }

        var anim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _keepBackdropKnobTransform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private string BuildTrackSubtitleText()
    {
        var artist = string.IsNullOrWhiteSpace(_payload.ArtistName) ? "" : _payload.ArtistName.Trim();
        var countText = $"已选 {_payload.Lines.Count} 行";
        return string.IsNullOrEmpty(artist)
            ? countText
            : $"{artist} · {countText}";
    }

    private ShareCardOptions BuildCurrentOptions()
    {
        var hasCustom = _backgroundMode == ShareBackgroundMode.CustomImage
            && _customBgBitmap is { PixelWidth: > 0, PixelHeight: > 0 };
        var baseColor = hasCustom && _colorIndex == 0
            ? _customBgThemeColor
            : _presets[_colorIndex].BaseColor;
        var gradientEnd = hasCustom && _colorIndex == 0
            ? ClassicLyricsPalette.BlendColor(_customBgThemeColor, Colors.Black, 0.28)
            : _presets[_colorIndex].AccentEndColor;

        return new ShareCardOptions(
            _style,
            baseColor,
            _showTranslation,
            _payload.TrackTitle,
            _payload.ArtistName,
            _payload.CoverBitmap,
            _payload.Lines,
            hasCustom ? _customBgBitmap : null,
            _selectedLyricsFont,
            _backgroundMode,
            _fontSizeScale,
            gradientEnd,
            _coverEffect);
    }

    private void SwitchStyle(LyricShareStyle nextStyle)
    {
        if (_style == nextStyle) return;
        _style = nextStyle;
        _settings.LyricShareStyle = nextStyle == LyricShareStyle.AppleMusic ? "apple-music" : "spotify";
        _settings.Save();
        ClearFeedback();
        RefreshAllVisuals();
    }

    private void SwitchBackgroundMode(ShareBackgroundMode nextMode)
    {
        if (nextMode == ShareBackgroundMode.AlbumCover && !_hasAlbumCover) return;
        if (_backgroundMode == nextMode && _customBgBitmap is null) return;

        _customBgBitmap = null;
        _customBgLabel = null;
        _backgroundMode = nextMode;
        if (nextMode != ShareBackgroundMode.CustomImage)
        {
            _nonCustomBgMode = nextMode;
            _settings.LyricShareBackgroundMode = ToSettingString(nextMode);
            _settings.Save();
        }
        ClearFeedback();
        RefreshAllVisuals();
    }

    private void UpdateFontScale(double nextScale, bool saveSettings)
    {
        var clamped = Math.Clamp(Math.Round(nextScale, 2), LyricShareCardRenderer.MinFontScale, LyricShareCardRenderer.MaxFontScale);
        _fontSizeScale = clamped;
        _settings.LyricShareFontScale = clamped;
        if (saveSettings)
            _settings.Save();

        if (Math.Abs(_fontScaleSlider.Value - clamped) >= 0.005)
        {
            _suppressFontSliderEvent = true;
            try { _fontScaleSlider.Value = clamped; }
            finally { _suppressFontSliderEvent = false; }
        }

        ClearFeedback();
        RefreshAllVisuals();
    }

    private void ApplyCoverEffectPreset(ShareCoverEffect preset, bool saveSettings)
    {
        _coverEffect = ShareCoverEffectSettings.FromPreset(preset);
        SyncEffectSliders();
        SyncCoverEffectSettingsToModel();
        if (saveSettings)
            _settings.Save();
        ClearFeedback();
        RefreshAllVisuals();
    }

    private void UpdateCoverEffectValues(ShareCoverEffectSettings next)
    {
        _coverEffect = next with
        {
            BlurAmount = Math.Clamp(Math.Round(next.BlurAmount, 2), 0.0, 1.0),
            DimAmount = Math.Clamp(Math.Round(next.DimAmount, 2), 0.0, 0.85),
            GradientFadeAmount = Math.Clamp(Math.Round(next.GradientFadeAmount, 2), 0.0, 1.0),
            ColorTintAmount = Math.Clamp(Math.Round(next.ColorTintAmount, 2), 0.0, 1.0),
        };
        SyncEffectSliders();
        SyncCoverEffectSettingsToModel();
        ClearFeedback();
        RefreshAllVisuals();
    }

    private void SyncEffectSliders()
    {
        _suppressEffectSliderEvent = true;
        try
        {
            if (Math.Abs(_blurSlider.Value - _coverEffect.BlurAmount) >= 0.005) _blurSlider.Value = _coverEffect.BlurAmount;
            if (Math.Abs(_dimSlider.Value - _coverEffect.DimAmount) >= 0.005) _dimSlider.Value = _coverEffect.DimAmount;
            if (Math.Abs(_fadeSlider.Value - _coverEffect.GradientFadeAmount) >= 0.005) _fadeSlider.Value = _coverEffect.GradientFadeAmount;
            if (Math.Abs(_tintSlider.Value - _coverEffect.ColorTintAmount) >= 0.005) _tintSlider.Value = _coverEffect.ColorTintAmount;
        }
        finally
        {
            _suppressEffectSliderEvent = false;
        }
    }

    private (Slider Slider, TextBlock ValueText) BuildEffectSliderCell(
        Grid grid,
        int row,
        int column,
        string label,
        double min,
        double max,
        double initialValue,
        Action<double> onValueChanged)
    {
        var cell = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleTb, 0);
        header.Children.Add(titleTb);

        var valTb = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valTb, 1);
        header.Children.Add(valTb);
        cell.Children.Add(header);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = 0.04,
            LargeChange = 0.12,
            IsMoveToPointEnabled = true,
            Height = 18,
            Cursor = Cursors.Hand,
            Style = CreateSpotifySliderStyle(),
            Value = Math.Clamp(initialValue, min, max),
        };
        slider.ValueChanged += (_, e) =>
        {
            if (_suppressEffectSliderEvent) return;
            var rounded = Math.Round(e.NewValue, 2);
            onValueChanged(rounded);
        };
        slider.LostMouseCapture += (_, _) => PersistPreferences();
        slider.KeyUp += (_, _) => PersistPreferences();
        cell.Children.Add(slider);

        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
        return (slider, valTb);
    }

    private void SyncCoverEffectSettingsToModel()
    {
        _settings.LyricShareCoverEffectPreset = CoverPresetToString(_coverEffect.Preset);
        _settings.LyricShareCoverBlur = _coverEffect.BlurAmount;
        _settings.LyricShareCoverDim = _coverEffect.DimAmount;
        _settings.LyricShareCoverGradientFade = _coverEffect.GradientFadeAmount;
        _settings.LyricShareCoverTint = _coverEffect.ColorTintAmount;
    }

    private void PersistFontScaleIfChanged() => PersistPreferences();

    private void PersistPreferences()
    {
        _settings.LyricShareFontScale = _fontSizeScale;
        SyncCoverEffectSettingsToModel();
        _settings.Save();
    }

    private static ShareCoverEffectSettings LoadCoverEffectSettings(AppSettings settings)
    {
        var preset = settings.LyricShareCoverEffectPreset?.Trim().ToLowerInvariant() switch
        {
            "clean" => ShareCoverEffect.Clean,
            "dreamy-blur" => ShareCoverEffect.DreamyBlur,
            "color-fade" => ShareCoverEffect.ColorFade,
            "color-tint" => ShareCoverEffect.ColorTint,
            "cinematic" => ShareCoverEffect.Cinematic,
            _ => ShareCoverEffect.SoftBlur,
        };
        var defaults = ShareCoverEffectSettings.FromPreset(preset);
        return new ShareCoverEffectSettings(
            preset,
            BlurAmount: settings.LyricShareCoverBlur is { } b && double.IsFinite(b) ? Math.Clamp(b, 0.0, 1.0) : defaults.BlurAmount,
            DimAmount: settings.LyricShareCoverDim is { } d && double.IsFinite(d) ? Math.Clamp(d, 0.0, 0.85) : defaults.DimAmount,
            GradientFadeAmount: settings.LyricShareCoverGradientFade is { } f && double.IsFinite(f) ? Math.Clamp(f, 0.0, 1.0) : defaults.GradientFadeAmount,
            ColorTintAmount: settings.LyricShareCoverTint is { } t && double.IsFinite(t) ? Math.Clamp(t, 0.0, 1.0) : defaults.ColorTintAmount);
    }

    private static string CoverPresetToString(ShareCoverEffect preset) => preset switch
    {
        ShareCoverEffect.Clean => "clean",
        ShareCoverEffect.DreamyBlur => "dreamy-blur",
        ShareCoverEffect.ColorFade => "color-fade",
        ShareCoverEffect.ColorTint => "color-tint",
        ShareCoverEffect.Cinematic => "cinematic",
        _ => "soft-blur",
    };

    private static ShareBackgroundMode ParseBackgroundMode(string? raw, bool hasCover) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "solid" => ShareBackgroundMode.Solid,
            "album-cover" => hasCover ? ShareBackgroundMode.AlbumCover : ShareBackgroundMode.Gradient,
            _ => ShareBackgroundMode.Gradient,
        };

    private static string ToSettingString(ShareBackgroundMode mode) => mode switch
    {
        ShareBackgroundMode.Solid => "solid",
        ShareBackgroundMode.AlbumCover => "album-cover",
        _ => "gradient",
    };

    private Color ResolveThemeColor()
    {
        if (_backgroundMode == ShareBackgroundMode.CustomImage && _customBgBitmap is { PixelWidth: > 0, PixelHeight: > 0 })
            return _colorIndex == 0 ? _customBgThemeColor : _presets[_colorIndex].BaseColor;
        if (_backgroundMode == ShareBackgroundMode.AlbumCover && _hasAlbumCover)
            return _presets[_colorIndex].BaseColor;
        return _presets[_colorIndex].BaseColor;
    }

    private void RefreshAllVisuals()
    {
        var hasCustom = _backgroundMode == ShareBackgroundMode.CustomImage
            && _customBgBitmap is { PixelWidth: > 0, PixelHeight: > 0 };
        var isAlbumCover = _backgroundMode == ShareBackgroundMode.AlbumCover && _hasAlbumCover;
        var isImageBg = hasCustom || isAlbumCover;
        var themeColor = ResolveThemeColor();

        // 1. 预览舞台明媚氛围渐变
        _stageMidStop.Color = ClassicLyricsPalette.BlendColor(themeColor, Colors.Black, 0.45);
        _stageBottomStop.Color = ClassicLyricsPalette.BlendColor(themeColor, Colors.Black, 0.16);

        // 2. 样式切换胶囊、背景模式胶囊与外背景开关状态
        UpdateStylePillVisual(animate: IsLoaded);
        UpdateBackgroundModePillVisual(animate: IsLoaded);
        UpdateKeepBackdropSwitchVisual(animate: false);

        // 3. 译文开关样式（Spotify 克制图标状态）
        if (_translationBtn is not null && _translationGlyph is not null && _translationBtnLabel is not null)
        {
            _translationBtn.Background = _showTranslation ? BgElevated : BgSurface;
            _translationBtnGlyphTintSync(_showTranslation);
        }

        // 4. 色板提示与选中状态（未选中圆点不加灰圈，选中仅呈现纯白外环）
        _swatchHintText.Text = isImageBg ? "叠加基调" : "色调预设";
        for (var i = 0; i < _swatchOuterRings.Count; i++)
        {
            var selected = i == _colorIndex;
            _swatchOuterRings[i].BorderBrush = selected ? Brushes.White : Brushes.Transparent;
            if (i < _swatchLabels.Count)
            {
                _swatchLabels[i].Foreground = selected ? TextPrimary : TextSubtle;
                _swatchLabels[i].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        // 5. 封面高级特效面板状态（Spotify Encore 纯白/深灰胶囊）
        _coverEffectCard.Visibility = isImageBg ? Visibility.Visible : Visibility.Collapsed;
        if (isImageBg)
        {
            var presetDefaults = ShareCoverEffectSettings.FromPreset(_coverEffect.Preset);
            var isPresetCustomized =
                Math.Abs(_coverEffect.BlurAmount - presetDefaults.BlurAmount) >= 0.015
                || Math.Abs(_coverEffect.DimAmount - presetDefaults.DimAmount) >= 0.015
                || Math.Abs(_coverEffect.GradientFadeAmount - presetDefaults.GradientFadeAmount) >= 0.015
                || Math.Abs(_coverEffect.ColorTintAmount - presetDefaults.ColorTintAmount) >= 0.015;
            _resetCoverEffectBtn.Visibility = isPresetCustomized ? Visibility.Visible : Visibility.Collapsed;

            var activeInk = FrozenBrush(18, 18, 18);
            foreach (var (fxKind, (pill, lbl)) in _effectPresetPills)
            {
                var active = fxKind == _coverEffect.Preset;
                pill.Background = active ? Brushes.White : BgElevated;
                lbl.Foreground = active ? activeInk : TextMuted;
                lbl.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            }

            _blurValueText.Text = $"{Math.Round(_coverEffect.BlurAmount * 100)}%";
            _dimValueText.Text = $"{Math.Round(_coverEffect.DimAmount * 100)}%";
            _fadeValueText.Text = $"{Math.Round(_coverEffect.GradientFadeAmount * 100)}%";
            _tintValueText.Text = $"{Math.Round(_coverEffect.ColorTintAmount * 100)}%";
        }

        // 6. 自定义图片按钮高亮与字体大小状态
        if (hasCustom)
        {
            var blackInk = FrozenBrush(18, 18, 18);
            _customImageBtn.Background = Brushes.White;
            _customImageBtnGlyph.Tint = blackInk;
            _customImageBtnLabel.Foreground = blackInk;
            _customImageBtnLabel.FontWeight = FontWeights.Bold;
            _customImageBtnLabel.Text = "已选图";
            _clearCustomImageBtn.Visibility = Visibility.Visible;
            _customImageStatusText.Text = _customBgLabel ?? "已应用图片";
            _customImageStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            _customImageBtn.Background = BgControl;
            _customImageBtnGlyph.Tint = Brushes.White;
            _customImageBtnLabel.Foreground = Brushes.White;
            _customImageBtnLabel.FontWeight = FontWeights.SemiBold;
            _customImageBtnLabel.Text = "选图";
            _clearCustomImageBtn.Visibility = Visibility.Collapsed;
            _customImageStatusText.Text = "";
            _customImageStatusText.Visibility = Visibility.Collapsed;
        }

        var isAutoFont = Math.Abs(_fontSizeScale - 1.0) < 0.015;
        _fontScaleValueText.Text = isAutoFont ? "自动" : $"{Math.Round(_fontSizeScale * 100)}%";
        _fontScaleValueText.Foreground = isAutoFont ? TextMuted : TextPrimary;
        _resetFontScaleBtn.Visibility = isAutoFont ? Visibility.Collapsed : Visibility.Visible;

        // 7. 直接挂载实时高清矢量卡片至 Viewbox，预览随系统 DPI 无损缩放
        _cachedPreviewBitmap = null;
        try
        {
            _previewViewbox.Child = LyricShareCardRenderer.BuildCardVisual(BuildCurrentOptions(), out _, out _);
            UpdatePreviewClipRadius();
        }
        catch (Exception ex)
        {
            Log.Write($"[share] 渲染预览失败: {ex.Message}");
            ShowFeedback($"渲染歌词卡片失败：{ex.Message}", isError: true);
        }
    }

    private void _translationBtnGlyphTintSync(bool active)
    {
        if (_translationGlyph is null || _translationBtnLabel is null) return;
        _translationGlyph.Tint = active ? AccentGreen : TextMuted;
        _translationBtnLabel.Foreground = active ? TextPrimary : TextMuted;
    }

    private void UpdatePreviewClipRadius()
    {
        var w = _previewCardClipFrame.ActualWidth;
        var h = _previewCardClipFrame.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var radius = _style == LyricShareStyle.Spotify ? 20.0 : 16.0;
        var corner = new CornerRadius(radius);
        _previewCardClipFrame.CornerRadius = corner;
        _previewCardClipFrame.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
        _previewShadowBackdrop.Width = w;
        _previewShadowBackdrop.Height = h;
        _previewShadowBackdrop.CornerRadius = corner;
    }

    private void UpdateStylePillVisual(bool animate)
    {
        var activeInk = FrozenBrush(18, 18, 18);
        var isApple = _style == LyricShareStyle.AppleMusic;

        _styleThumb.Background = Brushes.White;
        _spotifyLabel.Foreground = isApple ? TextMuted : activeInk;
        _spotifyLabel.FontWeight = isApple ? FontWeights.SemiBold : FontWeights.Bold;
        _appleLabel.Foreground = isApple ? activeInk : TextMuted;
        _appleLabel.FontWeight = isApple ? FontWeights.Bold : FontWeights.SemiBold;

        var segW = _stylePillTrack.ActualWidth > 0 ? _stylePillTrack.ActualWidth / 2.0 : 108.0;
        var targetX = isApple ? segW : 0.0;

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            _styleThumbTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _styleThumbTransform.X = targetX;
            return;
        }

        var anim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _styleThumbTransform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void UpdateBackgroundModePillVisual(bool animate)
    {
        var activeInk = FrozenBrush(18, 18, 18);
        var isCustom = _backgroundMode == ShareBackgroundMode.CustomImage
            && _customBgBitmap is { PixelWidth: > 0, PixelHeight: > 0 };
        _bgModeThumb.Background = Brushes.White;
        _bgModeThumb.Opacity = isCustom ? 0.0 : 1.0;

        var activeSeg = isCustom
            ? -1
            : _backgroundMode switch
            {
                ShareBackgroundMode.Solid => 1,
                ShareBackgroundMode.AlbumCover when _hasAlbumCover => 2,
                _ => 0,
            };

        _bgGradientLabel.Foreground = activeSeg == 0 ? activeInk : TextMuted;
        _bgGradientLabel.FontWeight = activeSeg == 0 ? FontWeights.Bold : FontWeights.Medium;
        _bgSolidLabel.Foreground = activeSeg == 1 ? activeInk : TextMuted;
        _bgSolidLabel.FontWeight = activeSeg == 1 ? FontWeights.Bold : FontWeights.Medium;
        _bgAlbumLabel.Foreground = activeSeg == 2 ? activeInk : TextMuted;
        _bgAlbumLabel.FontWeight = activeSeg == 2 ? FontWeights.Bold : FontWeights.Medium;

        if (activeSeg < 0) return;

        var segW = _bgModePillTrack.ActualWidth > 0 ? _bgModePillTrack.ActualWidth / 3.0 : 68.0;
        var targetX = activeSeg * segW;

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            _bgModeThumbTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _bgModeThumbTransform.X = targetX;
            return;
        }

        var anim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _bgModeThumbTransform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private static Style CreateSpotifySliderStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="Slider">
                <Setter Property="Foreground" Value="#E2E2E6"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Slider">
                            <Grid Height="16" Background="Transparent" VerticalAlignment="Center">
                                <Border Height="4" CornerRadius="2" Background="#46464B" VerticalAlignment="Center"/>
                                <Track x:Name="PART_Track" Height="16" VerticalAlignment="Center">
                                    <Track.DecreaseRepeatButton>
                                        <RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False" Height="16" VerticalAlignment="Center">
                                            <RepeatButton.Template>
                                                <ControlTemplate TargetType="RepeatButton">
                                                    <Grid Height="16" Background="Transparent">
                                                        <Border Height="4" CornerRadius="2"
                                                                Background="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Slider}}"
                                                                VerticalAlignment="Center"/>
                                                    </Grid>
                                                </ControlTemplate>
                                            </RepeatButton.Template>
                                        </RepeatButton>
                                    </Track.DecreaseRepeatButton>
                                    <Track.IncreaseRepeatButton>
                                        <RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False" Height="16" VerticalAlignment="Center">
                                            <RepeatButton.Template>
                                                <ControlTemplate TargetType="RepeatButton">
                                                    <Grid Height="16" Background="Transparent"/>
                                                </ControlTemplate>
                                            </RepeatButton.Template>
                                        </RepeatButton>
                                    </Track.IncreaseRepeatButton>
                                    <Track.Thumb>
                                        <Thumb Width="14" Height="16" Cursor="Hand" VerticalAlignment="Center">
                                            <Thumb.Template>
                                                <ControlTemplate TargetType="Thumb">
                                                    <Grid Height="16" Background="Transparent">
                                                        <Border x:Name="Knob" Width="10" Height="10" CornerRadius="5" Background="White"
                                                                HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                                    </Grid>
                                                    <ControlTemplate.Triggers>
                                                        <Trigger Property="IsMouseOver" Value="True">
                                                            <Setter TargetName="Knob" Property="Width" Value="13"/>
                                                            <Setter TargetName="Knob" Property="Height" Value="13"/>
                                                            <Setter TargetName="Knob" Property="CornerRadius" Value="6.5"/>
                                                        </Trigger>
                                                        <Trigger Property="IsDragging" Value="True">
                                                            <Setter TargetName="Knob" Property="Width" Value="13"/>
                                                            <Setter TargetName="Knob" Property="Height" Value="13"/>
                                                            <Setter TargetName="Knob" Property="CornerRadius" Value="6.5"/>
                                                        </Trigger>
                                                    </ControlTemplate.Triggers>
                                                </ControlTemplate>
                                            </Thumb.Template>
                                        </Thumb>
                                    </Track.Thumb>
                                </Track>
                            </Grid>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
                <Style.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter Property="Foreground" Value="#1ED760"/>
                    </Trigger>
                    <Trigger Property="IsKeyboardFocusWithin" Value="True">
                        <Setter Property="Foreground" Value="#1ED760"/>
                    </Trigger>
                </Style.Triggers>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static Button BuildMicroPillButton(string text, Action onClick)
    {
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(cp);

        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = borderFactory };

        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                Foreground = TextMuted,
            },
            Height = 20,
            Padding = new Thickness(8, 0, 8, 0),
            Background = BgElevated,
            Cursor = Cursors.Hand,
            Template = tpl,
        };
        btn.MouseEnter += (_, _) => btn.Background = BgControlHover;
        btn.MouseLeave += (_, _) => btn.Background = BgElevated;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void OpenBackgroundImageDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择卡片背景图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        LoadCustomBackgroundFromFile(dialog.FileName);
    }

    private void LoadCustomBackgroundFromFile(string filePath)
    {
        try
        {
            var bmp = WindowsShareService.LoadBackgroundImage(filePath, maxDimension: 1600);
            _customBgBitmap = bmp;
            _customBgLabel = IOPath.GetFileName(filePath);
            _customBgThemeColor = ClassicLyricsPalette.ShareCardColorFromCover(bmp, _payload.CoverBackground);
            _backgroundMode = ShareBackgroundMode.CustomImage;
            ClearFeedback();
            RefreshAllVisuals();
        }
        catch (Exception ex)
        {
            Log.Write($"[share] 读取自定义背景图失败 ({filePath}): {ex.Message}");
            ShowFeedback($"无法读取背景图片：{ex.Message}", isError: true);
        }
    }

    private void OnStageDragOver(object sender, DragEventArgs e)
    {
        var canDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        _previewStage.BorderBrush = canDrop ? AccentGreen : Brushes.Transparent;
        e.Handled = true;
    }

    private void OnStageDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _previewStage.BorderBrush = Brushes.Transparent;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: >= 1 } paths)
            LoadCustomBackgroundFromFile(paths[0]);
    }

    private void ExportPngViaDialog()
    {
        var baseName = string.IsNullOrWhiteSpace(_payload.TrackTitle)
            ? "Lyrx_Lyrics"
            : string.IsNullOrWhiteSpace(_payload.ArtistName)
                ? _payload.TrackTitle!
                : $"{_payload.TrackTitle} - {_payload.ArtistName}";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存歌词卡片",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = WindowsShareService.SanitizeFileName(baseName) + "_Lyrx.png",
        };
        if (dialog.ShowDialog(this) != true) return;
        TrySavePngToPath(dialog.FileName);
    }

    private bool TrySavePngToPath(string filePath)
    {
        try
        {
            var rendered = LyricShareCardRenderer.RenderBitmap(BuildCurrentOptions());
            WindowsShareService.SavePngToFile(rendered, filePath);
            _hasExported = true;
            ShowFeedback($"已保存高清 PNG（{rendered.PixelWidth}×{rendered.PixelHeight}）：{IOPath.GetFileName(filePath)}", isError: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"[share] 保存 PNG 失败 ({filePath}): {ex.Message}");
            ShowFeedback($"保存图片失败：{ex.Message}", isError: true);
            return false;
        }
    }

    private bool CopyCardToClipboard()
    {
        try
        {
            var options = BuildCurrentOptions();
            var keepBackdrop = _clipboardKeepBackdrop;
            var rendered = LyricShareCardRenderer.RenderClipboardPreviewBitmap(
                options,
                keepBackdrop: keepBackdrop,
                stageThemeColor: ResolveThemeColor());
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowsShareService.CopyImageToClipboard(rendered, hwnd);
            _hasExported = true;
            var modeLabel = keepBackdrop ? "含卡片外背景" : "纯圆角卡片";
            ShowFeedback($"已复制{modeLabel}到剪贴板（{rendered.PixelWidth}×{rendered.PixelHeight}），可直接粘贴", isError: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"[share] 复制图片到剪贴板失败: {ex.Message}");
            ShowFeedback($"复制图片失败：{ex.Message}", isError: true);
            return false;
        }
    }

    private void ShareViaWindowsSystemUi()
    {
        BitmapSource rendered;
        try
        {
            rendered = LyricShareCardRenderer.RenderBitmap(BuildCurrentOptions());
        }
        catch (Exception ex)
        {
            Log.Write($"[share] 渲染分享图片失败: {ex.Message}");
            ShowFeedback($"生成分享图片失败：{ex.Message}", isError: true);
            return;
        }

        var title = string.IsNullOrWhiteSpace(_payload.TrackTitle) ? "Lyrx 歌词卡片" : _payload.TrackTitle.Trim();
        var desc = string.IsNullOrWhiteSpace(_payload.ArtistName) ? "来自 Lyrx 的歌词卡片" : _payload.ArtistName.Trim();
        var payload = new ShareTempPayload(title, desc, title, rendered);

        if (WindowsShareService.TryShowSystemShare(this, payload, out var errorMessage))
        {
            _hasExported = true;
            ShowFeedback("已打开 Windows 系统分享面板", isError: false);
        }
        else
        {
            ShowFeedback(errorMessage ?? "无法打开系统分享面板。", isError: true);
        }
    }

    private void ShowFeedback(string message, bool isError)
    {
        _feedbackText.Text = message;
        _feedbackBanner.Background = isError
            ? FrozenBrush(Color.FromArgb(46, 224, 82, 82))
            : FrozenBrush(Color.FromArgb(40, 30, 215, 96));
        _feedbackBanner.BorderBrush = isError
            ? FrozenBrush(Color.FromArgb(140, 224, 82, 82))
            : FrozenBrush(Color.FromArgb(130, 30, 215, 96));
        _feedbackBanner.Visibility = Visibility.Visible;
    }

    private void ClearFeedback()
    {
        _feedbackText.Text = "";
        _feedbackBanner.Visibility = Visibility.Collapsed;
    }

    private static TextBlock MakePillSegmentLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border WrapPillSegment(TextBlock label, Action onClick)
    {
        var host = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = label,
        };
        host.MouseLeftButtonUp += (_, _) => onClick();
        return host;
    }

    private static Button BuildActionPill(
        ClassicTransportIcon iconKind,
        string label,
        Brush bg,
        Brush hoverBg,
        Brush fg,
        Action onClick)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(new ClassicTransportGlyph(iconKind)
        {
            Width = 15,
            Height = 15,
            Tint = fg,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var btn = new Button
        {
            Content = stack,
            Height = 40,
            Background = bg,
            Foreground = fg,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = RoundedPillTemplate(20, hasBorder: false),
        };
        btn.MouseEnter += (_, _) => btn.Background = hoverBg;
        btn.MouseLeave += (_, _) => btn.Background = bg;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Button BuildTertiaryShareButton(Action onClick)
    {
        var glyph = new ClassicTransportGlyph(ClassicTransportIcon.Share)
        {
            Width = 14,
            Height = 14,
            Tint = TextMuted,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock
        {
            Text = "系统分享…",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(glyph);
        stack.Children.Add(text);

        var btn = new Button
        {
            Content = stack,
            Height = 32,
            Background = Brushes.Transparent,
            Foreground = TextMuted,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = RoundedPillTemplate(16, hasBorder: false),
        };
        btn.MouseEnter += (_, _) =>
        {
            btn.Background = BgElevated;
            glyph.Tint = Brushes.White;
            text.Foreground = Brushes.White;
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.Background = Brushes.Transparent;
            glyph.Tint = TextMuted;
            text.Foreground = TextMuted;
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static FrameworkElement CreateDarkFontDropdown(
        IReadOnlyList<string> items,
        int selectedIndex,
        Action<int> onSelected)
    {
        var currentIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, items.Count - 1));
        var selectedText = new TextBlock
        {
            Text = items.Count > 0 ? items[currentIndex] : "",
            Foreground = TextPrimary,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var chevron = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 4.5,4.5 L 9,0"),
            Stroke = TextMuted,
            StrokeThickness = 1.6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 1, 0, 0),
        };

        var boxContent = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        boxContent.Children.Add(chevron);
        boxContent.Children.Add(selectedText);

        var boxBorder = new Border
        {
            Background = BgElevated,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.Hand,
            Child = boxContent,
        };

        var popupStack = new StackPanel();
        var popupBorder = new Border
        {
            Background = BgPopup,
            BorderBrush = BorderPopup,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Child = new ScrollViewer
            {
                Content = popupStack,
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Resources = { [typeof(ScrollBar)] = ClassicSettingsWindow.CreateDarkScrollBarStyle() },
            },
        };
        System.Windows.Documents.TextElement.SetFontFamily(popupBorder, ClassicLyricsFonts.Body);

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

        var itemVisuals = new List<(TextBlock Text, TextBlock Check)>();
        void UpdateVisuals(int idx)
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
            var row = new DockPanel();
            var check = new TextBlock
            {
                Text = "✓",
                Foreground = AccentGreen,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 2, 0),
            };
            DockPanel.SetDock(check, Dock.Right);
            row.Children.Add(check);

            var label = new TextBlock
            {
                Text = items[i],
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            row.Children.Add(label);

            var itemBorder = new Border
            {
                Child = row,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 7, 8, 7),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            itemBorder.MouseEnter += (_, _) => itemBorder.Background = BgControlHover;
            itemBorder.MouseLeave += (_, _) => itemBorder.Background = Brushes.Transparent;
            itemBorder.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                popup.IsOpen = false;
                if (currentIndex != captured)
                {
                    UpdateVisuals(captured);
                    onSelected(captured);
                }
            };

            itemVisuals.Add((label, check));
            popupStack.Children.Add(itemBorder);
        }

        UpdateVisuals(currentIndex);

        var pressed = false;
        boxBorder.MouseEnter += (_, _) => { if (!popup.IsOpen) boxBorder.Background = BgControlHover; };
        boxBorder.MouseLeave += (_, _) => { pressed = false; if (!popup.IsOpen) boxBorder.Background = BgElevated; };
        boxBorder.MouseLeftButtonDown += (_, e) => { e.Handled = true; pressed = !popup.IsOpen; };
        boxBorder.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var open = pressed;
            pressed = false;
            if (popup.IsOpen) { popup.IsOpen = false; return; }
            if (!open) return;
            popupBorder.Width = Math.Max(180, boxBorder.ActualWidth);
            popup.IsOpen = true;
        };
        popup.Opened += (_, _) =>
        {
            boxBorder.IsHitTestVisible = false;
            boxBorder.BorderBrush = BorderPopup;
            boxBorder.Background = BgControlHover;
        };
        popup.Closed += (_, _) =>
        {
            pressed = false;
            boxBorder.BorderBrush = Brushes.Transparent;
            boxBorder.Background = boxBorder.IsMouseOver ? BgControlHover : BgElevated;
            boxBorder.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                () => boxBorder.IsHitTestVisible = true);
        };

        return boxBorder;
    }

    private static ControlTemplate RoundedPillTemplate(double cornerRadius, bool hasBorder)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Border");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        if (hasBorder)
        {
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        }

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.92));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.45));
        template.Triggers.Add(disabled);
        return template;
    }

    private static ControlTemplate CircularIconTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Circle");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, BgElevated, "Circle"));
        hover.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
        template.Triggers.Add(hover);
        return template;
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            if (source is Border border && border.Cursor == Cursors.Hand) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b) =>
        FrozenBrush(Color.FromRgb(r, g, b));

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
