using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;

namespace Musebase.Windows;

/// <summary>
/// 播放进度条：支持显示歌曲实际进度、鼠标悬停高亮、拖拽与点击跳转播放。
/// 数据由 <see cref="Musebase.Windows.ClassicLyricsWindow"/> 每帧写入 Value/Maximum。
/// </summary>
internal sealed class ClassicPlaybackSlider : Slider
{
    internal static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(30, 215, 96));
    private static readonly Brush Rail = new SolidColorBrush(Color.FromRgb(77, 77, 77));
    private static readonly Brush ThumbBrush = new SolidColorBrush(Colors.White);

    /// <summary>轨道左右留白，已播放段的端点用它做内缩。</summary>
    private const double RailInset = 6;

    /// <summary>用户完成点击或拖动后请求跳转到指定秒数。</summary>
    public event Action<double>? SeekRequested;

    /// <summary>用户当前是否正在拖拽进度条。</summary>
    public bool IsDragging { get; private set; }

    public ClassicPlaybackSlider()
    {
        Template = new ControlTemplate(typeof(Slider));
        Height = 20;
        Focusable = false;
        IsTabStop = false;
        IsHitTestVisible = true;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        IsDirectionReversed = false;
        Maximum = 1;
        AutomationProperties.SetName(this, "播放进度");
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Maximum <= 0) return;

        IsDragging = true;
        CaptureMouse();
        UpdateValueFromMouse(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsDragging && Maximum > 0)
        {
            UpdateValueFromMouse(e.GetPosition(this).X);
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
            UpdateValueFromMouse(e.GetPosition(this).X);
            SeekRequested?.Invoke(Value);
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (IsDragging)
        {
            IsDragging = false;
            SeekRequested?.Invoke(Value);
        }
    }

    private void UpdateValueFromMouse(double mouseX)
    {
        var width = ActualWidth - RailInset * 2;
        if (width <= 1) return;
        var ratio = Math.Clamp((mouseX - RailInset) / width, 0d, 1d);
        Value = ratio * Maximum;
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PlaybackProgressPeer(this);

    private sealed class PlaybackProgressPeer(ClassicPlaybackSlider slider)
        : SliderAutomationPeer(slider), IRangeValueProvider
    {
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        bool IRangeValueProvider.IsReadOnly => false;
        double IRangeValueProvider.LargeChange => slider.LargeChange;
        double IRangeValueProvider.Maximum => slider.Maximum;
        double IRangeValueProvider.Minimum => slider.Minimum;
        double IRangeValueProvider.SmallChange => slider.SmallChange;
        double IRangeValueProvider.Value => slider.Value;

        void IRangeValueProvider.SetValue(double value) => slider.SeekRequested?.Invoke(value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var width = ActualWidth - RailInset * 2;
        if (width <= 1) return;

        var y = ActualHeight / 2;
        var ratio = Maximum > 0 ? Math.Clamp(Value / Maximum, 0d, 1d) : 0d;
        var end = RailInset + width * ratio;
        var isHoveredOrDragging = IsMouseOver || IsDragging;
        var trackHeight = isHoveredOrDragging ? 5 : 4;

        drawing.DrawRoundedRectangle(Rail, null, new Rect(RailInset, y - trackHeight / 2.0, width, trackHeight), 2, 2);
        if (end > RailInset)
            drawing.DrawRoundedRectangle(Accent, null, new Rect(RailInset, y - trackHeight / 2.0, end - RailInset, trackHeight), 2, 2);

        // 鼠标悬停或拖拽时，在当前进度处绘制精致的白色小滑块
        if (isHoveredOrDragging && Maximum > 0)
        {
            drawing.DrawEllipse(ThumbBrush, null, new Point(end, y), 5.5, 5.5);
        }
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
    }
}
