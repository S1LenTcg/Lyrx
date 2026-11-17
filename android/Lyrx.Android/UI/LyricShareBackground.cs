using Android.Graphics;

namespace Musebase.Android.UI;

internal enum ShareBackgroundMode
{
    Gradient,
    Solid,
    AlbumCover,
    CustomImage,
}

internal enum ShareCoverEffect
{
    Clean,
    SoftBlur,
    DreamyBlur,
    ColorFade,
    ColorTint,
    Cinematic,
}

internal readonly record struct ShareCoverEffectSettings(
    ShareCoverEffect Preset, int Blur, int Dim, int Fade, int Tint)
{
    public static ShareCoverEffectSettings FromPreset(ShareCoverEffect preset) => preset switch
    {
        ShareCoverEffect.Clean => new(preset, 0, 30, 26, 0),
        ShareCoverEffect.DreamyBlur => new(preset, 82, 24, 25, 22),
        ShareCoverEffect.ColorFade => new(preset, 14, 22, 86, 28),
        ShareCoverEffect.ColorTint => new(preset, 22, 34, 42, 72),
        ShareCoverEffect.Cinematic => new(preset, 12, 56, 84, 14),
        _ => new(ShareCoverEffect.SoftBlur, 35, 32, 36, 8),
    };
}

internal enum ShareLyricsFont
{
    Automatic,
    SansSerif,
    Serif,
    Monospace,
    Spotify,
}

internal static class LyricShareBackground
{
    public static Color Draw(Canvas canvas, RectF bounds, ShareCardOptions options, Bitmap? image)
    {
        var baseColor = options.BaseColor;
        if (image is null)
        {
            if (options.BackgroundMode == ShareBackgroundMode.Solid)
            {
                canvas.DrawColor(baseColor);
                return baseColor;
            }

            var top = LyricsPalette.BlendColor(baseColor, Color.White, 0.12);
            var end = options.GradientEndColor;
            DrawGradient(canvas, bounds, [top, baseColor, end], [0f, 0.48f, 1f]);
            return end;
        }

        canvas.DrawColor(baseColor);
        var effect = options.CoverEffect;
        if (effect.Blur == 0)
        {
            DrawCenterCrop(canvas, image, bounds);
        }
        else
        {
            using var blurred = BlurCrop(image, bounds, effect.Blur);
            if (effect.Blur < 12)
                DrawCenterCrop(canvas, image, bounds);
            DrawCenterCrop(canvas, blurred, bounds, Math.Min(255, effect.Blur * 255 / 12));
        }

        Fill(canvas, bounds, WithAlpha(baseColor, effect.Tint * 175 / 100));
        Fill(canvas, bounds, Color.Argb(effect.Dim * 255 / 100, 0, 0, 0));

        var deep = LyricsPalette.BlendColor(baseColor, Color.Black, 0.55);
        if (effect.Fade > 0)
        {
            DrawGradient(canvas, bounds,
                [Color.Argb(effect.Fade * 64 / 100, 0, 0, 0), WithAlpha(deep, effect.Fade * 36 / 100), WithAlpha(deep, effect.Fade * 234 / 100)],
                [0f, 0.36f, 1f]);
        }
        return deep;
    }

    public static void DrawGradient(Canvas canvas, RectF bounds, Color[] colors, float[] positions)
    {
        using var paint = new Paint(PaintFlags.AntiAlias);
        using var gradient = new LinearGradient(bounds.Left, bounds.Top, bounds.Left, bounds.Bottom,
            Array.ConvertAll(colors, color => color.ToArgb()), positions, Shader.TileMode.Clamp!);
        paint.SetShader(gradient);
        canvas.DrawRect(bounds, paint);
    }

    public static void DrawCenterCrop(Canvas canvas, Bitmap source, RectF bounds, int alpha = 255)
    {
        var scale = MathF.Max(bounds.Width() / source.Width, bounds.Height() / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        using var target = new RectF(bounds.CenterX() - width / 2, bounds.CenterY() - height / 2,
            bounds.CenterX() + width / 2, bounds.CenterY() + height / 2);
        using var paint = new Paint(PaintFlags.AntiAlias | PaintFlags.FilterBitmap) { Alpha = alpha };
        canvas.Save();
        canvas.ClipRect(bounds);
        canvas.DrawBitmap(source, null, target, paint);
        canvas.Restore();
    }

    private static void Fill(Canvas canvas, RectF bounds, Color color)
    {
        using var paint = new Paint { Color = color };
        canvas.DrawRect(bounds, paint);
    }

    private static Color WithAlpha(Color color, int alpha) => Color.Argb(alpha, color.R, color.G, color.B);

    private static Bitmap BlurCrop(Bitmap source, RectF bounds, int amount)
    {
        const float sampleSize = 160f;
        var scale = sampleSize / MathF.Max(bounds.Width(), bounds.Height());
        var w = Math.Max(1, (int)MathF.Round(bounds.Width() * scale));
        var h = Math.Max(1, (int)MathF.Round(bounds.Height() * scale));
        var bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        using var sampleBounds = new RectF(0, 0, w, h);
        DrawCenterCrop(canvas, source, sampleBounds);

        var pixels = new int[w * h];
        var scratch = new int[pixels.Length];
        bitmap.GetPixels(pixels, 0, w, 0, 0, w, h);
        var radius = Math.Max(1, (int)MathF.Round(amount * 0.13f));
        // 两轮可分离盒式模糊，低分辨率计算后再绘制到卡片。
        for (var pass = 0; pass < 2; pass++)
        {
            BlurPass(pixels, scratch, w, h, 1, w, radius);
            BlurPass(scratch, pixels, h, w, w, 1, radius);
        }
        bitmap.SetPixels(pixels, 0, w, 0, 0, w, h);
        return bitmap;
    }

    private static void BlurPass(int[] source, int[] destination, int length, int lines, int step, int stride, int radius)
    {
        var count = 2 * radius + 1;
        for (var line = 0; line < lines; line++)
        {
            var start = line * stride;
            var a = 0;
            var r = 0;
            var g = 0;
            var b = 0;
            for (var offset = -radius; offset <= radius; offset++)
            {
                var pixel = source[start + Math.Clamp(offset, 0, length - 1) * step];
                a += (int)((uint)pixel >> 24);
                r += (pixel >> 16) & 255;
                g += (pixel >> 8) & 255;
                b += pixel & 255;
            }

            for (var i = 0; i < length; i++)
            {
                destination[start + i * step] = (a / count << 24) | (r / count << 16) | (g / count << 8) | (b / count);
                var outgoing = source[start + Math.Max(0, i - radius) * step];
                var incoming = source[start + Math.Min(length - 1, i + radius + 1) * step];
                a += (int)((uint)incoming >> 24) - (int)((uint)outgoing >> 24);
                r += ((incoming >> 16) & 255) - ((outgoing >> 16) & 255);
                g += ((incoming >> 8) & 255) - ((outgoing >> 8) & 255);
                b += (incoming & 255) - (outgoing & 255);
            }
        }
    }
}
