using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;

namespace Musebase.Windows;

internal sealed class ClassicPlaybackSlider : Slider
{
    internal static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(30, 215, 96));
    private static readonly Brush Rail = new SolidColorBrush(Color.FromRgb(77, 77, 77));

    public bool IsScrubbing { get; private set; }
    public event Action? ScrubStarted;
    public event Action<double>? SeekRequested;
    public event Action? ScrubCancelled;

    public ClassicPlaybackSlider()
    {
        Template = new ControlTemplate(typeof(Slider));
        Height = 20;
        Focusable = true;
        VerticalAlignment = VerticalAlignment.Center;
        IsDirectionReversed = false;
        SmallChange = 5;
        LargeChange = 15;
        Maximum = 1;
        AutomationProperties.SetName(this, "播放进度");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PlaybackSliderPeer(this);

    private sealed class PlaybackSliderPeer(ClassicPlaybackSlider slider) : SliderAutomationPeer(slider), IRangeValueProvider
    {
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        bool IRangeValueProvider.IsReadOnly => !slider.IsEnabled;
        double IRangeValueProvider.LargeChange => slider.LargeChange;
        double IRangeValueProvider.Maximum => slider.Maximum;
        double IRangeValueProvider.Minimum => slider.Minimum;
        double IRangeValueProvider.SmallChange => slider.SmallChange;
        double IRangeValueProvider.Value => slider.Value;

        void IRangeValueProvider.SetValue(double value)
        {
            if (!slider.IsEnabled) throw new ElementNotEnabledException();
            if (!double.IsFinite(value) || value < slider.Minimum || value > slider.Maximum)
                throw new ArgumentOutOfRangeException(nameof(value));
            slider.SeekRequested?.Invoke(value);
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth <= 12) return;
        var width = ActualWidth - 12;
        var y = ActualHeight / 2;
        var end = 6 + width * Value / Maximum;
        var engaged = IsEnabled && (IsMouseOver || IsKeyboardFocused || IsScrubbing);
        drawing.DrawRoundedRectangle(Rail, null, new Rect(6, y - 2, width, 4), 2, 2);
        if (end > 6)
            drawing.DrawRoundedRectangle(engaged ? Accent : Brushes.White, null, new Rect(6, y - 2, end - 6, 4), 2, 2);
        if (engaged) drawing.DrawEllipse(Brushes.White, null, new Point(end, y), 6, 6);
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsEnabledProperty || e.Property == IsMouseOverProperty || e.Property == IsKeyboardFocusedProperty)
            InvalidateVisual();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled || !CaptureMouse()) return;
        Focus();
        IsScrubbing = true;
        ScrubStarted?.Invoke();
        PreviewPosition(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (IsScrubbing) PreviewPosition(e.GetPosition(this).X);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsScrubbing) return;
        PreviewPosition(e.GetPosition(this).X);
        IsScrubbing = false;
        ReleaseMouseCapture();
        InvalidateVisual();
        SeekRequested?.Invoke(Value);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CancelScrub();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsScrubbing)
        {
            CancelScrub();
            e.Handled = true;
            return;
        }
        if (IsScrubbing || !IsEnabled) return;

        var seconds = e.Key switch
        {
            Key.Left or Key.Down => Value - SmallChange,
            Key.Right or Key.Up => Value + SmallChange,
            Key.PageDown => Value - LargeChange,
            Key.PageUp => Value + LargeChange,
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => double.NaN,
        };
        if (double.IsNaN(seconds)) return;
        SeekRequested?.Invoke(Math.Clamp(seconds, Minimum, Maximum));
        e.Handled = true;
    }

    public void CancelScrub()
    {
        if (!IsScrubbing) return;
        IsScrubbing = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        ScrubCancelled?.Invoke();
    }

    private void PreviewPosition(double x)
    {
        Value = Math.Clamp((x - 6) / Math.Max(1, ActualWidth - 12), 0, 1) * Maximum;
    }
}
