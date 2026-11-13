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

    public static (Color Background, Color Subdued) FromCover(BitmapSource cover)
    {
        var scale = Math.Min(1, 64d / Math.Max(cover.PixelWidth, cover.PixelHeight));
        var sample = new TransformedBitmap(cover, new ScaleTransform(
            Math.Max(scale, 1d / cover.PixelWidth), Math.Max(scale, 1d / cover.PixelHeight)));
        var pixels = new byte[sample.PixelWidth * sample.PixelHeight * 4];
        new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0)
            .CopyPixels(pixels, sample.PixelWidth * 4, 0);

        // Build swatches before selecting a dark-vibrant, muted or vibrant color,
        // like the cover-art fallback used by Spicetify's Dynamic Theme.
        var buckets = new (double Area, double R, double G, double B)[HueBuckets, LightnessBuckets];
        double visibleArea = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3] / 255d;
            visibleArea += alpha;
            var (hue, saturation, lightness) = ToHsl(pixels[i + 2], pixels[i + 1], pixels[i]);
            var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            if (chroma < 0.025 || lightness is < 0.08 or > 0.90) continue;

            var hueIndex = (int)((hue + 10) % 360 / 20);
            var lightnessIndex = (int)(lightness * LightnessBuckets);
            ref var bucket = ref buckets[hueIndex, lightnessIndex];
            bucket.Area += alpha;
            bucket.R += alpha * pixels[i + 2];
            bucket.G += alpha * pixels[i + 1];
            bucket.B += alpha * pixels[i];
        }

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

            if (share >= 0.035 && lightness is >= 0.17 and <= 0.53 && saturation >= 0.35)
                KeepBest(ref darkVibrant, hue, saturation,
                    population * 0.8 + saturation * 2 + (1 - Math.Abs(0.34 - lightness) / 0.20) * 2);
            if (lightness is >= 0.50 and <= 0.86 && saturation <= 0.36)
                KeepBest(ref muted, hue, saturation,
                    population * 1.2 + (1 - Math.Abs(0.68 - lightness) / 0.22) * 2 + Math.Min(0.15, saturation) * 2);
            if (share >= 0.035 && lightness is >= 0.24 and <= 0.68 && saturation >= 0.16)
                KeepBest(ref vibrant, hue, saturation,
                    population * 1.2 + saturation * 1.4 + 1 - Math.Abs(0.45 - lightness) / 0.25);
        }

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

    private readonly record struct Swatch(double Hue, double Saturation, double Score);

    private static void KeepBest(ref Swatch? best, double hue, double saturation, double score)
    {
        if (best is null || score > best.Value.Score) best = new Swatch(hue, saturation, score);
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
}
