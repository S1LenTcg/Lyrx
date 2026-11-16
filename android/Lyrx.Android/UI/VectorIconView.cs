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
    BluetoothAudio,
    LandscapePlayer,
    PhoneSpeaker,
    Headphones,
    Speaker,
    Check,
    Download,
    Share,
    ArrowLeft,
    Image,
    Bilingual,
}

/// <summary>
/// 复刻桌面端 <c>ClassicTransportGlyph</c> 的 24×24 矢量绘制核心，供控件与自绘浮窗共享。
/// </summary>
internal static class ClassicIconRenderer
{
    [ThreadStatic] private static Paint? _fillPaint;
    [ThreadStatic] private static Paint? _strokePaint;
    [ThreadStatic] private static Path? _path;

    public static void Draw(
        Canvas canvas,
        float width,
        float height,
        ClassicIconKind kind,
        Color tintColor,
        bool active = false,
        float iconFraction = 0.62f)
    {
        var size = Math.Min(width, height) * iconFraction;
        if (size <= 0) return;

        var scale = size / 24f;
        var dx = (width - size) / 2f;
        var dy = (height - size) / 2f;

        var fillPaint = _fillPaint ??= new Paint(PaintFlags.AntiAlias);
        fillPaint.SetStyle(Paint.Style.Fill);

        var strokePaint = _strokePaint ??= new Paint(PaintFlags.AntiAlias)
        {
            StrokeCap = Paint.Cap.Round,
            StrokeJoin = Paint.Join.Round,
        };
        strokePaint.SetStyle(Paint.Style.Stroke);

        var path = _path ??= new Path();
        path.Reset();

        canvas.Save();
        canvas.Translate(dx, dy);
        canvas.Scale(scale, scale);

        var ink = active && kind != ClassicIconKind.Translate
            ? LyricsPalette.AccentGreen
            : tintColor;
        fillPaint.Color = ink;
        strokePaint.Color = ink;
        strokePaint.StrokeWidth = kind == ClassicIconKind.Translate ? 1.85f : 2.0f;

        switch (kind)
        {
            case ClassicIconKind.Play:
                path.MoveTo(6.5f, 3.5f);
                path.LineTo(19.5f, 12f);
                path.LineTo(6.5f, 20.5f);
                path.Close();
                canvas.DrawPath(path, fillPaint);
                break;

            case ClassicIconKind.Pause:
                canvas.DrawRect(6f, 4f, 10f, 20f, fillPaint);
                canvas.DrawRect(14f, 4f, 18f, 20f, fillPaint);
                break;

            case ClassicIconKind.Previous:
                canvas.DrawRect(5f, 5f, 7f, 19f, fillPaint);
                path.MoveTo(19f, 5f);
                path.LineTo(8f, 12f);
                path.LineTo(19f, 19f);
                path.Close();
                canvas.DrawPath(path, fillPaint);
                break;

            case ClassicIconKind.Next:
                canvas.DrawRect(17f, 5f, 19f, 19f, fillPaint);
                path.MoveTo(5f, 5f);
                path.LineTo(16f, 12f);
                path.LineTo(5f, 19f);
                path.Close();
                canvas.DrawPath(path, fillPaint);
                break;

            case ClassicIconKind.Sync:
                canvas.DrawLine(2f, 9f, 2f, 15f, strokePaint);
                canvas.DrawLine(7f, 5f, 7f, 19f, strokePaint);
                canvas.DrawLine(12f, 2f, 12f, 22f, strokePaint);
                canvas.DrawLine(17f, 5f, 17f, 19f, strokePaint);
                canvas.DrawLine(22f, 9f, 22f, 15f, strokePaint);
                break;

            case ClassicIconKind.Translate:
                if (active)
                {
                    fillPaint.Color = LyricsPalette.AccentGreen;
                    canvas.DrawRoundRect(new RectF(2.2f, 2.2f, 21.8f, 21.8f), 3.4f, 3.4f, fillPaint);
                    strokePaint.Color = LyricsPalette.ChromeBackground;
                }
                else
                {
                    strokePaint.Color = tintColor;
                }
                canvas.DrawLine(9.2f, 4.5f, 9.2f, 6.8f, strokePaint);
                canvas.DrawLine(4.8f, 6.8f, 13.6f, 6.8f, strokePaint);
                path.MoveTo(11.8f, 6.8f);
                path.CubicTo(11f, 10.1f, 8.8f, 12.4f, 5f, 14f);
                path.MoveTo(6.8f, 8.2f);
                path.CubicTo(8.3f, 10.7f, 10.4f, 12.6f, 13.4f, 14f);
                path.MoveTo(12.4f, 19.2f);
                path.LineTo(15.7f, 10.8f);
                path.LineTo(19f, 19.2f);
                path.MoveTo(13.4f, 16.6f);
                path.LineTo(18f, 16.6f);
                canvas.DrawPath(path, strokePaint);
                break;

            case ClassicIconKind.Settings:
                canvas.DrawLine(4.5f, 8f, 10.5f, 8f, strokePaint);
                canvas.DrawLine(15.5f, 8f, 19.5f, 8f, strokePaint);
                canvas.DrawCircle(13f, 8f, 2.2f, strokePaint);
                canvas.DrawLine(4.5f, 16f, 8.5f, 16f, strokePaint);
                canvas.DrawLine(13.5f, 16f, 19.5f, 16f, strokePaint);
                canvas.DrawCircle(11f, 16f, 2.2f, strokePaint);
                break;

            case ClassicIconKind.Clock:
                strokePaint.StrokeWidth = 1.9f;
                canvas.DrawCircle(12f, 12f, 8.2f, strokePaint);
                path.MoveTo(12f, 7.6f);
                path.LineTo(12f, 12.3f);
                path.LineTo(15.8f, 14.5f);
                canvas.DrawPath(path, strokePaint);
                break;

            case ClassicIconKind.Close:
                canvas.DrawLine(6.5f, 6.5f, 17.5f, 17.5f, strokePaint);
                canvas.DrawLine(17.5f, 6.5f, 6.5f, 17.5f, strokePaint);
                break;

            case ClassicIconKind.ChevronDown:
                path.MoveTo(5.5f, 8.8f);
                path.LineTo(12f, 15.3f);
                path.LineTo(18.5f, 8.8f);
                canvas.DrawPath(path, strokePaint);
                break;

            case ClassicIconKind.BluetoothAudio:
                path.MoveTo(2.4f, 9.2f);
                path.LineTo(5.5f, 9.2f);
                path.LineTo(10.2f, 5.0f);
                path.LineTo(10.2f, 19.0f);
                path.LineTo(5.5f, 14.8f);
                path.LineTo(2.4f, 14.8f);
                path.Close();
                canvas.DrawPath(path, fillPaint);
                strokePaint.StrokeWidth = 1.1f;
                canvas.DrawPath(path, strokePaint);

                path.Reset();
                path.MoveTo(13.5f, 8.3f);
                path.LineTo(20.9f, 15.7f);
                path.LineTo(17.2f, 19.4f);
                path.LineTo(17.2f, 4.6f);
                path.LineTo(20.9f, 8.3f);
                path.LineTo(13.5f, 15.7f);
                strokePaint.StrokeWidth = 1.75f;
                canvas.DrawPath(path, strokePaint);
                break;

            case ClassicIconKind.LandscapePlayer:
                strokePaint.StrokeWidth = 1.85f;
                canvas.DrawRoundRect(new RectF(2.8f, 5.2f, 21.2f, 18.8f), 2.5f, 2.5f, strokePaint);
                canvas.DrawRoundRect(new RectF(5.5f, 8.9f, 10.9f, 14.3f), 1.1f, 1.1f, fillPaint);
                canvas.DrawLine(13.0f, 10.2f, 18.4f, 10.2f, strokePaint);
                canvas.DrawLine(13.0f, 13.8f, 16.9f, 13.8f, strokePaint);
                break;

            case ClassicIconKind.PhoneSpeaker:
                strokePaint.StrokeWidth = 1.8f;
                canvas.DrawRoundRect(new RectF(6.5f, 3.0f, 17.5f, 21.0f), 2.4f, 2.4f, strokePaint);
                canvas.DrawLine(10.4f, 18.0f, 13.6f, 18.0f, strokePaint);
                break;

            case ClassicIconKind.Headphones:
                strokePaint.StrokeWidth = 1.85f;
                path.AddArc(new RectF(4.6f, 4.4f, 19.4f, 19.2f), 180f, 180f);
                canvas.DrawPath(path, strokePaint);
                canvas.DrawRoundRect(new RectF(4.2f, 12.2f, 7.6f, 19.0f), 1.5f, 1.5f, fillPaint);
                canvas.DrawRoundRect(new RectF(16.4f, 12.2f, 19.8f, 19.0f), 1.5f, 1.5f, fillPaint);
                break;

            case ClassicIconKind.Speaker:
                path.MoveTo(3.2f, 9.3f);
                path.LineTo(6.8f, 9.3f);
                path.LineTo(11.6f, 5.1f);
                path.LineTo(11.6f, 18.9f);
                path.LineTo(6.8f, 14.7f);
                path.LineTo(3.2f, 14.7f);
                path.Close();
                canvas.DrawPath(path, fillPaint);
                strokePaint.StrokeWidth = 1.1f;
                canvas.DrawPath(path, strokePaint);

                strokePaint.StrokeWidth = 1.8f;
                path.Reset();
                path.AddArc(new RectF(12.2f, 8.2f, 16.6f, 15.8f), -52f, 104f);
                path.AddArc(new RectF(13.6f, 5.4f, 20.6f, 18.6f), -54f, 108f);
                canvas.DrawPath(path, strokePaint);
                break;

            case ClassicIconKind.Check:
                strokePaint.StrokeWidth = 2.4f;
                path.MoveTo(5.2f, 12.4f);
                path.LineTo(9.8f, 17.0f);
                path.LineTo(18.8f, 7.6f);
                canvas.DrawPath(path, strokePaint);
                break;

            case ClassicIconKind.Download:
                strokePaint.StrokeWidth = 2.0f;
                canvas.DrawLine(12.0f, 4.2f, 12.0f, 15.2f, strokePaint);
                path.MoveTo(7.4f, 11.2f);
                path.LineTo(12.0f, 15.8f);
                path.LineTo(16.6f, 11.2f);
                canvas.DrawPath(path, strokePaint);
                canvas.DrawLine(5.0f, 19.2f, 19.0f, 19.2f, strokePaint);
                break;

            case ClassicIconKind.Share:
                strokePaint.StrokeWidth = 1.9f;
                canvas.DrawCircle(17.2f, 5.6f, 2.5f, strokePaint);
                canvas.DrawCircle(6.4f, 12.0f, 2.5f, strokePaint);
                canvas.DrawCircle(17.2f, 18.4f, 2.5f, strokePaint);
                canvas.DrawLine(8.6f, 10.7f, 15.0f, 6.9f, strokePaint);
                canvas.DrawLine(8.6f, 13.3f, 15.0f, 17.1f, strokePaint);
                break;

            case ClassicIconKind.ArrowLeft:
                strokePaint.StrokeWidth = 2.1f;
                canvas.DrawLine(19.0f, 12.0f, 5.5f, 12.0f, strokePaint);
                path.MoveTo(11.6f, 5.8f);
                path.LineTo(5.4f, 12.0f);
                path.LineTo(11.6f, 18.2f);
                canvas.DrawPath(path, strokePaint);
                break;

            case ClassicIconKind.Image:
                strokePaint.StrokeWidth = 1.85f;
                canvas.DrawRoundRect(new RectF(3.5f, 4.2f, 20.5f, 19.8f), 2.4f, 2.4f, strokePaint);
                canvas.DrawCircle(8.4f, 9.2f, 1.35f, fillPaint);
                path.MoveTo(4.6f, 17.5f);
                path.LineTo(9.4f, 12.8f);
                path.LineTo(12.6f, 15.6f);
                path.LineTo(15.6f, 11.8f);
                path.LineTo(19.4f, 17.5f);
                path.Close();
                canvas.DrawPath(path, fillPaint);
                break;

            case ClassicIconKind.Bilingual:
                strokePaint.StrokeWidth = 1.95f;
                // 极简双语切换徽标：左上隐约叠层代表原语言，右下简洁框 + A 代表译文（无汉字、无多余箭头）
                path.MoveTo(4.0f, 13.8f);
                path.LineTo(4.0f, 6.6f);
                path.QuadTo(4.0f, 4.2f, 6.4f, 4.2f);
                path.LineTo(14.0f, 4.2f);
                canvas.DrawPath(path, strokePaint);

                canvas.DrawRoundRect(new RectF(7.8f, 7.8f, 20.0f, 20.0f), 2.8f, 2.8f, strokePaint);

                path.Reset();
                path.MoveTo(11.1f, 16.8f);
                path.LineTo(13.9f, 10.9f);
                path.LineTo(16.7f, 16.8f);
                path.MoveTo(11.9f, 15.1f);
                path.LineTo(15.9f, 15.1f);
                canvas.DrawPath(path, strokePaint);
                break;
        }

        canvas.Restore();
    }
}

/// <summary>
/// 复刻桌面端 <c>ClassicTransportGlyph</c> 的 24×24 矢量图标视图，保证双端图标笔触与观感统一。
/// </summary>
internal sealed class VectorIconView : View
{
    private ClassicIconKind _kind;
    private Color _tintColor = LyricsPalette.MutedGray;
    private bool _active;
    private float _iconFraction = 0.62f;

    public VectorIconView(Context context, ClassicIconKind kind) : base(context) => _kind = kind;

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
        ClassicIconRenderer.Draw(canvas, Width, Height, _kind, _tintColor, _active, _iconFraction);
    }
}
