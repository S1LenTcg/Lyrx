using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Musebase.Windows;

/// <summary>
/// 播放进度条：支持显示歌曲实际进度、鼠标悬停高亮、拖拽/点击与键盘方向键跳转播放，以及 UIA 辅助功能。
/// 数据由 <see cref="Musebase.Windows.ClassicLyricsWindow"/> 每帧写入 Value/Maximum。
/// </summary>
internal sealed class ClassicPlaybackSlider : Slider
{
    private static readonly DependencyProperty HoverProgressProperty = DependencyProperty.Register(
        nameof(HoverProgress), typeof(double), typeof(ClassicPlaybackSlider),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty PressProgressProperty = DependencyProperty.Register(
        nameof(PressProgress), typeof(double), typeof(ClassicPlaybackSlider),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(ClassicPlaybackSlider),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    internal static readonly Brush Accent = FrozenBrush(30, 215, 96);
    private static readonly Brush Rail = FrozenBrush(77, 77, 77);
    private static readonly Brush FocusedRail = FrozenBrush(106, 106, 106);
    private static readonly Brush ThumbBrush = FrozenBrush(255, 255, 255);
    private static readonly Brush FocusThumbHalo = FrozenBrush(100, 30, 215, 96);
    private static readonly Pen FocusThumbPen = FrozenPen(255, 30, 215, 96, 1.5);
    private static readonly IEasingFunction SliderEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>轨道左右留白，已播放段的端点用它做内缩。</summary>
    private const double RailInset = 8;
    private const double SmallStepSeconds = 5;
    private const double LargeStepSeconds = 15;
    private static readonly TimeSpan KeyboardSeekDebounce = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _keyboardSeekTimer;
    private bool _lastAutomationReadOnly;
    private double _dragStartX;
    private bool _hasMovedDuringDrag;
    private bool _suppressImmediateDisplaySync;
    private double _displayAnimationTarget = double.NaN;

    /// <summary>用户完成点击、拖动或键盘跳转后请求跳转到指定秒数。</summary>
    public event Action<double>? SeekRequested;

    /// <summary>用户当前是否正在拖拽进度条。</summary>
    public bool IsDragging { get; private set; }

    /// <summary>用户当前是否正在通过键盘连续调节进度（防抖窗口尚未提交）。</summary>
    public bool IsKeyboardSeeking { get; private set; }

    /// <summary>用户是否正在交互调节进度（拖拽或键盘连续步进），用于阻止定时刷新覆盖当前预览值。</summary>
    public bool IsUserSeeking => IsDragging || IsKeyboardSeeking;

    private double HoverProgress
    {
        get => (double)GetValue(HoverProgressProperty);
        set => SetValue(HoverProgressProperty, value);
    }

    private double PressProgress
    {
        get => (double)GetValue(PressProgressProperty);
        set => SetValue(PressProgressProperty, value);
    }

    private double DisplayValue
    {
        get => (double)GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    private bool IsAutomationReadOnly => !IsEnabled || Maximum <= Minimum;

    public ClassicPlaybackSlider()
    {
        Template = new ControlTemplate(typeof(Slider));
        Height = 20;
        Focusable = true;
        IsTabStop = true;
        FocusVisualStyle = null;
        IsHitTestVisible = true;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        IsDirectionReversed = false;
        Minimum = 0;
        Maximum = 1;
        SmallChange = SmallStepSeconds;
        LargeChange = LargeStepSeconds;
        AutomationProperties.SetName(this, "播放进度");

        _keyboardSeekTimer = new DispatcherTimer { Interval = KeyboardSeekDebounce };
        _keyboardSeekTimer.Tick += (_, _) => CommitKeyboardSeek();
        IsEnabledChanged += OnSliderIsEnabledChanged;
        _lastAutomationReadOnly = IsAutomationReadOnly;
    }

    /// <summary>以平滑过渡动画更新进度条位置（用于点击歌词等显式跳转）。</summary>
    public void AnimateToValue(double targetSeconds)
    {
        var clamped = Math.Clamp(targetSeconds, Minimum, Maximum);
        _suppressImmediateDisplaySync = true;
        try
        {
            Value = clamped;
        }
        finally
        {
            _suppressImmediateDisplaySync = false;
        }
        AnimateDisplayValueTo(clamped, 220);
    }

    /// <summary>取消尚未提交的键盘防抖跳转（例如切歌或窗口关闭时）。</summary>
    public void CancelPendingSeek()
    {
        _keyboardSeekTimer.Stop();
        if (IsKeyboardSeeking)
        {
            IsKeyboardSeeking = false;
            UpdateVisualActivityAnimations();
            InvalidateVisual();
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateVisualActivityAnimations();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateVisualActivityAnimations();
        InvalidateVisual();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (UIElementAutomationPeer.FromElement(this) is PlaybackProgressPeer peer)
            peer.RaiseFocusChanged(true);
        UpdateVisualActivityAnimations();
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsKeyboardSeeking)
            CommitKeyboardSeek();
        if (UIElementAutomationPeer.FromElement(this) is PlaybackProgressPeer peer)
            peer.RaiseFocusChanged(false);
        UpdateVisualActivityAnimations();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled || Maximum <= Minimum) return;

        if (Focusable && !IsKeyboardFocused)
            Focus();
        CancelPendingSeek();
        var mouseX = e.GetPosition(this).X;
        IsDragging = true;
        _dragStartX = mouseX;
        _hasMovedDuringDrag = false;
        CaptureMouse();
        UpdateVisualActivityAnimations();
        UpdateValueFromMouse(mouseX, animateClick: true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsDragging && Maximum > Minimum)
        {
            var mouseX = e.GetPosition(this).X;
            if (!_hasMovedDuringDrag && Math.Abs(mouseX - _dragStartX) >= 2.5)
                _hasMovedDuringDrag = true;
            UpdateValueFromMouse(mouseX, animateClick: !_hasMovedDuringDrag);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsDragging)
        {
            IsDragging = false;
            ReleaseMouseCapture();
            UpdateVisualActivityAnimations();
            UpdateValueFromMouse(e.GetPosition(this).X, animateClick: !_hasMovedDuringDrag);
            if (IsEnabled && Maximum > Minimum)
                SeekRequested?.Invoke(Math.Clamp(Value, Minimum, Maximum));
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (IsDragging)
        {
            IsDragging = false;
            UpdateVisualActivityAnimations();
            if (IsEnabled && Maximum > Minimum)
                SeekRequested?.Invoke(Math.Clamp(Value, Minimum, Maximum));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && Keyboard.Modifiers == ModifierKeys.None && TryHandleNavigationKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // WPF Slider 的内置 KeyGesture 类绑定会在 OnKeyDown 前先触发下列虚方法，因此统一委托到 StepBy/StepTo。
    protected override void OnDecreaseSmall() => StepBy(-SmallChange);
    protected override void OnIncreaseSmall() => StepBy(SmallChange);
    protected override void OnDecreaseLarge() => StepBy(-LargeChange);
    protected override void OnIncreaseLarge() => StepBy(LargeChange);
    protected override void OnMinimizeValue() => StepTo(Minimum);
    protected override void OnMaximizeValue() => StepTo(Maximum);

    protected override void OnMinimumChanged(double oldMinimum, double newMinimum)
    {
        base.OnMinimumChanged(oldMinimum, newMinimum);
        NotifyAutomationReadOnlyChanged();
        InvalidateVisual();
    }

    protected override void OnMaximumChanged(double oldMaximum, double newMaximum)
    {
        base.OnMaximumChanged(oldMaximum, newMaximum);
        NotifyAutomationReadOnlyChanged();
        InvalidateVisual();
    }

    private bool TryHandleNavigationKey(Key key)
    {
        switch (key)
        {
            case Key.Left:
            case Key.Down:
                StepBy(-SmallChange);
                return true;
            case Key.Right:
            case Key.Up:
                StepBy(SmallChange);
                return true;
            case Key.PageDown:
                StepBy(-LargeChange);
                return true;
            case Key.PageUp:
                StepBy(LargeChange);
                return true;
            case Key.Home:
                StepTo(Minimum);
                return true;
            case Key.End:
                StepTo(Maximum);
                return true;
            default:
                return false;
        }
    }

    private void StepBy(double delta) => StepTo(Value + delta);

    private void StepTo(double target)
    {
        if (!IsEnabled || IsDragging || Maximum <= Minimum) return;
        var clamped = Math.Clamp(target, Minimum, Maximum);
        if (Math.Abs(clamped - Value) < 1e-6 && !IsKeyboardSeeking) return;

        IsKeyboardSeeking = true;
        UpdateVisualActivityAnimations();
        _suppressImmediateDisplaySync = true;
        try { Value = clamped; }
        finally { _suppressImmediateDisplaySync = false; }
        AnimateDisplayValueTo(clamped, 140);
        _keyboardSeekTimer.Stop();
        _keyboardSeekTimer.Start();
        InvalidateVisual();
    }

    private void CommitKeyboardSeek()
    {
        _keyboardSeekTimer.Stop();
        if (!IsKeyboardSeeking) return;
        IsKeyboardSeeking = false;
        UpdateVisualActivityAnimations();
        InvalidateVisual();
        if (IsEnabled && Maximum > Minimum)
            SeekRequested?.Invoke(Math.Clamp(Value, Minimum, Maximum));
    }

    private void ApplyAutomationSeek(double clampedSeconds)
    {
        CancelPendingSeek();
        if (IsDragging)
        {
            IsDragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }
        UpdateVisualActivityAnimations();
        AnimateToValue(clampedSeconds);
        SeekRequested?.Invoke(clampedSeconds);
    }

    private void OnSliderIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Cursor = IsEnabled ? Cursors.Hand : Cursors.Arrow;
        if (!IsEnabled)
        {
            CancelPendingSeek();
            if (IsDragging)
            {
                IsDragging = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
            }
        }
        UpdateVisualActivityAnimations();
        NotifyAutomationReadOnlyChanged();
        InvalidateVisual();
    }

    private void NotifyAutomationReadOnlyChanged()
    {
        var current = IsAutomationReadOnly;
        if (current == _lastAutomationReadOnly) return;
        var previous = _lastAutomationReadOnly;
        _lastAutomationReadOnly = current;
        if (UIElementAutomationPeer.FromElement(this) is PlaybackProgressPeer peer)
            peer.RaiseReadOnlyChanged(previous, current);
    }

    private void UpdateValueFromMouse(double mouseX, bool animateClick)
    {
        var width = ActualWidth - RailInset * 2;
        if (width <= 1) return;
        var range = Maximum - Minimum;
        if (range <= 0) return;
        var ratio = Math.Clamp((mouseX - RailInset) / width, 0d, 1d);
        var next = Minimum + ratio * range;

        if (animateClick)
        {
            _suppressImmediateDisplaySync = true;
            try { Value = next; }
            finally { _suppressImmediateDisplaySync = false; }
            AnimateDisplayValueTo(next, 160);
        }
        else
        {
            Value = next;
        }
        InvalidateVisual();
    }

    private void AnimateDisplayValueTo(double target, int durationMs)
    {
        var clamped = Math.Clamp(target, Minimum, Maximum);
        if (!SystemParameters.ClientAreaAnimation || !IsLoaded)
        {
            _displayAnimationTarget = double.NaN;
            BeginAnimation(DisplayValueProperty, null);
            DisplayValue = clamped;
            return;
        }

        var current = Math.Clamp(DisplayValue, Minimum, Maximum);
        if (Math.Abs(current - clamped) < 0.001)
        {
            _displayAnimationTarget = double.NaN;
            BeginAnimation(DisplayValueProperty, null);
            DisplayValue = clamped;
            return;
        }

        _displayAnimationTarget = clamped;
        var anim = new DoubleAnimation(current, clamped, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = SliderEase,
        };
        anim.Completed += (_, _) => _displayAnimationTarget = double.NaN;
        BeginAnimation(DisplayValueProperty, anim);
    }

    private void UpdateVisualActivityAnimations()
    {
        var hoverTarget = IsEnabled && (IsMouseOver || IsDragging || IsKeyboardFocused || IsKeyboardSeeking) ? 1d : 0d;
        var pressTarget = IsEnabled && IsDragging ? 1d : 0d;

        if (!SystemParameters.ClientAreaAnimation || !IsLoaded)
        {
            BeginAnimation(HoverProgressProperty, null);
            HoverProgress = hoverTarget;
            BeginAnimation(PressProgressProperty, null);
            PressProgress = pressTarget;
            return;
        }

        BeginAnimation(HoverProgressProperty, new DoubleAnimation(hoverTarget, TimeSpan.FromMilliseconds(140)) { EasingFunction = SliderEase });
        BeginAnimation(PressProgressProperty, new DoubleAnimation(pressTarget, TimeSpan.FromMilliseconds(110)) { EasingFunction = SliderEase });
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PlaybackProgressPeer(this);

    private sealed class PlaybackProgressPeer(ClassicPlaybackSlider slider)
        : SliderAutomationPeer(slider), IRangeValueProvider
    {
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        bool IRangeValueProvider.IsReadOnly => slider.IsAutomationReadOnly;
        double IRangeValueProvider.LargeChange => slider.LargeChange;
        double IRangeValueProvider.Maximum => slider.Maximum;
        double IRangeValueProvider.Minimum => slider.Minimum;
        double IRangeValueProvider.SmallChange => slider.SmallChange;
        double IRangeValueProvider.Value => slider.Value;

        void IRangeValueProvider.SetValue(double value)
        {
            if (!slider.IsEnabled)
                throw new ElementNotEnabledException();
            if (slider.Maximum <= slider.Minimum)
                throw new InvalidOperationException("Progress slider range is empty.");
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value));

            var clamped = Math.Clamp(value, slider.Minimum, slider.Maximum);
            slider.ApplyAutomationSeek(clamped);
        }

        internal void RaiseReadOnlyChanged(bool oldValue, bool newValue)
        {
            if (oldValue != newValue && ListenerExists(AutomationEvents.PropertyChanged))
                RaisePropertyChangedEvent(RangeValuePatternIdentifiers.IsReadOnlyProperty, oldValue, newValue);
        }

        internal void RaiseFocusChanged(bool hasFocus)
        {
            if (ListenerExists(AutomationEvents.PropertyChanged))
                RaisePropertyChangedEvent(AutomationElementIdentifiers.HasKeyboardFocusProperty, !hasFocus, hasFocus);
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var width = ActualWidth - RailInset * 2;
        if (width <= 1) return;

        var y = ActualHeight / 2;
        var range = Maximum - Minimum;
        var shownValue = DisplayValue;
        var ratio = range > 0 ? Math.Clamp((shownValue - Minimum) / range, 0d, 1d) : 0d;
        var end = RailInset + width * ratio;
        var isFocused = IsKeyboardFocused;
        var hoverT = IsEnabled ? Math.Clamp(HoverProgress, 0d, 1d) : 0d;
        var pressT = IsEnabled ? Math.Clamp(PressProgress, 0d, 1d) : 0d;
        var trackHeight = 4.0 + hoverT * 1.2 + pressT * 0.4;

        if (!IsEnabled) drawing.PushOpacity(0.45);
        try
        {
            drawing.DrawRoundedRectangle(isFocused ? FocusedRail : Rail, null, new Rect(RailInset, y - trackHeight / 2.0, width, trackHeight), trackHeight / 2.0, trackHeight / 2.0);
            if (end > RailInset)
                drawing.DrawRoundedRectangle(Accent, null, new Rect(RailInset, y - trackHeight / 2.0, end - RailInset, trackHeight), trackHeight / 2.0, trackHeight / 2.0);

            // 鼠标悬停、拖拽或键盘聚焦时，滑块平滑放大出现
            var baseThumbRadius = isFocused ? 5.0 : 5.5;
            var thumbRadius = (baseThumbRadius + pressT * 1.3) * hoverT;
            if (thumbRadius > 0.2 && range > 0)
            {
                if (isFocused || pressT > 0.05)
                {
                    var haloRadius = thumbRadius + 2.5;
                    if (isFocused)
                        drawing.DrawEllipse(FocusThumbHalo, null, new Point(end, y), haloRadius, haloRadius);
                    else
                        drawing.DrawEllipse(FrozenBrush((byte)Math.Round(72 * pressT), 255, 255, 255), null, new Point(end, y), haloRadius, haloRadius);
                }
                drawing.DrawEllipse(
                    ThumbBrush,
                    isFocused ? FocusThumbPen : null,
                    new Point(end, y),
                    thumbRadius,
                    thumbRadius);
            }
        }
        finally
        {
            if (!IsEnabled) drawing.Pop();
        }
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        if (!_suppressImmediateDisplaySync)
        {
            if (double.IsNaN(_displayAnimationTarget) || Math.Abs(newValue - _displayAnimationTarget) > 0.05)
            {
                _displayAnimationTarget = double.NaN;
                BeginAnimation(DisplayValueProperty, null);
                DisplayValue = newValue;
            }
        }
        InvalidateVisual();
    }

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b) => FrozenBrush(255, r, g, b);

    private static SolidColorBrush FrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(byte a, byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(FrozenBrush(a, r, g, b), thickness);
        pen.Freeze();
        return pen;
    }
}
