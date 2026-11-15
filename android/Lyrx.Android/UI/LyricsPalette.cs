using Android.Graphics;

namespace Musebase.Android.UI;

internal readonly record struct ClassicThemePreset(
    string Id,
    string Label,
    Color Background,
    Color Subdued,
    bool IsDynamic = false);

/// <summary>
/// 与桌面端 <c>ClassicLyricsPalette</c> 一致的主题预设、动态取色（HSL 分桶）及颜色混合工具。
/// </summary>
internal static class LyricsPalette
{
    private const int HueBuckets = 18;
    private const int LightnessBuckets = 5;

    public static readonly Color ChromeBackground = Color.Rgb(18, 18, 18);
    public static readonly Color CoverFrameBackground = Color.Rgb(40, 40, 40);
    public static readonly Color SurfaceCard = Color.Rgb(26, 26, 26);
    public static readonly Color SurfaceElevated = Color.Rgb(38, 38, 38);
    public static readonly Color PopupBackground = Color.Rgb(32, 32, 32);
    public static readonly Color PopupBorder = Color.Rgb(64, 64, 64);
    public static readonly Color SubtleBorder = Color.Rgb(46, 46, 46);
    public static readonly Color AccentGreen = Color.Rgb(30, 215, 96);
    public static readonly Color MutedGray = Color.Rgb(179, 179, 179);

    public static readonly Color DefaultBackground = Color.Rgb(51, 51, 51);
    public static readonly Color DefaultSubdued = Color.Rgb(179, 179, 179);

    public static readonly IReadOnlyList<ClassicThemePreset> ThemePresets =
    [
        new("dynamic", "跟随专辑封面动态取色（默认）", Color.Rgb(51, 51, 51), Color.Rgb(179, 179, 179), IsDynamic: true),
        new("spotify-green", "深林暗绿（Spotify 经典绿）", Color.Rgb(27, 77, 52), Color.Rgb(154, 198, 175)),
        new("midnight", "极夜纯黑（OLED 深邃黑）", Color.Rgb(22, 22, 22), Color.Rgb(158, 158, 158)),
        new("ocean", "深海靛蓝（Midnight Blue）", Color.Rgb(26, 58, 92), Color.Rgb(148, 178, 210)),
        new("burgundy", "勃艮第酒红（Velvet Red）", Color.Rgb(79, 29, 40), Color.Rgb(204, 152, 164)),
        new("violet", "暮光暗紫（Twilight Purple）", Color.Rgb(52, 34, 78), Color.Rgb(173, 155, 199)),
        new("amber", "琥珀暖棕（Warm Espresso）", Color.Rgb(68, 44, 26), Color.Rgb(198, 172, 148)),
        new("warm-gray", "暖调岩石灰（Warm Charcoal）", Color.Rgb(48, 48, 52), Color.Rgb(176, 176, 182)),
    ];

    public static (Color Background, Color Subdued) Resolve(string? mode, (Color Background, Color Subdued) coverPalette)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            var normalized = mode.Equals("black", StringComparison.OrdinalIgnoreCase) ? "midnight" : mode;
            foreach (var preset in ThemePresets)
            {
                if (!preset.IsDynamic && string.Equals(preset.Id, normalized, StringComparison.OrdinalIgnoreCase))
                    return (preset.Background, preset.Subdued);
            }
        }
        return coverPalette;
    }

    public static Color BlendColor(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0.0, 1.0);
        return Color.Rgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    public static (Color Background, Color Subdued) FromCover(Bitmap? cover)
    {
        if (cover is null || cover.IsRecycled || cover.Width <= 0 || cover.Height <= 0)
            return (DefaultBackground, DefaultSubdued);

        var maxSide = Math.Max(cover.Width, cover.Height);
        var scale = Math.Min(1.0, 64.0 / maxSide);
        var targetW = Math.Max(1, (int)Math.Round(cover.Width * scale));
        var targetH = Math.Max(1, (int)Math.Round(cover.Height * scale));

        using var sample = targetW == cover.Width && targetH == cover.Height
            ? null
            : Bitmap.CreateScaledBitmap(cover, targetW, targetH, true);
        var src = sample ?? cover;

        var pixels = new int[src.Width * src.Height];
        src.GetPixels(pixels, 0, src.Width, 0, 0, src.Width, src.Height);

        var buckets = new (double Area, double R, double G, double B)[HueBuckets, LightnessBuckets];
        double visibleArea = 0;

        foreach (var argb in pixels)
        {
            var a = ((argb >> 24) & 0xFF) / 255.0;
            var r = (byte)((argb >> 16) & 0xFF);
            var g = (byte)((argb >> 8) & 0xFF);
            var b = (byte)(argb & 0xFF);

            visibleArea += a;
            var (hue, saturation, lightness) = ToHsl(r, g, b);
            var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            if (chroma < 0.025 || lightness is < 0.08 or > 0.90) continue;

            var hueIndex = Math.Clamp((int)((hue + 10) % 360 / 20), 0, HueBuckets - 1);
            var lightnessIndex = Math.Clamp((int)(lightness * LightnessBuckets), 0, LightnessBuckets - 1);
            ref var bucket = ref buckets[hueIndex, lightnessIndex];
            bucket.Area += a;
            bucket.R += a * r;
            bucket.G += a * g;
            bucket.B += a * b;
        }

        if (visibleArea <= 0) return (DefaultBackground, DefaultSubdued);

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
        if (muted is { } soft)
            return (FromHsl(soft.Hue, Math.Clamp(soft.Saturation * 1.8, 0.10, 0.28), 0.185),
                FromHsl(soft.Hue, Math.Clamp(soft.Saturation * 1.7, 0.10, 0.23), 0.79));
        if (vibrant is { } strong)
            return (FromHsl(strong.Hue, Math.Min(0.76, strong.Saturation * 1.3), 0.18),
                FromHsl(strong.Hue, Math.Clamp(strong.Saturation, 0.18, 0.62), 0.77));

        return (DefaultBackground, DefaultSubdued);
    }

    private readonly record struct Swatch(double Hue, double Saturation, double Score);

    private static void KeepBest(ref Swatch? best, double hue, double saturation, double score)
    {
        if (best is null || score > best.Value.Score) best = new Swatch(hue, saturation, score);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(byte r, byte g, byte b)
    {
        var red = r / 255.0;
        var green = g / 255.0;
        var blue = b / 255.0;
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
            0 => (chroma, mid, 0.0),
            1 => (mid, chroma, 0.0),
            2 => (0.0, chroma, mid),
            3 => (0.0, mid, chroma),
            4 => (mid, 0.0, chroma),
            _ => (chroma, 0.0, mid),
        };
        var offset = lightness - chroma / 2;
        return Color.Rgb(
            (byte)Math.Clamp(Math.Round((r + offset) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + offset) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + offset) * 255), 0, 255));
    }
}
