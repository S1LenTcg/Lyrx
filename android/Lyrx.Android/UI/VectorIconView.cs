using Android.Content;
using Android.Graphics;
using Android.Views;
using Path = global::Android.Graphics.Path;

namespace Musebase.Android.UI;

internal enum ClassicIconKind
{
    Previous,
    Play,
    Pause,
    Next,
    Translate,
    Settings,
    Sync,
    Clock,
    Close,
    ChevronDown,
}

/// <summary>
/// 复刻桌面端 <c>ClassicTransportGlyph</c> 的 24×24 矢量图标视图，保证双端图标笔触与观感统一。
/// </summary>
internal sealed class VectorIconView : View
{
    private readonly Paint _fillPaint = new(PaintFlags.AntiAlias) { Color = Color.White };
    private readonly Paint _strokePaint = new(PaintFlags.AntiAlias)
    {
        Color = Color.White,
        StrokeCap = Paint.Cap.Round,
        StrokeJoin = Paint.Join.Round,
    };
    private readonly Path _path = new();

    private ClassicIconKind _kind;
    private Color _tintColor = LyricsPalette.MutedGray;
    private bool _active;
    private float _iconFraction = 0.62f;

    public VectorIconView(Context context, ClassicIconKind kind) : base(context)
    {
        _kind = kind;
        _fillPaint.SetStyle(Paint.Style.Fill);
        _strokePaint.SetStyle(Paint.Style.Stroke);
    }

    public void Configure(ClassicIconKind kind, Color tint, bool active = false, float iconFraction = 0.62f)
    {
        var changed = _kind != kind || _tintColor != tint || _active != active || Math.Abs(_iconFraction - iconFraction) > 0.001f;
        _kind = kind;
        _tintColor = tint;
        _active = active;
        _iconFraction = iconFraction;
        if (changed) Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var size = Math.Min(Width, Height) * _iconFraction;
        if (size <= 0) return;

        var scale = size / 24f;
        var dx = (Width - size) / 2f;
        var dy = (Height - size) / 2f;

        canvas.Save();
        canvas.Translate(dx, dy);
        canvas.Scale(scale, scale);

        var ink = _active && _kind != ClassicIconKind.Translate
            ? LyricsPalette.AccentGreen
            : _tintColor;
        _fillPaint.Color = ink;
        _strokePaint.Color = ink;
        _strokePaint.StrokeWidth = _kind == ClassicIconKind.Translate ? 1.85f : 2.0f;

        switch (_kind)
        {
            case ClassicIconKind.Play:
                _path.Reset();
                _path.MoveTo(6.5f, 3.5f);
                _path.LineTo(19.5f, 12f);
                _path.LineTo(6.5f, 20.5f);
                _path.Close();
                canvas.DrawPath(_path, _fillPaint);
                break;

            case ClassicIconKind.Pause:
                canvas.DrawRect(6f, 4f, 10f, 20f, _fillPaint);
                canvas.DrawRect(14f, 4f, 18f, 20f, _fillPaint);
                break;

            case ClassicIconKind.Previous:
                canvas.DrawRect(5f, 5f, 7f, 19f, _fillPaint);
                _path.Reset();
                _path.MoveTo(19f, 5f);
                _path.LineTo(8f, 12f);
                _path.LineTo(19f, 19f);
                _path.Close();
                canvas.DrawPath(_path, _fillPaint);
                break;

            case ClassicIconKind.Next:
                canvas.DrawRect(17f, 5f, 19f, 19f, _fillPaint);
                _path.Reset();
                _path.MoveTo(5f, 5f);
                _path.LineTo(16f, 12f);
                _path.LineTo(5f, 19f);
                _path.Close();
                canvas.DrawPath(_path, _fillPaint);
                break;

            case ClassicIconKind.Sync:
                canvas.DrawLine(2f, 9f, 2f, 15f, _strokePaint);
                canvas.DrawLine(7f, 5f, 7f, 19f, _strokePaint);
                canvas.DrawLine(12f, 2f, 12f, 22f, _strokePaint);
                canvas.DrawLine(17f, 5f, 17f, 19f, _strokePaint);
                canvas.DrawLine(22f, 9f, 22f, 15f, _strokePaint);
                break;

            case ClassicIconKind.Translate:
                if (_active)
                {
                    _fillPaint.Color = LyricsPalette.AccentGreen;
                    canvas.DrawRoundRect(new RectF(2.2f, 2.2f, 21.8f, 21.8f), 3.4f, 3.4f, _fillPaint);
                    _strokePaint.Color = LyricsPalette.ChromeBackground;
                }
                else
                {
                    _strokePaint.Color = _tintColor;
                }
                canvas.DrawLine(9.2f, 4.5f, 9.2f, 6.8f, _strokePaint);
                canvas.DrawLine(4.8f, 6.8f, 13.6f, 6.8f, _strokePaint);
                _path.Reset();
                _path.MoveTo(11.8f, 6.8f);
                _path.CubicTo(11f, 10.1f, 8.8f, 12.4f, 5f, 14f);
                _path.MoveTo(6.8f, 8.2f);
                _path.CubicTo(8.3f, 10.7f, 10.4f, 12.6f, 13.4f, 14f);
                _path.MoveTo(12.4f, 19.2f);
                _path.LineTo(15.7f, 10.8f);
                _path.LineTo(19f, 19.2f);
                _path.MoveTo(13.4f, 16.6f);
                _path.LineTo(18f, 16.6f);
                canvas.DrawPath(_path, _strokePaint);
                break;

            case ClassicIconKind.Settings:
                canvas.DrawLine(4.5f, 8f, 10.5f, 8f, _strokePaint);
                canvas.DrawLine(15.5f, 8f, 19.5f, 8f, _strokePaint);
                canvas.DrawCircle(13f, 8f, 2.2f, _strokePaint);
                canvas.DrawLine(4.5f, 16f, 8.5f, 16f, _strokePaint);
                canvas.DrawLine(13.5f, 16f, 19.5f, 16f, _strokePaint);
                canvas.DrawCircle(11f, 16f, 2.2f, _strokePaint);
                break;

            case ClassicIconKind.Clock:
                _strokePaint.StrokeWidth = 1.9f;
                canvas.DrawCircle(12f, 12f, 8.2f, _strokePaint);
                _path.Reset();
                _path.MoveTo(12f, 7.6f);
                _path.LineTo(12f, 12.3f);
                _path.LineTo(15.8f, 14.5f);
                canvas.DrawPath(_path, _strokePaint);
                break;

            case ClassicIconKind.Close:
                canvas.DrawLine(6.5f, 6.5f, 17.5f, 17.5f, _strokePaint);
                canvas.DrawLine(17.5f, 6.5f, 6.5f, 17.5f, _strokePaint);
                break;

            case ClassicIconKind.ChevronDown:
                _path.Reset();
                _path.MoveTo(5.5f, 8.8f);
                _path.LineTo(12f, 15.3f);
                _path.LineTo(18.5f, 8.8f);
                canvas.DrawPath(_path, _strokePaint);
                break;
        }

        canvas.Restore();
    }
}
