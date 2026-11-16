using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Musebase.Windows;

internal readonly record struct ClassicThemePreset(
    string Id,
    string Label,
    Color Background,
    Color Subdued,
    bool IsDynamic = false);

internal static class ClassicLyricsPalette
{
    private const int HueBuckets = 18;
    private const int LightnessBuckets = 5;

    public static readonly IReadOnlyList<ClassicThemePreset> ThemePresets =
    [
        new("dynamic", "跟随专辑封面动态取色（默认）", Color.FromRgb(51, 51, 51), Color.FromRgb(179, 179, 179), IsDynamic: true),
        new("spotify-green", "深林暗绿（Spotify 经典绿）", Color.FromRgb(27, 77, 52), Color.FromRgb(154, 198, 175)),
        new("midnight", "极夜纯黑（OLED 深邃黑）", Color.FromRgb(22, 22, 22), Color.FromRgb(158, 158, 158)),
        new("ocean", "深海靛蓝（Midnight Blue）", Color.FromRgb(26, 58, 92), Color.FromRgb(148, 178, 210)),
        new("burgundy", "勃艮第酒红（Velvet Red）", Color.FromRgb(79, 29, 40), Color.FromRgb(204, 152, 164)),
        new("violet", "暮光暗紫（Twilight Purple）", Color.FromRgb(52, 34, 78), Color.FromRgb(173, 155, 199)),
        new("amber", "琥珀暖棕（Warm Espresso）", Color.FromRgb(68, 44, 26), Color.FromRgb(198, 172, 148)),
        new("warm-gray", "暖调岩石灰（Warm Charcoal）", Color.FromRgb(48, 48, 52), Color.FromRgb(176, 176, 182)),
    ];

    public static (Color Background, Color Subdued) Resolve(string? mode, (Color Background, Color Subdued) coverPalette)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            foreach (var preset in ThemePresets)
            {
                if (!preset.IsDynamic && string.Equals(preset.Id, mode, StringComparison.OrdinalIgnoreCase))
                    return (preset.Background, preset.Subdued);
            }
        }
        return coverPalette;
    }

    public static (Color Background, Color Subdued) FromCover(BitmapSource? cover)
    {
        var (darkVibrant, muted, vibrant, hasPixels) = AnalyzeCoverSwatches(cover);
        if (!hasPixels)
            return (Color.FromRgb(51, 51, 51), Color.FromRgb(179, 179, 179));

        if (darkVibrant is { } dark)
            return (FromHsl(dark.Hue, Math.Min(0.84, 0.23 + dark.Saturation * 0.66), 0.36 - dark.Saturation * 0.255),
                FromHsl(dark.Hue, Math.Clamp(0.94 - dark.Saturation * 0.49, 0.46, 0.70), 1.015 - dark.Saturation * 0.36));
        // On a dark canvas, a coherent soft tint reads closer to Encore than a muddy accent.
        if (muted is { } soft)
            return (FromHsl(soft.Hue, Math.Clamp(soft.Saturation * 1.8, 0.10, 0.28), 0.185),
                FromHsl(soft.Hue, Math.Clamp(soft.Saturation * 1.7, 0.10, 0.23), 0.79));
        if (vibrant is { } strong)
            return (FromHsl(strong.Hue, Math.Min(0.76, strong.Saturation * 1.3), 0.18),
                FromHsl(strong.Hue, Math.Clamp(strong.Saturation, 0.18, 0.62), 0.77));
        return (Color.FromRgb(51, 51, 51), Color.FromRgb(179, 179, 179));
    }

    /// <summary>
    /// 为歌词分享卡片提取更明媚、通透且保持白色文字可读性的封面主色。
    /// 优先采纳高饱和度与高明度主色，并通过 WCAG 相对亮度区间限制，避免昏暗发灰。
    /// </summary>
    public static Color ShareCardColorFromCover(BitmapSource? cover, Color? fallbackColor = null)
    {
        var (darkVibrant, muted, vibrant, hasPixels) = AnalyzeCoverSwatches(cover);
        if (hasPixels)
        {
            if (vibrant is { } strong)
                return BrightenForShareCard(strong.Hue, strong.Saturation, strong.Lightness);
            if (darkVibrant is { } dark)
                return BrightenForShareCard(dark.Hue, dark.Saturation, Math.Max(dark.Lightness, 0.34));
            if (muted is { } soft)
                return BrightenForShareCard(soft.Hue, soft.Saturation, soft.Lightness);
        }

        if (fallbackColor is { } fb)
        {
            var (hue, sat, light) = ToHsl(fb.R, fb.G, fb.B);
            if (sat >= 0.12)
                return BrightenForShareCard(hue, sat, Math.Max(light, 0.34));
        }

        return Color.FromRgb(74, 80, 92);
    }

    /// <summary>
    /// 在保留色相与鲜艳度的前提下将基色提亮到明媚区间（相对亮度 [0.06, 0.175]，确保白字对比度 ≥ 4.6:1）。
    /// </summary>
    public static Color LiftToShareBrightness(Color color, double minLuminance = 0.065, double maxLuminance = 0.175)
    {
        var (hue, sat, light) = ToHsl(color.R, color.G, color.B);
        var colorOut = color;
        var lum = RelativeLuminance(colorOut);

        if (lum < minLuminance)
        {
            var l = Math.Max(light, 0.24);
            while (l < 0.62 && RelativeLuminance(colorOut) < minLuminance)
            {
                l = Math.Min(0.62, l + 0.02);
                colorOut = FromHsl(hue, sat, l);
            }
            light = l;
        }

        while (light > 0.22 && RelativeLuminance(colorOut) > maxLuminance)
        {
            light = Math.Max(0.22, light - 0.015);
            colorOut = FromHsl(hue, sat, light);
        }

        return colorOut;
    }

    public static double RelativeLuminance(Color color)
    {
        static double Linearize(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);
    }

    public static Color HighContrastDarkInk(Color themeColor)
    {
        var (hue, sat, _) = ToHsl(themeColor.R, themeColor.G, themeColor.B);
        var targetSat = sat >= 0.12 ? Math.Clamp(sat * 0.85, 0.24, 0.72) : sat * 0.6;
        var ink = FromHsl(hue, targetSat, 0.14);
        var light = 0.14;
        while (light > 0.05 && RelativeLuminance(ink) > 0.035)
        {
            light -= 0.02;
            ink = FromHsl(hue, targetSat, light);
        }
        return ink;
    }

    private static Color BrightenForShareCard(double hue, double saturation, double coverLightness)
    {
        var targetSat = saturation >= 0.18
            ? Math.Clamp(Math.Max(saturation * 1.12, 0.44), 0.44, 0.84)
            : Math.Clamp(saturation * 1.35, 0.08, 0.30);
        var targetLight = Math.Clamp(coverLightness * 0.65 + 0.14, 0.34, 0.43);
        return LiftToShareBrightness(FromHsl(hue, targetSat, targetLight), minLuminance: 0.065, maxLuminance: 0.175);
    }

    private static (Swatch? DarkVibrant, Swatch? Muted, Swatch? Vibrant, bool HasPixels) AnalyzeCoverSwatches(BitmapSource? cover)
    {
        if (cover is null || cover.PixelWidth <= 0 || cover.PixelHeight <= 0)
            return (null, null, null, false);

        var scale = Math.Min(1, 64d / Math.Max(cover.PixelWidth, cover.PixelHeight));
        var sample = new TransformedBitmap(cover, new ScaleTransform(
            Math.Max(scale, 1d / cover.PixelWidth), Math.Max(scale, 1d / cover.PixelHeight)));
        var pixels = new byte[sample.PixelWidth * sample.PixelHeight * 4];
        new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0)
            .CopyPixels(pixels, sample.PixelWidth * 4, 0);

        var buckets = new (double Area, double R, double G, double B)[HueBuckets, LightnessBuckets];
        double visibleArea = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3] / 255d;
            visibleArea += alpha;
            var (hue, saturation, lightness) = ToHsl(pixels[i + 2], pixels[i + 1], pixels[i]);
            var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            if (chroma < 0.025 || lightness is < 0.08 or > 0.90) continue;

            var hueIndex = Math.Clamp((int)((hue + 10) % 360 / 20), 0, HueBuckets - 1);
            var lightnessIndex = Math.Clamp((int)(lightness * LightnessBuckets), 0, LightnessBuckets - 1);
            ref var bucket = ref buckets[hueIndex, lightnessIndex];
            bucket.Area += alpha;
            bucket.R += alpha * pixels[i + 2];
            bucket.G += alpha * pixels[i + 1];
            bucket.B += alpha * pixels[i];
        }

        if (visibleArea <= 0)
            return (null, null, null, false);

        Swatch? darkVibrant = null;
        Swatch? muted = null;
        Swatch? vibrant = null;
        foreach (var bucket in buckets)
        {
            if (bucket.Area == 0) continue;
            var share = bucket.Area / visibleArea;
            if (share < 0.015) continue;
            var (hue, saturation, lightness) = ToHsl(
                (byte)Math.Round(bucket.R / bucket.Area),
                (byte)Math.Round(bucket.G / bucket.Area),
                (byte)Math.Round(bucket.B / bucket.Area));
            var population = Math.Min(1, Math.Sqrt(share / 0.12));

            if (share >= 0.025 && lightness is >= 0.10 and <= 0.53 && saturation >= 0.28)
                KeepBest(ref darkVibrant, hue, saturation, lightness,
                    population * 0.8 + saturation * 2 + (1 - Math.Abs(0.32 - lightness) / 0.22) * 2);
            if (lightness is >= 0.50 and <= 0.86 && saturation <= 0.36)
                KeepBest(ref muted, hue, saturation, lightness,
                    population * 1.2 + (1 - Math.Abs(0.68 - lightness) / 0.22) * 2 + Math.Min(0.15, saturation) * 2);
            if (share >= 0.025 && lightness is >= 0.22 and <= 0.78 && saturation >= 0.18)
                KeepBest(ref vibrant, hue, saturation, lightness,
                    population * 1.1 + saturation * 1.6 + (1 - Math.Abs(0.50 - lightness) / 0.30));
        }

        return (darkVibrant, muted, vibrant, true);
    }

    private readonly record struct Swatch(double Hue, double Saturation, double Lightness, double Score);

    private static void KeepBest(ref Swatch? best, double hue, double saturation, double lightness, double score)
    {
        if (best is null || score > best.Value.Score) best = new Swatch(hue, saturation, lightness, score);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(byte r, byte g, byte b)
    {
        var red = r / 255d;
        var green = g / 255d;
        var blue = b / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var lightness = (max + min) / 2;
        var delta = max - min;
        if (delta == 0) return (0, 0, lightness);

        var hue = max == red ? (green - blue) / delta
            : max == green ? (blue - red) / delta + 2
            : (red - green) / delta + 4;
        return ((hue * 60 + 360) % 360, delta / (1 - Math.Abs(2 * lightness - 1)), lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var section = (hue + 360) % 360 / 60;
        var mid = chroma * (1 - Math.Abs(section % 2 - 1));
        var (r, g, b) = (int)section switch
        {
            0 => (chroma, mid, 0d),
            1 => (mid, chroma, 0d),
            2 => (0d, chroma, mid),
            3 => (0d, mid, chroma),
            4 => (mid, 0d, chroma),
            _ => (chroma, 0d, mid),
        };
        var offset = lightness - chroma / 2;
        return Color.FromRgb((byte)Math.Round((r + offset) * 255),
            (byte)Math.Round((g + offset) * 255), (byte)Math.Round((b + offset) * 255));
    }

    public static Color BlendColor(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    /// <summary>
    /// 基于当前主题色生成柔和、低刺眼感的莫奈（Monet）胶囊选中底色与高对比深色文字。
    /// </summary>
    public static (Color Surface, Color OnSurface) SoftMonetPill(Color themeColor, Color? fallbackHueColor = null)
    {
        var (hue, sat, _) = ToHsl(themeColor.R, themeColor.G, themeColor.B);
        double targetSat;

        if (sat < 0.08)
        {
            if (fallbackHueColor is { } fb)
            {
                var (fbHue, fbSat, _) = ToHsl(fb.R, fb.G, fb.B);
                if (fbSat >= 0.08)
                {
                    hue = fbHue;
                    targetSat = 0.16;
                }
                else
                {
                    hue = 212.0;
                    targetSat = 0.10;
                }
            }
            else
            {
                hue = 212.0;
                targetSat = 0.10;
            }
        }
        else
        {
            targetSat = Math.Clamp(sat * 0.58, 0.22, 0.32);
        }

        var surface = FromHsl(hue, targetSat, 0.795);
        var onSurface = FromHsl(hue, Math.Min(targetSat, 0.24), 0.11);
        return (surface, onSurface);
    }
}
