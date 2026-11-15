using Android.Content;
using Android.Graphics;
using Android.Views;

namespace Musebase.Android.UI;

/// <summary>
/// 复刻桌面端 <c>ClassicPlaybackSlider</c> 的 Spotify 风格进度条：
/// 细暗灰轨道 + 白色（拖拽时高亮为 Spotify 绿）进度填充 + 手指拖拽实时预览并回调跳转。
/// </summary>
internal sealed class ClassicSeekBar : View
{
    private static readonly Color RailColor = Color.Rgb(77, 77, 77);
    private static readonly Color ActiveWhite = Color.White;

    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private double _maximum = 1.0;
    private double _value;
    private bool _dragging;

    public bool IsDragging => _dragging;

    public event Action<double>? SeekRequested;
    public event Action<double>? PreviewChanged;

    public ClassicSeekBar(Context context) : base(context)
    {
        Clickable = true;
    }

    public void SetProgress(double value, double maximum, bool enabled)
    {
        Enabled = enabled;
        _maximum = Math.Max(0.0, maximum);
        if (!_dragging)
        {
            var clamped = _maximum > 0 ? Math.Clamp(value, 0, _maximum) : 0;
            if (Math.Abs(_value - clamped) > 0.05 || !enabled)
            {
                _value = clamped;
                Invalidate();
            }
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null || !Enabled || _maximum <= 0) return false;

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                Parent?.RequestDisallowInterceptTouchEvent(true);
                _dragging = true;
                UpdateFromX(e.GetX());
                return true;

            case MotionEventActions.Move:
                if (_dragging)
                {
                    UpdateFromX(e.GetX());
                    return true;
                }
                break;

            case MotionEventActions.Up:
                if (_dragging)
                {
                    UpdateFromX(e.GetX());
                    _dragging = false;
                    Parent?.RequestDisallowInterceptTouchEvent(false);
                    Invalidate();
                    SeekRequested?.Invoke(_value);
                    return true;
                }
                break;

            case MotionEventActions.Cancel:
                if (_dragging)
                {
                    _dragging = false;
                    Parent?.RequestDisallowInterceptTouchEvent(false);
                    Invalidate();
                    return true;
                }
                break;
        }
        return base.OnTouchEvent(e);
    }

    private void UpdateFromX(float x)
    {
        var inset = Dp(7);
        var trackWidth = Math.Max(1f, Width - inset * 2f);
        var ratio = Math.Clamp((x - inset) / trackWidth, 0f, 1f);
        _value = ratio * _maximum;
        PreviewChanged?.Invoke(_value);
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (Width <= 0 || Height <= 0) return;

        var inset = Dp(7);
        var trackWidth = Math.Max(1f, Width - inset * 2f);
        var cy = Height / 2f;
        var halfTrack = _dragging ? Dp(2.2f) : Dp(1.6f);
        var ratio = Enabled && _maximum > 0 ? (float)Math.Clamp(_value / _maximum, 0, 1) : 0f;
        var fillEnd = inset + trackWidth * ratio;

        _paint.Color = RailColor;
        canvas.DrawRoundRect(new RectF(inset, cy - halfTrack, Width - inset, cy + halfTrack), halfTrack, halfTrack, _paint);

        if (Enabled && ratio > 0)
        {
            _paint.Color = _dragging ? LyricsPalette.AccentGreen : ActiveWhite;
            canvas.DrawRoundRect(new RectF(inset, cy - halfTrack, fillEnd, cy + halfTrack), halfTrack, halfTrack, _paint);
        }

        if (Enabled)
        {
            _paint.Color = ActiveWhite;
            var thumbRadius = _dragging ? Dp(6.5f) : Dp(4.8f);
            canvas.DrawCircle(fillEnd, cy, thumbRadius, _paint);
        }
    }

    private float Dp(float dp) => dp * (Resources?.DisplayMetrics?.Density ?? 2f);
}
