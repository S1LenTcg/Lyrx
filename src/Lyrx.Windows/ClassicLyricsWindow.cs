using System.IO;
using System.Runtime.InteropServices;
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
    private readonly Button _fullscreen;
    private readonly Action _onToggleTranslation;
    private readonly WindowChrome _chrome;
    private readonly Dictionary<int, LyricRow> _rows = new();
    private TrackInfo? _track;
    private Lyrics? _lyrics;
    private LyricsStatusKind? _status;
    private int _activeIndex = -1;
    private Color _subdued = DefaultSubdued;
    private Color _background = DefaultBackground;
    private Color _coverSubdued = DefaultSubdued;
    private Color _coverBackground = DefaultBackground;
    private bool _manualScroll;
    private long _manualScrollAt;
    private bool _resumeScrollPending;
    private bool _transportPending;
    private bool _isFullscreen;
    private Rect _restoreBounds;
    private bool _wasMaximized;
    private bool _isAnimatingTranslation;
    private bool _waitingForTranslationAnimation;
    private bool _latinMetrics;
    private bool _chineseLyrics;
    private double _lineHeightScale = 1.0;
    private double _rowGapScale = 1.0;
    private LyricRow? _translationAnchorRow;
    private double _translationAnchorViewportY;

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

        var layout = new DockPanel();
        var footer = new Grid { Margin = new Thickness(12, 0, 12, 0), UseLayoutRounding = true };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.RowDefinitions.Add(new RowDefinition());
        footer.RowDefinitions.Add(new RowDefinition());
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

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

        var windowControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _translate = ChromeButton(ClassicTransportIcon.Translate, "切换双语歌词", ToggleTranslation);
        UpdateTranslateButton();
        var settingsBtn = ChromeButton(ClassicTransportIcon.Settings, "设置", openSettings);
        var minimize = ChromeButton(ClassicTransportIcon.Minimize, "最小化", () => WindowState = WindowState.Minimized);
        _fullscreen = ChromeButton(ClassicTransportIcon.Fullscreen, "全屏 (F11)", ToggleFullscreen);
        var close = ChromeButton(ClassicTransportIcon.Close, "关闭", Close);
        windowControls.Children.Add(_translate);
        windowControls.Children.Add(settingsBtn);
        windowControls.Children.Add(minimize);
        windowControls.Children.Add(_fullscreen);
        windowControls.Children.Add(close);
        Grid.SetColumn(windowControls, 2);
        footer.Children.Add(windowControls);

        void LayoutFooter(double width)
        {
            var compact = width < 600;
            footer.Margin = compact ? new Thickness(8, 2, 8, 10) : new Thickness(12, 0, 12, 0);
            var coverSize = compact ? 44 : 56;
            coverFrame.Width = coverSize;
            coverFrame.Height = coverSize;
            coverContent.Clip = new RectangleGeometry(new Rect(0, 0, coverSize, coverSize), 4, 4);
            footer.Height = compact ? 120 : 78;
            footer.RowDefinitions[0].Height = compact ? new GridLength(56) : new GridLength(1, GridUnitType.Star);
            footer.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            footer.ColumnDefinitions[1].Width = compact
                ? new GridLength(0)
                : new GridLength(Math.Clamp(width * 0.44, 280, 722));
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
        LayoutFooter(Width - footer.Margin.Left - footer.Margin.Right);
        footer.SizeChanged += (_, e) => { if (e.WidthChanged) LayoutFooter(e.NewSize.Width); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _shuffle = TransportButton(ClassicTransportIcon.Shuffle, "随机播放", _player.ToggleShuffleAsync);
        _previous = TransportButton(ClassicTransportIcon.Previous, "上一首", _player.SkipPreviousAsync);
        _play = TransportButton(ClassicTransportIcon.Play, "播放", _player.TogglePlayPauseAsync, round: true);
        _next = TransportButton(ClassicTransportIcon.Next, "下一首", _player.SkipNextAsync);
        _repeat = TransportButton(ClassicTransportIcon.Repeat, "单曲循环", _player.ToggleRepeatOneAsync);
        buttons.Children.Add(_shuffle);
        buttons.Children.Add(_previous);
        buttons.Children.Add(_play);
        buttons.Children.Add(_next);
        buttons.Children.Add(_repeat);
        transport.Children.Add(buttons);

        // 底部进度条：支持鼠标悬停、拖拽与点击跳转播放（或校准时钟）
        _progress.SeekRequested += async seconds =>
        {
            await _player.SeekAsync(TimeSpan.FromSeconds(seconds));
            ResumeAutoScroll();
            Refresh();
        };
        var timeline = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        Grid.SetColumn(_progress, 1);
        Grid.SetColumn(_remaining, 2);
        timeline.Children.Add(_elapsed);
        timeline.Children.Add(_remaining);
        timeline.Children.Add(_progress);
        transport.Children.Add(timeline);

        _scroll = new ScrollViewer
        {
            Content = _lines,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        var lyricsView = new Grid();
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
        var syncMenu = new ContextMenu();
        var globalGroup = new MenuItem { Header = "所有歌曲" };
        foreach (var (label, offset) in new (string, double?)[]
        {
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
        _resume.Click += (_, _) =>
        {
            _coordinator.RefreshCurrentLine();
            ResumeAutoScroll();
            Refresh();
        };

        void AdjustTrackOffset(double? offset)
        {
            if (offset is null) _coordinator.TrackOffsetSeconds = 0;
            else _coordinator.TrackOffsetSeconds += offset.Value;
        }

        _panel = new Border
        {
            Child = lyricsView,
            Background = new SolidColorBrush(DefaultBackground),
            Margin = new Thickness(10, 16, 10, 6),
            CornerRadius = new CornerRadius(8),
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
        Closed += (_, _) => { _timer.Stop(); SavePlacement(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _isFullscreen)
            {
                SetFullscreen(false);
                e.Handled = true;
            }
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsInteractiveTarget(e.OriginalSource as DependencyObject)) return;
            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
            if (!_isFullscreen && WindowState == WindowState.Normal && e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
                e.Handled = true;
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

    private Button TransportButton(ClassicTransportIcon glyph, string label, Func<Task<bool>> action, bool round = false)
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
        button.Click += async (_, _) =>
        {
            if (_transportPending) return;
            _transportPending = true;
            try { await action(); }
            finally { _transportPending = false; Refresh(); }
        };
        return button;
    }

    private static ControlTemplate TransportTemplate(double cornerRadius)
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
        template.Triggers.Add(hover);
        return template;
    }

    private void Refresh()
    {
        AutoSyncIfIdle();

        var track = _coordinator.CurrentTrack;
        var lyrics = _coordinator.CurrentLyrics;
        var status = _coordinator.CurrentStatus?.Kind;
        var trackChanged = !Equals(track, _track);
        if (trackChanged)
        {
            _track = track;
            _activeIndex = -1;
            CancelScrollAnimation();
            _title.Text = track?.Title ?? "等待播放";
            _artist.Text = track?.Artist ?? "";
            _cover.Source = null;
            if (track is not null) _ = LoadCoverAsync(track);
            else
            {
                _coverBackground = DefaultBackground;
                _coverSubdued = DefaultSubdued;
                ApplyActivePalette();
            }
        }

        if (trackChanged || !ReferenceEquals(lyrics, _lyrics) || status != _status || _lines.Children.Count == 0)
        {
            var animateReplacement = !trackChanged
                && _settings.ClassicShowTranslation
                && _waitingForTranslationAnimation
                && lyrics is not null
                && HasAnyTranslation(lyrics);
            _lyrics = lyrics;
            _status = status;
            if (!trackChanged && _cover.Source is null && track is not null)
            {
                _ = LoadCoverAsync(track);
            }
            if (trackChanged)
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
            RenderLyrics(preserveScroll: !trackChanged);
            if (animateReplacement)
            {
                _waitingForTranslationAnimation = false;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AnimateTranslation(1d));
            }
        }

        var position = track is null ? null : _player.GetEstimatedPosition();
        var duration = track?.Duration ?? (lyrics?.Length > 0 ? TimeSpan.FromSeconds(lyrics.Length.Value) : (lyrics?.Lines.Count > 0 ? TimeSpan.FromSeconds(lyrics.Lines[^1].Position + 15) : null));
        if (position is { } p) position = TimeSpan.FromTicks(Math.Clamp(p.Ticks, 0, Math.Max(0, duration?.Ticks ?? long.MaxValue)));
        var state = _player.GetTransportState();
        UpdateTransport(state);

        // 进度条：反映真实播放位置，支持拖拽与实时时间预览
        var total = duration is { TotalSeconds: > 0 } known ? known : (TimeSpan?)null;
        _progress.Maximum = total?.TotalSeconds ?? 1;
        if (!_progress.IsDragging)
        {
            _progress.Value = Math.Clamp(position?.TotalSeconds ?? 0, 0, _progress.Maximum);
            _elapsed.Text = position is { } elapsed ? FormatTime(elapsed) : "–:––";
            _remaining.Text = total is { } remainingTotal && position is { } current
                ? $"−{FormatTime(remainingTotal - current)}"
                : "–:––";
        }
        else
        {
            var cur = TimeSpan.FromSeconds(_progress.Value);
            _elapsed.Text = FormatTime(cur);
            _remaining.Text = total is { } remTotal ? $"−{FormatTime(remTotal - cur)}" : "–:––";
        }

        if (lyrics is null || position is null) return;

        var adjusted = position.Value.TotalSeconds + lyrics.TimeDelay + _coordinator.ManualOffsetSeconds;
        var (active, _) = lyrics.LineIndexesAt(adjusted);
        var index = active ?? -1;
        if (_activeIndex != index)
        {
            var previousIndex = _activeIndex;
            _activeIndex = index;
            var isSequentialStep = previousIndex >= 0 && (index == previousIndex + 1);

            if (!isSequentialStep)
            {
                // 任意跳转 / 切歌 / 拖拽进度：瞬时对齐所有行，防止动画冲突与抽搐（鬼畜）
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
                // 正常单句过渡：丝滑过渡（上一句淡出收拢，当前句呼吸放大点亮）
                SetRowVisual(_rows.GetValueOrDefault(previousIndex), previousIndex, animate: true);
                if (_rows.TryGetValue(index, out var currentRow))
                {
                    SetRowVisual(currentRow, index, animate: true);
                    AutomationProperties.SetItemStatus(currentRow, "当前歌词");
                }
                if (!_manualScroll && !_isAnimatingTranslation)
                    FollowActiveLine(animate: true);
            }
            _resumeScrollPending = false;
        }
        else if (_resumeScrollPending && !_manualScroll && !_isAnimatingTranslation)
        {
            FollowActiveLine(animate: false);
        }
    }

    private void UpdateTransport(TransportState state)
    {
        _previous.IsEnabled = state.Controls.CanPrevious;
        _play.IsEnabled = state.Controls.CanPlayPause;
        _next.IsEnabled = state.Controls.CanNext;
        _shuffle.IsEnabled = state.Shuffle.HasValue;
        _repeat.IsEnabled = state.Repeat.HasValue;
        var repeatOne = state.Repeat == MediaPlaybackAutoRepeatMode.Track;
        var repeatList = state.Repeat == MediaPlaybackAutoRepeatMode.List;
        ((ClassicTransportGlyph)_play.Content).SetState(state.IsPlaying ? ClassicTransportIcon.Pause : ClassicTransportIcon.Play);
        ((ClassicTransportGlyph)_shuffle.Content).SetState(ClassicTransportIcon.Shuffle, state.Shuffle == true);
        ((ClassicTransportGlyph)_repeat.Content).SetState(repeatOne ? ClassicTransportIcon.RepeatOne : ClassicTransportIcon.Repeat, repeatOne || repeatList);
        SetButtonLabel(_play, state.IsPlaying ? "暂停" : "播放");
        SetButtonLabel(_shuffle, state.Shuffle switch { true => "关闭随机播放", false => "开启随机播放", _ => "当前播放器未提供随机播放控制" });
        SetButtonLabel(_repeat, state.Repeat switch
        {
            MediaPlaybackAutoRepeatMode.Track => "关闭单曲循环",
            MediaPlaybackAutoRepeatMode.List => "正在列表循环，点击切换为单曲循环",
            MediaPlaybackAutoRepeatMode.None => "开启单曲循环",
            _ => "当前播放器未提供循环控制",
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
            _latinMetrics = IsMostlyLatin(lyrics);
            _chineseLyrics = IsMostlyChinese(lyrics);
            for (var i = 0; i < lyrics.Lines.Count; i++)
            {
                var line = lyrics.Lines[i];
                if (!line.Enabled) continue;
                var mainText = string.IsNullOrWhiteSpace(line.Content) ? "♪" : line.Content;
                var subText = ExtractTranslation(line);
                var row = NewLine(mainText, subText, progress);
                var linePos = line.Position;
                row.Cursor = Cursors.Hand;
                row.MouseLeftButtonUp += async (_, e) =>
                {
                    var seekSeconds = linePos - lyrics.TimeDelay - _coordinator.ManualOffsetSeconds;
                    if (seekSeconds < 0) seekSeconds = 0;
                    await _player.SeekAsync(TimeSpan.FromSeconds(seekSeconds));
                    ResumeAutoScroll();
                    Refresh();
                    e.Handled = true;
                };
                _rows.Add(i, row);
                _lines.Children.Add(row);
            }
        }

        if (_rows.Count == 0)
        {
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
        else if (preserveScroll && _activeIndex >= 0)
        {
            foreach (var (rowIndex, row) in _rows)
                SetRowVisual(row, rowIndex, animate: false);
            if (!_manualScroll && !_isAnimatingTranslation)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FollowActiveLine(animate: false));
        }
        ResizeLyrics();
    }

    private LyricRow NewLine(string text, string? translation = null, double progress = 0d)
    {
        var row = new LyricRow(text, translation, _subdued, progress, _latinMetrics)
        {
            LineHeightScale = _lineHeightScale,
            GapScale = _rowGapScale,
        };
        return row;
    }

    /// <summary>
    /// 判断整首歌词是否以拉丁字母为主。英文歌用更松的行高/行距，
    /// 中文、日文、韩文等方块字保持紧凑那套；中英混排时按字数占比取胜的一方。
    /// </summary>
    private static bool IsMostlyLatin(Lyrics lyrics)
    {
        var latin = 0;
        var wide = 0;
        foreach (var line in lyrics.Lines)
        {
            if (!line.Enabled) continue;
            foreach (var c in line.Content)
            {
                if (c >= '\u2E80') wide++;            // CJK 汉字、假名、谚文、全角标点
                else if (char.IsLetter(c)) latin++;
            }
        }
        return latin > wide;
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
        _lines.Margin = new Thickness(side, top, side, _scroll.ActualHeight / 2);
        var size = CurrentMainFontSize();
        foreach (LyricRow line in _lines.Children)
            line.ApplySizing(size);
        if (_activeIndex >= 0 && !_isAnimatingTranslation) _resumeScrollPending = true;
    }

    private void PauseAutoScroll()
    {
        if (_rows.Count == 0 || _manualScroll) return;
        CancelScrollAnimation();
        _manualScroll = true;
        _manualScrollAt = Environment.TickCount64;
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
        if (seconds <= 0 || !_manualScroll || _rows.Count == 0) return;
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

    private void FollowActiveLine(bool animate = true)
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
            var durationSeconds = Math.Clamp(0.45 + Math.Min(distance / 1500.0, 0.20), 0.45, 0.65);
            var duration = TimeSpan.FromSeconds(durationSeconds);
            var animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(offset, KeyTime.FromTimeSpan(duration), ScrollSpline));
            BeginAnimation(FollowOffsetProperty, animation);
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

    private async Task LoadCoverAsync(TrackInfo track)
    {
        var preferOnlineCover = Services.NowPlayingService.IsBrowserOrVideoSource(track.SourceAppId);
        byte[]? bytes = null;

        if (preferOnlineCover)
        {
            // 浏览器与视频站点优先按清洗后的歌名从 QQ 音乐等在线库拉取官方专辑封面
            bytes = await Services.CoverFetcher.FetchCoverAsync(track.Title, track.Artist, _lyrics?.Metadata.ArtworkUrl);
            if (!Equals(_track, track) || !Equals(_player.CurrentTrack, track)) return;
        }

        if (bytes is null)
        {
            bytes = await _player.GetThumbnailAsync();
            if (!Equals(_track, track) || !Equals(_player.CurrentTrack, track)) return;
        }

        // 若本地 SMTC 未提供封面（例如 B 站桌面客户端），自动回退向在线音乐库检索高清专辑封面
        if (bytes is null && !preferOnlineCover)
        {
            bytes = await Services.CoverFetcher.FetchCoverAsync(track.Title, track.Artist, _lyrics?.Metadata.ArtworkUrl);
            if (!Equals(_track, track) || !Equals(_player.CurrentTrack, track)) return;
        }

        if (bytes is null)
        {
            _coverBackground = DefaultBackground;
            _coverSubdued = DefaultSubdued;
            ApplyActivePalette();
            return;
        }
        try
        {
            using var stream = new MemoryStream(bytes);
            var cover = new BitmapImage();
            cover.BeginInit();
            cover.StreamSource = stream;
            cover.DecodePixelWidth = 160;
            cover.CacheOption = BitmapCacheOption.OnLoad;
            cover.EndInit();
            cover.Freeze();
            _cover.Source = cover;

            (_coverBackground, _coverSubdued) = ClassicLyricsPalette.FromCover(cover);
            ApplyActivePalette();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or InvalidOperationException or IOException)
        {
            _coverBackground = DefaultBackground;
            _coverSubdued = DefaultSubdued;
            ApplyActivePalette();
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
        if (SystemParameters.ClientAreaAnimation && current != background)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(current, background, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        foreach (var (index, row) in _rows) SetRowVisual(row, index, animate: false);
        if (_rows.Count == 0 && _lines.Children.Count > 0 && _lines.Children[0] is LyricRow emptyRow)
        {
            emptyRow.Opacity = 1.0;
            SetRowVisual(emptyRow, isActive: false, isPast: false, subdued, animate: false);
        }
    }

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
        var isActive = index == _activeIndex;
        var isPast = index < _activeIndex;
        var color = isHovered ? Colors.White : RowColor(index);
        row.UpdateVisualState(isActive, isPast, color, animate, isHovered);
    }

    private static void SetRowVisual(LyricRow row, bool isActive, bool isPast, Color color, bool animate, bool isHovered = false)
    {
        row.UpdateVisualState(isActive, isPast, color, animate, isHovered);
    }

    private Button ChromeButton(ClassicTransportIcon glyph, string label, Action onClick)
    {
        var icon = new ClassicTransportGlyph(glyph);
        var button = new Button
        {
            Content = icon,
            Width = 32,
            Height = 36,
            Margin = new Thickness(1, 0, 1, 0),
            Foreground = Muted,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            ToolTip = label,
            Template = TransportTemplate(cornerRadius: 4),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
        };
        icon.SetBinding(ClassicTransportGlyph.TintProperty, new Binding(nameof(Button.Foreground)) { Source = button });
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>歌词面板上、左、右三条黑边的宽度；三者共用同一个值，保证看起来完全一致。</summary>
    private const double PanelChromeGap = 6;

    /// <summary>
    /// 歌词面板与窗口边缘之间的留白。
    /// 只保留上/左/右三条边（底边贴住播放控制条，不再留缝），三条边共用
    /// <see cref="PanelChromeGap"/>，所以宽度一致且很窄；圆角保持 0，
    /// 否则四个角会额外露出三角形底色，看着就像边变粗了。
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

    private bool IsInteractiveTarget(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            if (source is LyricRow row && _rows.ContainsValue(row)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static string FormatTime(TimeSpan time) => $"{Math.Max(0, (int)time.TotalMinutes)}:{Math.Max(0, time.Seconds):00}";

    private sealed class LyricRow : StackPanel
    {
        public SolidColorBrush ForegroundBrush { get; }
        public TextBlock Main { get; }
        public string? TranslationText { get; private set; }
        public bool HasTranslation => _slot is not null;

        private readonly ScaleTransform _scale = new(1.0, 1.0);
        private TranslationSlot? _slot;
        private TextBlock? _sub;
        private TranslateTransform? _subTranslate;
        private double _mainSize = 24;

        /// <summary>整首歌词是否以拉丁字母为主（英文等）。决定行高与行间距口径。</summary>
        private readonly bool _latinMetrics;

        /// <summary>用户在设置里选的行距档位倍率（1.0 = 标准）。</summary>
        public double LineHeightScale { get; set; } = 1.0;
        public double GapScale { get; set; } = 1.0;

        private static readonly IEasingFunction VisualEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        private static readonly TimeSpan VisualDuration = TimeSpan.FromMilliseconds(450);

        public LyricRow(string text, string? translation, Color initialColor, double progress, bool latinMetrics)
        {
            _latinMetrics = latinMetrics;
            Background = Brushes.Transparent;
            RenderTransformOrigin = new Point(0, 0.5);
            RenderTransform = _scale;
            Opacity = 0.72;
            ForegroundBrush = new SolidColorBrush(initialColor);
            Main = new TextBlock
            {
                Text = text,
                Foreground = ForegroundBrush,
                FontFamily = ClassicLyricsFonts.Title,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
            };
            Children.Add(Main);
            SetTranslation(translation, progress);
        }

        public void UpdateVisualState(bool isActive, bool isPast, Color targetColor, bool animate, bool isHovered = false)
        {
            var targetScale = isHovered ? (isActive ? 1.05 : 1.02) : (isActive ? 1.04 : 1.0);
            var targetOpacity = isHovered ? 1.0 : (isActive ? 1.0 : (isPast ? 0.45 : 0.72));

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

        public void SetTranslation(string? translation, double progress)
        {
            if (string.IsNullOrWhiteSpace(translation))
            {
                TranslationText = null;
                if (_slot is not null)
                {
                    Children.Remove(_slot);
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
                FontFamily = ClassicLyricsFonts.Body,
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
            Children.Add(_slot);
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
        /// 行高与行间距按"整首歌词的主语言"取两套基准口径，再乘用户在设置里选的行距档位：
        /// 中日韩方块字基准 1.32 行高 / 0.82 字号间距；英文等拉丁文本 1.45 / 1.00。
        /// 英文有上下伸部、谚文有收音，都比汉字更需要行间留白，写死一套必然有一边发闷。
        /// </summary>
        public void ApplySizing(double mainSize)
        {
            _mainSize = mainSize;
            Main.FontSize = mainSize;
            Main.LineHeight = mainSize * (_latinMetrics ? 1.45 : 1.32) * LineHeightScale;
            if (_sub is not null)
            {
                var subSize = Math.Max(14, Math.Round(mainSize * 0.56));
                _sub.FontSize = subSize;
                _sub.LineHeight = subSize * (_latinMetrics ? 1.4 : 1.34) * LineHeightScale;
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
            var gap = Math.Max(4, mainSize * (_latinMetrics ? 1.00 : 0.82) * GapScale);
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
