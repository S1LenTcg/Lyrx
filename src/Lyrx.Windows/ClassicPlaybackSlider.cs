using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Controls;

namespace Musebase.Windows;

/// <summary>
/// 播放进度条——<b>只读显示</b>：把当前播放位置画成一条轨道 + 已播放段，
/// 不响应鼠标拖拽、点击、滚轮与键盘，也不对外发出任何跳转请求。
/// 数据由 <see cref="Musebase.Windows.ClassicLyricsWindow"/> 每帧写入 Value/Maximum。
/// </summary>
internal sealed class ClassicPlaybackSlider : Slider
{
    internal static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(30, 215, 96));
    private static readonly Brush Rail = new SolidColorBrush(Color.FromRgb(77, 77, 77));

    /// <summary>轨道左右留白，已播放段的端点用它做内缩。</summary>
    private const double RailInset = 6;

    public ClassicPlaybackSlider()
    {
        Template = new ControlTemplate(typeof(Slider));
        Height = 20;
        Focusable = false;
        IsTabStop = false;
        IsHitTestVisible = false;   // 纯粹的指示器：鼠标事件直接穿过去
        VerticalAlignment = VerticalAlignment.Center;
        IsDirectionReversed = false;
        Maximum = 1;
        AutomationProperties.SetName(this, "播放进度");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PlaybackProgressPeer(this);

    /// <summary>对辅助功能暴露为只读的 RangeValue：读得到播放进度，改不了。</summary>
    private sealed class PlaybackProgressPeer(ClassicPlaybackSlider slider)
        : SliderAutomationPeer(slider), IRangeValueProvider
    {
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        bool IRangeValueProvider.IsReadOnly => true;
        double IRangeValueProvider.LargeChange => slider.LargeChange;
        double IRangeValueProvider.Maximum => slider.Maximum;
        double IRangeValueProvider.Minimum => slider.Minimum;
        double IRangeValueProvider.SmallChange => slider.SmallChange;
        double IRangeValueProvider.Value => slider.Value;

        void IRangeValueProvider.SetValue(double value) => throw new ElementNotEnabledException();
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

        drawing.DrawRoundedRectangle(Rail, null, new Rect(RailInset, y - 2, width, 4), 2, 2);
        if (end > RailInset)
            drawing.DrawRoundedRectangle(Accent, null, new Rect(RailInset, y - 2, end - RailInset, 4), 2, 2);
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
    }
}
