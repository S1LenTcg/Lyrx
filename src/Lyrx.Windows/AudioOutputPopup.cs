using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Musebase.Windows.Services;

namespace Musebase.Windows;

internal static class AudioOutputVisualHelper
{
    public static ClassicTransportIcon ResolveIcon(AudioOutputKind kind, bool isMuted = false)
    {
        if (isMuted && kind == AudioOutputKind.Speaker)
        {
            return ClassicTransportIcon.MutedSpeaker;
        }

        return kind switch
        {
            AudioOutputKind.Bluetooth => ClassicTransportIcon.BluetoothAudio,
            AudioOutputKind.WiredOrUsb => ClassicTransportIcon.Headphones,
            _ => ClassicTransportIcon.Speaker,
        };
    }

    public static Color BlendColor(Color from, Color to, double ratio)
    {
        var t = Math.Clamp(ratio, 0.0, 1.0);
        return Color.FromArgb(
            255,
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    public static ClassicTransportGlyph CreateGlyph(ClassicTransportIcon icon, double size, Color color)
    {
        var glyph = new ClassicTransportGlyph(icon)
        {
            Width = size,
            Height = size,
            Tint = CreateFrozenBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        return glyph;
    }

    public static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 5-bar animated playback equalizer matching Android AudioWaveView.
/// </summary>
internal sealed class AudioWaveIndicator : FrameworkElement
{
    private const int BarCount = 5;
    private static readonly double[] BaseFractions = [0.42, 0.72, 1.0, 0.64, 0.84];
    private static readonly double[] Cycles = [1.0, 1.4, 0.9, 1.6, 1.2];
    private static readonly double[] PhaseOffsets = [0.0, 1.1, 2.3, 3.6, 4.8];

    private readonly DispatcherTimer _timer;
    private bool _isPlaying;
    private bool _isOpen;
    private double _phase;
    private long _lastTickMs;
    private Brush _activeBrush;
    private Brush _idleBrush;

    public AudioWaveIndicator()
    {
        Width = 22;
        Height = 16;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _activeBrush = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromRgb(0x1E, 0xD7, 0x60));
        _idleBrush = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _timer.Tick += OnTick;
        Unloaded += (_, _) => StopTimer();
    }

    public void SetState(bool isPlaying, bool isOpen)
    {
        _isPlaying = isPlaying;
        _isOpen = isOpen;
        SyncTimer();
        InvalidateVisual();
    }

    private void SyncTimer()
    {
        if (_isPlaying && _isOpen)
        {
            if (!_timer.IsEnabled)
            {
                _lastTickMs = Environment.TickCount64;
                _timer.Start();
            }
        }
        else
        {
            StopTimer();
        }
    }

    private void StopTimer()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Clamp((now - _lastTickMs) / 1000.0, 0.0, 0.1);
        _lastTickMs = now;
        _phase = (_phase + dt * (Math.PI * 2.0 / 1.35)) % (Math.PI * 2.0 * 100.0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var h = ActualHeight > 0 ? ActualHeight : Height;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        if (h <= 0 || w <= 0)
        {
            return;
        }

        const double barWidth = 2.4;
        const double gap = 2.4;
        var totalWidth = BarCount * barWidth + (BarCount - 1) * gap;
        var startX = Math.Max(0, (w - totalWidth) / 2.0);
        var centerY = h / 2.0;
        var radius = barWidth / 2.0;
        var brush = _isPlaying ? _activeBrush : _idleBrush;

        for (var i = 0; i < BarCount; i++)
        {
            double amplitude;
            if (_isPlaying)
            {
                var wave = 0.5 + 0.5 * Math.Sin(_phase * Cycles[i] + PhaseOffsets[i]);
                amplitude = BaseFractions[i] * (0.28 + 0.72 * wave);
            }
            else
            {
                amplitude = BaseFractions[i] * 0.28;
            }

            var barHeight = Math.Clamp(h * amplitude, barWidth + 1.0, h);
            var x = startX + i * (barWidth + gap);
            var y = centerY - barHeight / 2.0;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, barHeight), radius, radius);
        }
    }
}

/// <summary>
/// Interactive horizontal volume pill for the active audio output endpoint.
/// Uses complementary clipped light/dark ink layers so text remains crisp from 0% to 100%.
/// </summary>
internal sealed class VolumeOutputPill : Border
{
    private const double PillHeight = 52;
    private const double PillRadius = 26;

    private readonly Grid _root;
    private readonly Border _fillBar;
    private readonly Border _lightLayerHost;
    private readonly Border _darkLayerHost;
    private readonly ClassicTransportGlyph _lightIcon;
    private readonly ClassicTransportGlyph _darkIcon;
    private readonly TextBlock _lightTitle;
    private readonly TextBlock _darkTitle;
    private readonly TextBlock _lightSubtitle;
    private readonly TextBlock _darkSubtitle;

    private AudioOutputDevice? _device;
    private int _percent = 50;
    private bool _isMuted;
    private bool _isDragging;

    public event Action<int>? VolumeScrubbed;
    public event Action? MuteToggleRequested;

    internal int CurrentPercent => _percent;
    internal bool IsMuted => _isMuted;
    internal bool IsDragging => _isDragging;

    public VolumeOutputPill()
    {
        Height = PillHeight;
        CornerRadius = new CornerRadius(PillRadius);
        Background = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _root = new Grid();
        Child = _root;

        _fillBar = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF)),
            IsHitTestVisible = false,
        };
        _root.Children.Add(_fillBar);

        (_lightLayerHost, _lightIcon, _lightTitle, _lightSubtitle) = BuildContentLayer(darkInk: false);
        (_darkLayerHost, _darkIcon, _darkTitle, _darkSubtitle) = BuildContentLayer(darkInk: true);

        _root.Children.Add(_lightLayerHost);
        _root.Children.Add(_darkLayerHost);

        SizeChanged += (_, _) => UpdateGeometry();
        UpdateState(null, new AudioVolumeState(50, false), forceSync: true);
    }

    private static (Border Host, ClassicTransportGlyph Icon, TextBlock Title, TextBlock Subtitle) BuildContentLayer(bool darkInk)
    {
        var primaryColor = darkInk ? Color.FromRgb(0x14, 0x15, 0x18) : Color.FromArgb(0xF4, 0xFF, 0xFF, 0xFF);
        var secondaryColor = darkInk ? Color.FromArgb(0xB8, 0x14, 0x15, 0x18) : Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF);
        var badgeBg = darkInk ? Color.FromRgb(0x18, 0x1A, 0x1F) : Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF);
        var checkColor = darkInk ? Colors.White : Color.FromRgb(0x14, 0x15, 0x18);

        var grid = new Grid
        {
            Margin = new Thickness(16, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = AudioOutputVisualHelper.CreateGlyph(ClassicTransportIcon.Speaker, 20, primaryColor);
        icon.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        var title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = AudioOutputVisualHelper.CreateFrozenBrush(primaryColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        var subtitle = new TextBlock
        {
            FontSize = 11.2,
            Foreground = AudioOutputVisualHelper.CreateFrozenBrush(secondaryColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 1, 0, 0),
        };
        textStack.Children.Add(title);
        textStack.Children.Add(subtitle);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        var checkBadge = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = AudioOutputVisualHelper.CreateFrozenBrush(badgeBg),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                Width = 10,
                Height = 8,
                Stretch = Stretch.Uniform,
                Data = Geometry.Parse("M 1,4.2 L 3.8,7 L 9,1"),
                Stroke = AudioOutputVisualHelper.CreateFrozenBrush(checkColor),
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(checkBadge, 2);
        grid.Children.Add(checkBadge);

        var host = new Border
        {
            Child = grid,
            IsHitTestVisible = false,
        };
        return (host, icon, title, subtitle);
    }

    public void UpdateState(AudioOutputDevice? device, AudioVolumeState volume, bool forceSync = false)
    {
        _device = device;
        if (forceSync || !_isDragging)
        {
            _percent = Math.Clamp(volume.Percent, 0, 100);
        }
        _isMuted = volume.IsMuted;

        ApplyVisualTextAndIcons();
        UpdateGeometry();
    }

    private void ApplyVisualTextAndIcons()
    {
        var name = string.IsNullOrWhiteSpace(_device?.Name) ? "系统默认输出" : _device.Name;
        var kind = _device?.Kind ?? AudioOutputKind.Speaker;
        var iconKind = AudioOutputVisualHelper.ResolveIcon(kind, _isMuted);
        var subtitle = _isMuted ? $"已静音 · {_percent}%" : $"媒体音量 {_percent}%";

        _lightIcon.SetState(iconKind);
        _darkIcon.SetState(iconKind);
        _lightTitle.Text = name;
        _darkTitle.Text = name;
        _lightSubtitle.Text = subtitle;
        _darkSubtitle.Text = subtitle;

        ToolTip = $"{name} ({subtitle}) — 拖动或滚轮调节音量，右键静音";
        AutomationProperties.SetName(this, $"{name}，{subtitle}");
    }

    private void UpdateGeometry()
    {
        var width = ActualWidth;
        var height = ActualHeight > 0 ? ActualHeight : PillHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Clip = new RectangleGeometry(new Rect(0, 0, width, height), PillRadius, PillRadius);

        var fillWidth = Math.Clamp(width * (_percent / 100.0), 0.0, width);
        _fillBar.Width = fillWidth;
        _darkLayerHost.Clip = new RectangleGeometry(new Rect(0, 0, fillWidth, height));
        _lightLayerHost.Clip = new RectangleGeometry(new Rect(fillWidth, 0, Math.Max(0, width - fillWidth), height));
    }

    internal void ApplyVolumeFromScrub(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var changed = clamped != _percent || (_isMuted && clamped > 0);
        _percent = clamped;
        if (clamped > 0 && _isMuted)
        {
            _isMuted = false;
        }

        ApplyVisualTextAndIcons();
        UpdateGeometry();

        if (changed)
        {
            VolumeScrubbed?.Invoke(clamped);
        }
    }

    private void UpdateFromPointerX(double x)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var fraction = Math.Clamp(x / width, 0.0, 1.0);
        var percent = (int)Math.Round(fraction * 100.0);
        ApplyVolumeFromScrub(percent);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _isDragging = true;
        CaptureMouse();
        UpdateFromPointerX(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateFromPointerX(e.GetPosition(this).X);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
        {
            UpdateFromPointerX(e.GetPosition(this).X);
            _isDragging = false;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _isDragging = false;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        MuteToggleRequested?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var delta = e.Delta > 0 ? 2 : -2;
        ApplyVolumeFromScrub(_percent + delta);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 2;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Down:
                ApplyVolumeFromScrub(_percent - step);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Up:
                ApplyVolumeFromScrub(_percent + step);
                e.Handled = true;
                break;
            case Key.M:
            case Key.Space:
                MuteToggleRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new VolumeOutputPillAutomationPeer(this);

    private sealed class VolumeOutputPillAutomationPeer : FrameworkElementAutomationPeer, IRangeValueProvider
    {
        private readonly VolumeOutputPill _pill;

        public VolumeOutputPillAutomationPeer(VolumeOutputPill owner) : base(owner)
        {
            _pill = owner;
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(VolumeOutputPill);

        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        public bool IsReadOnly => false;
        public double LargeChange => 5;
        public double Maximum => 100;
        public double Minimum => 0;
        public double SmallChange => 2;
        public double Value => _pill._percent;

        public void SetValue(double value) => _pill.ApplyVolumeFromScrub((int)Math.Round(value));
    }
}

/// <summary>
/// Manages the floating audio output &amp; Bluetooth switcher popup anchored above the footer trigger.
/// </summary>
internal sealed class AudioOutputPopupController
{
    private readonly Window _owner;
    private WindowsAudioOutputMonitor _monitor;
    private readonly FrameworkElement _verticalAnchor;
    private readonly FrameworkElement _horizontalAnchor;
    private readonly FrameworkElement _boundsElement;
    private readonly Action _onOpened;
    private readonly Action _onClosed;

    private readonly Popup _popup;
    private readonly Border _card;
    private readonly ScrollViewer _listScroll;
    private readonly TranslateTransform _cardSlide;
    private readonly ScaleTransform _cardScale;
    private readonly Border _coverFallback;
    private readonly Image _coverImage;
    private readonly TextBlock _trackTitleText;
    private readonly TextBlock _trackSubtitleText;
    private readonly AudioWaveIndicator _waveIndicator;
    private readonly StackPanel _inactiveList;
    private readonly VolumeOutputPill _activePill;

    private Color _paletteBackground = Color.FromRgb(18, 22, 20);
    private bool _isPlaying;
    private string? _lastTrackTitle;
    private string? _lastTrackSubtitle;
    private ImageSource? _lastCover;
    private bool _trackInitialized;

    public bool IsOpen => _popup.IsOpen;
    internal Popup PopupForTest => _popup;
    internal Border CardForTest => _card;
    internal TextBlock TrackTitleForTest => _trackTitleText;
    internal TextBlock TrackSubtitleForTest => _trackSubtitleText;
    internal StackPanel InactiveListForTest => _inactiveList;
    internal VolumeOutputPill ActivePillForTest => _activePill;
    internal StackPanel RootPanelForTest => (StackPanel)_card.Child;

    public AudioOutputPopupController(
        Window owner,
        WindowsAudioOutputMonitor monitor,
        FrameworkElement verticalAnchor,
        FrameworkElement horizontalAnchor,
        FrameworkElement boundsElement,
        Action onOpened,
        Action onClosed)
    {
        _owner = owner;
        _monitor = monitor;
        _verticalAnchor = verticalAnchor;
        _horizontalAnchor = horizontalAnchor;
        _boundsElement = boundsElement;
        _onOpened = onOpened;
        _onClosed = onClosed;

        _cardSlide = new TranslateTransform(0, 0);
        _cardScale = new ScaleTransform(1, 1);
        var cardTransform = new TransformGroup();
        cardTransform.Children.Add(_cardScale);
        cardTransform.Children.Add(_cardSlide);

        var rootPanel = new StackPanel();

        // Header: cover (40x40 rounded) + title/artist + wave indicator
        var headerGrid = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var coverHost = new Grid
        {
            Width = 40,
            Height = 40,
            Margin = new Thickness(0, 0, 12, 0),
            Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 8, 8),
        };
        _coverFallback = new Border
        {
            Background = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = "♫",
                FontSize = 17,
                Foreground = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _coverImage = new Image
        {
            Stretch = Stretch.UniformToFill,
            Visibility = Visibility.Collapsed,
        };
        RenderOptions.SetBitmapScalingMode(_coverImage, BitmapScalingMode.HighQuality);
        coverHost.Children.Add(_coverFallback);
        coverHost.Children.Add(_coverImage);
        Grid.SetColumn(coverHost, 0);
        headerGrid.Children.Add(coverHost);

        var trackTextStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        _trackTitleText = new TextBlock
        {
            Text = "等待播放",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        _trackSubtitleText = new TextBlock
        {
            Text = "音频输出",
            FontSize = 11.5,
            Foreground = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        trackTextStack.Children.Add(_trackTitleText);
        trackTextStack.Children.Add(_trackSubtitleText);
        Grid.SetColumn(trackTextStack, 1);
        headerGrid.Children.Add(trackTextStack);

        _waveIndicator = new AudioWaveIndicator
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_waveIndicator, 2);
        headerGrid.Children.Add(_waveIndicator);

        rootPanel.Children.Add(headerGrid);

        // Divider
        rootPanel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 12, 0, 10),
            Background = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
        });

        // Scrollable device area
        var listHost = new StackPanel();
        _inactiveList = new StackPanel();
        listHost.Children.Add(_inactiveList);

        _activePill = new VolumeOutputPill
        {
            Margin = new Thickness(0),
        };
        _activePill.VolumeScrubbed += pct => _monitor.SetVolumePercent(pct);
        _activePill.MuteToggleRequested += () => _monitor.ToggleMute();
        listHost.Children.Add(_activePill);

        _listScroll = new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = listHost,
        };
        rootPanel.Children.Add(_listScroll);

        _card = new Border
        {
            Width = 320,
            Padding = new Thickness(14, 14, 14, 14),
            CornerRadius = new CornerRadius(18),
            BorderBrush = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            RenderTransformOrigin = new Point(0.5, 1.0),
            RenderTransform = cardTransform,
            Child = rootPanel,
            Effect = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 10,
                Direction = 270,
                Opacity = 0.56,
                Color = Colors.Black,
            },
        };
        ApplyCardTint();
        TextElement.SetFontFamily(_card, owner.FontFamily);

        var shadowMarginHost = new Border
        {
            Padding = new Thickness(16, 16, 16, 10),
            Background = Brushes.Transparent,
            Child = _card,
        };

        _popup = new Popup
        {
            Placement = PlacementMode.Top,
            PlacementTarget = _verticalAnchor,
            VerticalOffset = -2,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Child = shadowMarginHost,
        };

        _popup.Opened += (_, _) =>
        {
            _waveIndicator.SetState(_isPlaying, isOpen: true);
            _monitor.Refresh();
            _ = _monitor.RefreshPairedBluetoothAsync();
            AnimateOpen();
            _onOpened();
        };

        _popup.Closed += (_, _) =>
        {
            _waveIndicator.SetState(_isPlaying, isOpen: false);
            _onClosed();
        };
    }

    public void SetMonitor(WindowsAudioOutputMonitor monitor)
    {
        _monitor = monitor;
    }

    public void Toggle()
    {
        if (_popup.IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    public void Open()
    {
        if (_popup.IsOpen)
        {
            return;
        }

        SyncPopupBoundsAndPlacement();
        TextElement.SetFontFamily(_card, _owner.FontFamily);
        ApplyCardTint();
        _popup.IsOpen = true;
    }

    internal void SyncPopupBoundsAndPlacement()
    {
        var boundsW = _boundsElement.ActualWidth > 0 ? _boundsElement.ActualWidth : _owner.ActualWidth;
        var boundsH = _boundsElement.ActualHeight > 0
            ? _boundsElement.ActualHeight
            : Math.Max(220.0, _owner.ActualHeight - _verticalAnchor.ActualHeight);
        _card.Width = Math.Clamp(boundsW - 24.0, 240.0, 324.0);
        _card.MaxHeight = Math.Max(160.0, boundsH - 24.0);
        _listScroll.MaxHeight = Math.Clamp(boundsH - 130.0, 88.0, 340.0);
        SyncHorizontalOffset();
    }

    public void Close()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
        }
    }

    public void UpdatePalette(Color backgroundColor)
    {
        _paletteBackground = backgroundColor;
        ApplyCardTint();
    }

    public void UpdateTrack(string? title, string? subtitle, ImageSource? cover, bool isPlaying)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "等待播放" : title.Trim();
        var normalizedSubtitle = string.IsNullOrWhiteSpace(subtitle) ? "音频输出" : subtitle.Trim();
        if (_trackInitialized
            && string.Equals(_lastTrackTitle, normalizedTitle, StringComparison.Ordinal)
            && string.Equals(_lastTrackSubtitle, normalizedSubtitle, StringComparison.Ordinal)
            && ReferenceEquals(_lastCover, cover)
            && _isPlaying == isPlaying)
        {
            return;
        }

        _trackInitialized = true;
        _lastTrackTitle = normalizedTitle;
        _lastTrackSubtitle = normalizedSubtitle;
        _lastCover = cover;

        _trackTitleText.Text = normalizedTitle;
        _trackSubtitleText.Text = normalizedSubtitle;

        if (cover != null)
        {
            _coverImage.Source = cover;
            _coverImage.Visibility = Visibility.Visible;
            _coverFallback.Visibility = Visibility.Collapsed;
        }
        else
        {
            _coverImage.Source = null;
            _coverImage.Visibility = Visibility.Collapsed;
            _coverFallback.Visibility = Visibility.Visible;
        }

        _isPlaying = isPlaying;
        _waveIndicator.SetState(_isPlaying, _popup.IsOpen);
    }

    public void UpdateOutputs(
        AudioOutputDevice? active,
        IReadOnlyList<AudioOutputDevice> available,
        AudioVolumeState volume)
    {
        _inactiveList.Children.Clear();
        var inactive = available.Where(d => !d.IsActive).ToList();
        for (var i = 0; i < inactive.Count; i++)
        {
            var dev = inactive[i];
            var pill = CreateInactiveDevicePill(dev);
            pill.Margin = new Thickness(0, 0, 0, 8);
            _inactiveList.Children.Add(pill);
        }

        _activePill.UpdateState(active, volume, forceSync: false);
    }

    public void UpdateVolume(AudioVolumeState volume)
    {
        _activePill.UpdateState(_monitor.GetCurrentActiveOutput(), volume, forceSync: false);
    }

    private void SyncHorizontalOffset()
    {
        if (!_owner.IsVisible || _verticalAnchor.ActualWidth <= 0 || _boundsElement.ActualWidth <= 0)
        {
            _popup.HorizontalOffset = 0;
            return;
        }

        var boundsTopLeft = _boundsElement.TranslatePoint(new Point(0, 0), _owner);
        var verticalTopLeft = _verticalAnchor.TranslatePoint(new Point(0, 0), _owner);
        var anchorWidth = _horizontalAnchor.ActualWidth > 0 ? _horizontalAnchor.ActualWidth : 32.0;
        var anchorTopLeft = _horizontalAnchor.TranslatePoint(new Point(0, 0), _owner);
        var anchorCenterX = anchorTopLeft.X + anchorWidth / 2.0;

        // Keep card strictly inside the horizontal bounds of the lyrics area with 12px inner padding
        var minCardLeft = boundsTopLeft.X + 12.0;
        var maxCardLeft = Math.Max(minCardLeft, boundsTopLeft.X + _boundsElement.ActualWidth - _card.Width - 12.0);
        var desiredCardLeft = Math.Clamp(anchorCenterX - _card.Width / 2.0, minCardLeft, maxCardLeft);

        var desiredHostLeft = desiredCardLeft - 16.0;
        _popup.HorizontalOffset = Math.Round(desiredHostLeft - verticalTopLeft.X);
    }

    private void ApplyCardTint()
    {
        var baseDark = Color.FromRgb(0x12, 0x13, 0x16);
        var topTint = AudioOutputVisualHelper.BlendColor(baseDark, _paletteBackground, 0.30);
        var bottomTint = AudioOutputVisualHelper.BlendColor(baseDark, _paletteBackground, 0.18);
        topTint.A = 0xF5;
        bottomTint.A = 0xF7;

        var brush = new LinearGradientBrush(topTint, bottomTint, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        _card.Background = brush;
    }

    private void AnimateOpen()
    {
        _card.BeginAnimation(UIElement.OpacityProperty, null);
        _cardSlide.BeginAnimation(TranslateTransform.YProperty, null);
        _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var duration = TimeSpan.FromMilliseconds(175);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        _card.Opacity = 0.0;
        _cardSlide.Y = 10.0;
        _cardScale.ScaleX = 0.96;
        _cardScale.ScaleY = 0.96;

        _card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease });
        _cardSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10.0, 0.0, duration) { EasingFunction = ease });
        _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1.0, duration) { EasingFunction = ease });
        _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1.0, duration) { EasingFunction = ease });
    }

    private Button CreateInactiveDevicePill(AudioOutputDevice device)
    {
        var row = new Grid
        {
            Margin = new Thickness(15, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = AudioOutputVisualHelper.CreateGlyph(
            AudioOutputVisualHelper.ResolveIcon(device.Kind),
            18,
            Color.FromArgb(0xEA, 0xFF, 0xFF, 0xFF));
        icon.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var nameText = new TextBlock
        {
            Text = device.Name,
            FontSize = 12.8,
            FontWeight = FontWeights.SemiBold,
            Foreground = AudioOutputVisualHelper.CreateFrozenBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(nameText, 1);
        row.Children.Add(nameText);

        var chevron = AudioOutputVisualHelper.CreateGlyph(
            ClassicTransportIcon.ChevronRight,
            13,
            Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        Grid.SetColumn(chevron, 2);
        row.Children.Add(chevron);

        var btn = CreatePillButton(
            height: 44,
            radius: 22,
            normalBg: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
            hoverBg: Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF),
            pressedBg: Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF),
            content: row,
            tooltip: $"切换默认输出到 {device.Name}",
            onClick: () =>
            {
                if (!_monitor.SwitchDefaultOutput(device.Id))
                {
                    WindowsAudioOutputMonitor.TryOpenSystemSoundSettings();
                }
            });
        AutomationProperties.SetName(btn, $"切换到 {device.Name}");
        return btn;
    }

    private static Button CreatePillButton(
        double height,
        double radius,
        Color normalBg,
        Color hoverBg,
        Color pressedBg,
        UIElement content,
        string tooltip,
        Action onClick)
    {
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "PillBg";
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        borderFactory.SetValue(Border.BackgroundProperty, AudioOutputVisualHelper.CreateFrozenBrush(normalBg));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = borderFactory };

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, AudioOutputVisualHelper.CreateFrozenBrush(hoverBg), "PillBg"));
        template.Triggers.Add(hoverTrigger);

        var pressTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressTrigger.Setters.Add(new Setter(Border.BackgroundProperty, AudioOutputVisualHelper.CreateFrozenBrush(pressedBg), "PillBg"));
        template.Triggers.Add(pressTrigger);

        var btn = new Button
        {
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Template = template,
            ToolTip = tooltip,
            Content = content,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
