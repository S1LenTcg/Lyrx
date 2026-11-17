using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Musebase.Windows;

internal enum LyricShareStyle
{
    Spotify,
    AppleMusic,
}

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
    ShareCoverEffect Preset,
    double BlurAmount,
    double DimAmount,
    double GradientFadeAmount,
    double ColorTintAmount)
{
    public static ShareCoverEffectSettings FromPreset(ShareCoverEffect preset) => preset switch
    {
        ShareCoverEffect.Clean => new(preset, BlurAmount: 0.0, DimAmount: 0.30, GradientFadeAmount: 0.26, ColorTintAmount: 0.0),
        ShareCoverEffect.SoftBlur => new(preset, BlurAmount: 0.35, DimAmount: 0.32, GradientFadeAmount: 0.36, ColorTintAmount: 0.08),
        ShareCoverEffect.DreamyBlur => new(preset, BlurAmount: 0.82, DimAmount: 0.24, GradientFadeAmount: 0.25, ColorTintAmount: 0.22),
        ShareCoverEffect.ColorFade => new(preset, BlurAmount: 0.14, DimAmount: 0.22, GradientFadeAmount: 0.86, ColorTintAmount: 0.28),
        ShareCoverEffect.ColorTint => new(preset, BlurAmount: 0.22, DimAmount: 0.34, GradientFadeAmount: 0.42, ColorTintAmount: 0.72),
        ShareCoverEffect.Cinematic => new(preset, BlurAmount: 0.12, DimAmount: 0.56, GradientFadeAmount: 0.84, ColorTintAmount: 0.14),
        _ => FromPreset(ShareCoverEffect.SoftBlur),
    };
}

internal readonly record struct ShareLyricLine(string Original, string? Translation);

internal readonly record struct SharePalettePreset(
    string Id,
    string Label,
    Color BaseColor,
    Color? AccentEndColor = null,
    bool IsDynamic = false);

internal sealed record ShareCardOptions(
    LyricShareStyle Style,
    Color BaseColor,
    bool ShowTranslation,
    string? TrackTitle,
    string? ArtistName,
    BitmapSource? CoverBitmap,
    IReadOnlyList<ShareLyricLine> Lines,
    BitmapSource? CustomBackgroundBitmap = null,
    FontFamily? LyricsFontFamily = null,
    ShareBackgroundMode BackgroundMode = ShareBackgroundMode.Gradient,
    double FontSizeScale = 1.0,
    Color? GradientEndColor = null,
    ShareCoverEffectSettings? CoverEffect = null);

/// <summary>
/// 高分辨率歌词分享卡片渲染器，同时供预览窗与 PNG 导出 / 剪贴板 / 系统分享使用。
/// 预览与导出共享同一套 WPF 布局与度量逻辑，导出分辨率独立于窗口尺寸与系统 DPI 缩放。
/// </summary>
internal static class LyricShareCardRenderer
{
    private const int SpotifyCanvasSize = 1080;
    private const int AppleCanvasWidth = 1200;

    private static readonly Geometry AppleLeafGeometry = Geometry.Parse(
        "M 12.2,6.2 C 12.0,2.8 14.4,0.4 17.2,0.2 C 17.3,3.4 14.9,6.0 12.2,6.2 Z");

    private static readonly Geometry AppleBodyGeometry = Geometry.Parse(
        "M 12.0,8.2 C 14.2,6.4 18.2,6.4 20.4,8.8 C 17.8,10.6 17.5,16.4 21.3,18.6 C 20.2,22.8 17.6,27.2 15.2,27.2 C 13.8,27.2 13.2,26.3 12.0,26.3 C 10.8,26.3 10.2,27.2 8.8,27.2 C 6.2,27.2 2.2,21.8 2.2,15.0 C 2.2,9.6 6.0,6.4 9.2,6.4 C 10.6,6.4 11.4,8.2 12.0,8.2 Z");

    private static readonly Geometry SpotifyWave1 = Geometry.Parse("M 9.12,18.72 Q 24,12.48 38.88,18.72");
    private static readonly Geometry SpotifyWave2 = Geometry.Parse("M 12.00,26.40 Q 24,21.36 36.00,26.40");
    private static readonly Geometry SpotifyWave3 = Geometry.Parse("M 14.64,33.36 Q 24,29.52 33.36,33.36");

    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol, Microsoft YaHei UI, Segoe UI");

    static LyricShareCardRenderer()
    {
        AppleLeafGeometry.Freeze();
        AppleBodyGeometry.Freeze();
        SpotifyWave1.Freeze();
        SpotifyWave2.Freeze();
        SpotifyWave3.Freeze();
    }

    public const double ExportScale = 2.0;
    public const double MinFontScale = 0.75;
    public const double MaxFontScale = 1.35;

    private static BitmapSource? ResolveBackgroundImage(ShareCardOptions opt, out bool isAlbumCover)
    {
        if (opt.BackgroundMode == ShareBackgroundMode.CustomImage
            && opt.CustomBackgroundBitmap is { PixelWidth: > 0, PixelHeight: > 0 } custom)
        {
            isAlbumCover = false;
            return custom;
        }
        if (opt.BackgroundMode == ShareBackgroundMode.AlbumCover
            && opt.CoverBitmap is { PixelWidth: > 0, PixelHeight: > 0 } cover)
        {
            isAlbumCover = true;
            return cover;
        }
        if (opt.BackgroundMode == ShareBackgroundMode.Gradient
            && opt.CustomBackgroundBitmap is { PixelWidth: > 0, PixelHeight: > 0 } legacyCustom)
        {
            isAlbumCover = false;
            return legacyCustom;
        }
        isAlbumCover = false;
        return null;
    }

    public static IReadOnlyList<SharePalettePreset> BuildPresets(Color dynamicBaseColor)
    {
        var liftedDynamic = ClassicLyricsPalette.LiftToShareBrightness(dynamicBaseColor);
        var dynamicEnd = ClassicLyricsPalette.BlendColor(liftedDynamic, Colors.Black, 0.28);
        return
        [
            new("dynamic", "封面取色", liftedDynamic, dynamicEnd, IsDynamic: true),
            new("spotify-pine", "松林幽绿", Color.FromRgb(24, 106, 77), Color.FromRgb(11, 61, 44)),
            new("nordic-indigo", "雾霭靛蓝", Color.FromRgb(42, 71, 110), Color.FromRgb(20, 36, 59)),
            new("twilight-plum", "暮光暗紫", Color.FromRgb(82, 62, 112), Color.FromRgb(40, 29, 59)),
            new("terracotta", "赤陶暖阳", Color.FromRgb(198, 76, 50), Color.FromRgb(118, 34, 22)),
            new("oat-cream", "奶油燕麦", Color.FromRgb(237, 228, 211), Color.FromRgb(210, 195, 170)),
            new("mint-pastel", "薄荷苏打", Color.FromRgb(198, 240, 219), Color.FromRgb(145, 212, 182)),
            new("obsidian", "曜石深炭", Color.FromRgb(42, 42, 46), Color.FromRgb(18, 18, 20)),
        ];
    }

    public static FrameworkElement BuildCardVisual(ShareCardOptions options, out int canvasWidth, out int canvasHeight, double scale = ExportScale)
    {
        var s = Math.Max(1.0, scale);
        var visual = options.Style == LyricShareStyle.AppleMusic
            ? BuildAppleMusicCard(options, s, out canvasWidth, out canvasHeight)
            : BuildSpotifyCard(options, s, out canvasWidth, out canvasHeight);

        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Ideal);
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        return visual;
    }

    public static BitmapSource RenderBitmap(ShareCardOptions options, double scale = ExportScale)
    {
        var visual = BuildCardVisual(options, out var width, out var height, scale);
        return RenderVisual(visual, width, height);
    }

    private static BitmapSource RenderVisual(FrameworkElement visual, int width, int height)
    {
        visual.Measure(new Size(width, height));
        visual.Arrange(new Rect(0, 0, width, height));
        visual.UpdateLayout();

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// 按剪贴板配置导出预览样式位图：
    /// <paramref name="keepBackdrop"/> 为 true 时连同预览舞台渐变外背景与投影一起复制；
    /// 为 false 时仅复制纯净的圆角卡片（外角透明）。
    /// </summary>
    public static BitmapSource RenderClipboardPreviewBitmap(
        ShareCardOptions options,
        bool keepBackdrop = true,
        Color? stageThemeColor = null,
        double scale = ExportScale)
    {
        var s = Math.Max(1.0, scale);
        var cardVisual = BuildCardVisual(options, out var cardW, out var cardH, s);
        var radius = (options.Style == LyricShareStyle.Spotify ? 42.0 : 34.0) * s;

        if (!keepBackdrop)
        {
            var cardOnlyHost = new Border
            {
                Width = cardW,
                Height = cardH,
                Background = Brushes.Transparent,
                Clip = new RectangleGeometry(new Rect(0, 0, cardW, cardH), radius, radius),
                Child = cardVisual,
            };
            TextOptions.SetTextFormattingMode(cardOnlyHost, TextFormattingMode.Ideal);
            RenderOptions.SetBitmapScalingMode(cardOnlyHost, BitmapScalingMode.HighQuality);
            return RenderVisual(cardOnlyHost, cardW, cardH);
        }

        var pad = (int)Math.Round(72 * s);
        var outW = cardW + pad * 2;
        var outH = cardH + pad * 2;
        var theme = stageThemeColor ?? options.BaseColor;

        var stageBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        stageBrush.GradientStops.Add(new GradientStop(Color.FromRgb(10, 10, 12), 0.0));
        stageBrush.GradientStops.Add(new GradientStop(ClassicLyricsPalette.BlendColor(theme, Colors.Black, 0.42), 0.46));
        stageBrush.GradientStops.Add(new GradientStop(ClassicLyricsPalette.BlendColor(theme, Colors.Black, 0.12), 1.0));

        var host = new Grid
        {
            Width = outW,
            Height = outH,
            Background = stageBrush,
        };

        // 独立阴影兄弟节点置于卡片下方，避免 Blur/DropShadow 包裹文字导致文字发虚
        var shadowBackdrop = new Border
        {
            Width = cardW,
            Height = cardH,
            Margin = new Thickness(pad),
            CornerRadius = new CornerRadius(radius),
            Background = Brushes.Black,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 52 * s,
                ShadowDepth = 14 * s,
                Direction = 270,
                Opacity = 0.58,
            },
        };
        host.Children.Add(shadowBackdrop);

        var clippedCard = new Border
        {
            Width = cardW,
            Height = cardH,
            Margin = new Thickness(pad),
            Clip = new RectangleGeometry(new Rect(0, 0, cardW, cardH), radius, radius),
            Child = cardVisual,
        };
        host.Children.Add(clippedCard);

        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Ideal);
        RenderOptions.SetBitmapScalingMode(host, BitmapScalingMode.HighQuality);
        return RenderVisual(host, outW, outH);
    }

    public static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static FrameworkElement BuildSpotifyCard(ShareCardOptions opt, double s, out int canvasWidth, out int canvasHeight)
    {
        var size = (int)Math.Round(SpotifyCanvasSize * s);
        var padX = 70 * s;
        var padTop = 66 * s;
        var coverSize = 104 * s;
        var lyricsTopBound = 224 * s;
        var lyricsBottomBound = 916 * s;

        canvasWidth = size;
        canvasHeight = size;

        var root = new Grid
        {
            Width = size,
            Height = size,
            ClipToBounds = true,
        };

        var bgImage = ResolveBackgroundImage(opt, out _);
        var isLightSurface = bgImage is null && ClassicLyricsPalette.RelativeLuminance(opt.BaseColor) > 0.36;
        var endColor = opt.GradientEndColor ?? ClassicLyricsPalette.BlendColor(opt.BaseColor, Colors.Black, 0.24);
        Color badgeBottomBg;

        if (bgImage is not null)
        {
            AddFilteredBackgroundLayers(root, bgImage, opt, size, size, out badgeBottomBg);
        }
        else if (opt.BackgroundMode == ShareBackgroundMode.Solid)
        {
            root.Children.Add(new Border { Background = FrozenBrush(opt.BaseColor) });
            badgeBottomBg = opt.BaseColor;
        }
        else
        {
            var topBg = isLightSurface
                ? ClassicLyricsPalette.BlendColor(opt.BaseColor, Colors.White, 0.20)
                : ClassicLyricsPalette.BlendColor(opt.BaseColor, Colors.White, 0.10);
            var midBg = opt.BaseColor;
            var bottomBg = endColor;

            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0.06, 0),
                EndPoint = new Point(0.75, 1),
            };
            gradient.GradientStops.Add(new GradientStop(topBg, 0.0));
            gradient.GradientStops.Add(new GradientStop(midBg, 0.44));
            gradient.GradientStops.Add(new GradientStop(bottomBg, 1.0));
            root.Children.Add(new Border { Background = gradient });

            badgeBottomBg = ClassicLyricsPalette.BlendColor(midBg, bottomBg, 0.72);
        }

        var mainInk = isLightSurface ? Color.FromRgb(18, 18, 18) : Colors.White;
        var subInk = isLightSurface ? Color.FromArgb(184, 18, 18, 18) : Color.FromArgb(204, 255, 255, 255);
        var metaSubInk = isLightSurface ? Color.FromArgb(174, 18, 18, 18) : Color.FromArgb(224, 255, 255, 255);

        // 1. 顶部专辑封面 + 歌曲名 + 「歌曲 · 歌手」
        var headerGrid = new Grid
        {
            Margin = new Thickness(padX, padTop, padX, 0),
            Height = coverSize,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(coverSize) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24 * s) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var coverThumb = CreateCoverThumbnail(opt.CoverBitmap, coverSize, radius: 14 * s);
        Grid.SetColumn(coverThumb, 0);
        headerGrid.Children.Add(coverThumb);

        var titleText = string.IsNullOrWhiteSpace(opt.TrackTitle) ? "未知歌曲" : opt.TrackTitle.Trim();
        var subText = string.IsNullOrWhiteSpace(opt.ArtistName) ? "歌曲" : $"歌曲 · {opt.ArtistName.Trim()}";

        var metaStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        metaStack.Children.Add(new TextBlock
        {
            Text = titleText,
            FontFamily = ClassicLyricsFonts.Body,
            FontWeight = FontWeights.Bold,
            FontSize = 34 * s,
            Foreground = FrozenBrush(mainInk),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        metaStack.Children.Add(new TextBlock
        {
            Text = subText,
            FontFamily = ClassicLyricsFonts.Body,
            FontWeight = FontWeights.Normal,
            FontSize = 27 * s,
            Foreground = FrozenBrush(metaSubInk),
            Margin = new Thickness(0, 6 * s, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        Grid.SetColumn(metaStack, 2);
        headerGrid.Children.Add(metaStack);
        root.Children.Add(headerGrid);

        // 2. 中部自适应歌词块（确保不空不挤，并尊重用户字号缩放与自然换行）
        var useSpotifyMix = opt.LyricsFontFamily is null
            || string.Equals(opt.LyricsFontFamily.Source, ClassicLyricsFonts.Body.Source, StringComparison.Ordinal);
        var mainLyricFont = useSpotifyMix ? ClassicLyricsFonts.SpotifyTitle : opt.LyricsFontFamily!;
        var subLyricFont = useSpotifyMix ? ClassicLyricsFonts.Body : opt.LyricsFontFamily!;
        var lyricsMaxW = size - padX * 2;
        var maxAvailH = lyricsBottomBound - lyricsTopBound;
        var sparseCount = opt.Lines.Count <= 3;
        var baseMaxFont = opt.Lines.Count switch
        {
            1 => 82.0,
            2 => 74.0,
            3 => 66.0,
            _ => 58.0,
        };

        var (fontSize, lineGap, trGap, totalH) = FitLyricsMetrics(
            opt.Lines,
            opt.ShowTranslation,
            mainLyricFont,
            FontWeights.Bold,
            subLyricFont,
            FontWeights.Normal,
            lyricsMaxW,
            maxAvailH,
            baseMaxFontSize: baseMaxFont * s,
            minFontSize: 28 * s,
            hardMinFontSize: 12 * s,
            lineSpacingMult: sparseCount ? 1.15 : 1.12,
            lineGapRatio: sparseCount ? 0.28 : 0.22,
            trGapRatio: sparseCount ? 0.12 : 0.10,
            fontSizeScale: opt.FontSizeScale,
            scale: s);

        var lyricsBlock = BuildLyricsBlock(
            opt.Lines,
            opt.ShowTranslation,
            mainLyricFont,
            FontWeights.Bold,
            subLyricFont,
            FontWeights.Normal,
            fontSize,
            lineGap,
            trGap,
            lineSpacingMult: sparseCount ? 1.15 : 1.12,
            maxW: lyricsMaxW,
            mainColor: mainInk,
            subColor: subInk,
            scale: s);

        var lyricsHost = new Grid
        {
            Margin = new Thickness(padX, lyricsTopBound, padX, size - lyricsBottomBound),
            ClipToBounds = true,
        };
        if (totalH > maxAvailH)
        {
            lyricsHost.Children.Add(new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = lyricsBlock,
            });
        }
        else
        {
            lyricsBlock.VerticalAlignment = VerticalAlignment.Center;
            lyricsHost.Children.Add(lyricsBlock);
        }
        root.Children.Add(lyricsHost);

        // 3. 底部左下角 Spotify 样式标识（centerY = size - 78*s, r = 24*s → bottom = 54*s）
        var brand = BuildSpotifyBrand(mainInk, badgeBottomBg, mainInk, s);
        brand.HorizontalAlignment = HorizontalAlignment.Left;
        brand.VerticalAlignment = VerticalAlignment.Bottom;
        brand.Margin = new Thickness(padX, 0, 0, 54 * s);
        root.Children.Add(brand);

        return root;
    }

    private static FrameworkElement BuildAppleMusicCard(ShareCardOptions opt, double s, out int canvasWidth, out int canvasHeight)
    {
        var width = (int)Math.Round(AppleCanvasWidth * s);
        var outerMargin = 68 * s;
        var cardW = width - outerMargin * 2;
        var cardPadX = 62 * s;
        var textW = cardW - cardPadX * 2;
        var cardRadius = 34 * s;
        var upperPadY = (opt.Lines.Count <= 2 ? 78 : opt.Lines.Count <= 4 ? 64 : 56) * s;
        var upperMinH = (opt.Lines.Count == 1 ? 300 : opt.Lines.Count == 2 ? 270 : 240) * s;
        var lowerH = 222 * s;

        var lyricFont = opt.LyricsFontFamily ?? ClassicLyricsFonts.Family;
        var metaFont = ClassicLyricsFonts.Family;
        var sparseCount = opt.Lines.Count <= 3;
        var baseMaxFont = opt.Lines.Count switch
        {
            1 => 66.0,
            2 => 60.0,
            3 => 55.0,
            _ => 50.0,
        };

        var (fontSize, lineGap, trGap, lyricsH) = FitLyricsMetrics(
            opt.Lines,
            opt.ShowTranslation,
            lyricFont,
            FontWeights.Normal,
            lyricFont,
            FontWeights.Normal,
            textW,
            maxAvailH: 720 * s,
            baseMaxFontSize: baseMaxFont * s,
            minFontSize: 28 * s,
            hardMinFontSize: 22 * s,
            lineSpacingMult: 1.16,
            lineGapRatio: sparseCount ? 0.28 : 0.22,
            trGapRatio: sparseCount ? 0.12 : 0.10,
            fontSizeScale: opt.FontSizeScale,
            scale: s);

        var upperH = Math.Max(upperMinH, Math.Ceiling(lyricsH) + upperPadY * 2);
        var cardH = upperH + lowerH;
        canvasWidth = width;
        canvasHeight = (int)Math.Round(cardH + outerMargin * 2);

        var root = new Grid
        {
            Width = canvasWidth,
            Height = canvasHeight,
            ClipToBounds = true,
        };

        var bgImage = ResolveBackgroundImage(opt, out _);
        var hasImageBg = bgImage is not null;
        var isLightSurface = !hasImageBg && ClassicLyricsPalette.RelativeLuminance(opt.BaseColor) > 0.36;
        var fx = opt.CoverEffect ?? ShareCoverEffectSettings.FromPreset(ShareCoverEffect.SoftBlur);
        Color effectiveBase = opt.BaseColor;

        if (hasImageBg)
        {
            AddFilteredBackgroundLayers(root, bgImage!, opt, canvasWidth, canvasHeight, out effectiveBase);
        }
        else if (isLightSurface)
        {
            var topFrame = ClassicLyricsPalette.BlendColor(effectiveBase, Colors.White, 0.24);
            var endFrame = opt.GradientEndColor ?? ClassicLyricsPalette.BlendColor(effectiveBase, Colors.Black, 0.10);
            root.Children.Add(opt.BackgroundMode == ShareBackgroundMode.Solid
                ? new Border { Background = FrozenBrush(effectiveBase) }
                : new Border
                {
                    Background = new LinearGradientBrush(topFrame, endFrame, new Point(0.1, 0), new Point(0.85, 1)),
                });
        }
        else if (opt.BackgroundMode == ShareBackgroundMode.Solid)
        {
            var isDarkNeutral = Math.Abs(effectiveBase.R - effectiveBase.G) <= 6
                && Math.Abs(effectiveBase.G - effectiveBase.B) <= 6
                && effectiveBase.R < 55;
            var solidFrame = isDarkNeutral
                ? Color.FromRgb(42, 44, 48)
                : ClassicLyricsPalette.LiftToShareBrightness(effectiveBase, minLuminance: 0.065, maxLuminance: 0.175);
            root.Children.Add(new Border { Background = FrozenBrush(solidFrame) });
            effectiveBase = solidFrame;
        }
        else
        {
            var isDarkNeutral = Math.Abs(effectiveBase.R - effectiveBase.G) <= 6
                && Math.Abs(effectiveBase.G - effectiveBase.B) <= 6
                && effectiveBase.R < 55;
            var backdropBase = isDarkNeutral
                ? Color.FromRgb(56, 60, 66)
                : ClassicLyricsPalette.LiftToShareBrightness(effectiveBase, minLuminance: 0.065, maxLuminance: 0.175);
            var backdropTop = ClassicLyricsPalette.BlendColor(backdropBase, Colors.White, 0.16);
            var backdropBottom = opt.GradientEndColor ?? ClassicLyricsPalette.BlendColor(backdropBase, Colors.Black, 0.12);

            root.Children.Add(new Border
            {
                Background = new LinearGradientBrush(
                    backdropTop,
                    backdropBottom,
                    new Point(0.08, 0),
                    new Point(0.85, 1)),
            });

            if (opt.CoverBitmap is { PixelWidth: > 0, PixelHeight: > 0 } coverBmp
                && (coverBmp.PixelWidth > 12 || coverBmp.PixelHeight > 12))
            {
                var tiny = new TransformedBitmap(
                    coverBmp,
                    new ScaleTransform(12.0 / coverBmp.PixelWidth, 12.0 / coverBmp.PixelHeight));
                tiny.Freeze();
                var wash = new Image
                {
                    Source = tiny,
                    Stretch = Stretch.Fill,
                    Opacity = 28.0 / 255.0,
                };
                RenderOptions.SetBitmapScalingMode(wash, BitmapScalingMode.HighQuality);
                root.Children.Add(wash);
            }

            effectiveBase = backdropBase;
        }

        // 2. 卡片柔和投影（独立于圆角裁剪外层，避免裁切阴影）
        var shadowCard = new Border
        {
            Width = cardW,
            Height = cardH,
            Margin = new Thickness(outerMargin),
            CornerRadius = new CornerRadius(cardRadius),
            Background = FrozenBrush(Color.FromArgb(hasImageBg ? (byte)54 : (byte)255, 0, 0, 0)),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 28 * s,
                ShadowDepth = 6 * s,
                Direction = 270,
                Opacity = isLightSurface ? 0.16 : 0.28,
            },
        };
        root.Children.Add(shadowCard);

        // 3. 圆角双色卡片主体
        Color upperColor, lowerColor, cardMainInk, cardSubInk, cardMetaSubInk, appleBrandInk;
        byte upperAlpha = 255;
        byte lowerAlpha = 255;
        if (hasImageBg)
        {
            var baseNeutral = Color.FromRgb(16, 17, 21);
            var dockNeutral = Color.FromRgb(10, 11, 15);
            var chroma = Math.Max(opt.BaseColor.R, Math.Max(opt.BaseColor.G, opt.BaseColor.B))
                - Math.Min(opt.BaseColor.R, Math.Min(opt.BaseColor.G, opt.BaseColor.B));
            var safeGlassTint = ClassicLyricsPalette.LiftToShareBrightness(opt.BaseColor, minLuminance: 0.04, maxLuminance: 0.14);
            var tintWeight = chroma >= 20
                ? Math.Clamp(0.16 + fx.ColorTintAmount * 0.26 + Math.Max(0, fx.GradientFadeAmount - 0.30) * 0.20, 0.16, 0.45)
                : fx.ColorTintAmount * 0.20;

            upperColor = tintWeight > 0.02 ? ClassicLyricsPalette.BlendColor(baseNeutral, safeGlassTint, tintWeight) : baseNeutral;
            lowerColor = tintWeight > 0.02 ? ClassicLyricsPalette.BlendColor(dockNeutral, safeGlassTint, tintWeight * 0.85) : dockNeutral;
            upperAlpha = (byte)Math.Round(Math.Clamp(116 + fx.BlurAmount * 22, 116, 146));
            lowerAlpha = (byte)Math.Round(Math.Clamp(158 + fx.BlurAmount * 18, 158, 184));
            cardMainInk = Color.FromArgb(245, 255, 255, 255);
            cardSubInk = Color.FromArgb(190, 255, 255, 255);
            cardMetaSubInk = Color.FromArgb(212, 255, 255, 255);
            appleBrandInk = Color.FromArgb(135, 255, 255, 255);
        }
        else if (isLightSurface)
        {
            upperColor = ClassicLyricsPalette.BlendColor(opt.BaseColor, Colors.White, 0.72);
            lowerColor = ClassicLyricsPalette.BlendColor(opt.BaseColor, Colors.White, 0.44);
            cardMainInk = Color.FromArgb(240, 18, 18, 18);
            cardSubInk = Color.FromArgb(172, 18, 18, 18);
            cardMetaSubInk = Color.FromArgb(192, 18, 18, 18);
            appleBrandInk = Color.FromArgb(132, 18, 18, 18);
        }
        else
        {
            var isDarkEffective = Math.Abs(effectiveBase.R - effectiveBase.G) <= 6
                && Math.Abs(effectiveBase.G - effectiveBase.B) <= 6
                && effectiveBase.R < 75;
            upperColor = isDarkEffective
                ? Color.FromRgb(42, 44, 48)
                : opt.BackgroundMode == ShareBackgroundMode.Solid
                    ? ClassicLyricsPalette.BlendColor(effectiveBase, Colors.Black, 0.08)
                    : ClassicLyricsPalette.BlendColor(effectiveBase, Colors.White, 0.04);
            lowerColor = isDarkEffective
                ? Color.FromRgb(28, 30, 34)
                : ClassicLyricsPalette.BlendColor(effectiveBase, Colors.Black, opt.BackgroundMode == ShareBackgroundMode.Solid ? 0.18 : 0.14);
            cardMainInk = Color.FromArgb(245, 255, 255, 255);
            cardSubInk = Color.FromArgb(182, 255, 255, 255);
            cardMetaSubInk = Color.FromArgb(204, 255, 255, 255);
            appleBrandInk = Color.FromArgb(115, 255, 255, 255);
        }

        var cardGrid = new Grid
        {
            Width = cardW,
            Height = cardH,
        };
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(upperH) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(lowerH) });

        var upperPanel = new Border
        {
            Background = FrozenBrush(Color.FromArgb(upperAlpha, upperColor.R, upperColor.G, upperColor.B)),
            Padding = new Thickness(cardPadX, 0, cardPadX, 0),
        };
        var lyricsBlock = BuildLyricsBlock(
            opt.Lines,
            opt.ShowTranslation,
            lyricFont,
            FontWeights.Normal,
            lyricFont,
            FontWeights.Normal,
            fontSize,
            lineGap,
            trGap,
            lineSpacingMult: 1.16,
            maxW: textW,
            mainColor: cardMainInk,
            subColor: cardSubInk,
            scale: s);
        lyricsBlock.VerticalAlignment = VerticalAlignment.Center;
        upperPanel.Child = lyricsBlock;
        Grid.SetRow(upperPanel, 0);
        cardGrid.Children.Add(upperPanel);

        // 下半区：封面 + 歌名 + 歌手 + Apple Music 标识
        var lowerPanel = new Border
        {
            Background = FrozenBrush(Color.FromArgb(lowerAlpha, lowerColor.R, lowerColor.G, lowerColor.B)),
            Padding = new Thickness(cardPadX, 0, cardPadX, 0),
        };
        var lowerGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        lowerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(142 * s) });
        lowerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30 * s) });
        lowerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var appleCover = CreateCoverThumbnail(opt.CoverBitmap, 142 * s, radius: 16 * s);
        Grid.SetColumn(appleCover, 0);
        lowerGrid.Children.Add(appleCover);

        var titleStr = string.IsNullOrWhiteSpace(opt.TrackTitle) ? "未知歌曲" : opt.TrackTitle.Trim();
        var artistStr = string.IsNullOrWhiteSpace(opt.ArtistName) ? "" : opt.ArtistName.Trim();

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoStack.Children.Add(new TextBlock
        {
            Text = titleStr,
            FontFamily = metaFont,
            FontWeight = FontWeights.Medium,
            FontSize = 35 * s,
            Foreground = FrozenBrush(cardMainInk),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        if (!string.IsNullOrEmpty(artistStr))
        {
            infoStack.Children.Add(new TextBlock
            {
                Text = artistStr,
                FontFamily = metaFont,
                FontWeight = FontWeights.Normal,
                FontSize = 32 * s,
                Foreground = FrozenBrush(cardMetaSubInk),
                Margin = new Thickness(0, 6 * s, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
        }
        var appleBrand = BuildAppleMusicBrand(appleBrandInk, s);
        appleBrand.Margin = new Thickness(0, 14 * s, 0, 0);
        infoStack.Children.Add(appleBrand);

        Grid.SetColumn(infoStack, 2);
        lowerGrid.Children.Add(infoStack);
        lowerPanel.Child = lowerGrid;
        Grid.SetRow(lowerPanel, 1);
        cardGrid.Children.Add(lowerPanel);

        var cardClipHost = new Border
        {
            Width = cardW,
            Height = cardH,
            Margin = new Thickness(outerMargin),
            Clip = new RectangleGeometry(new Rect(0, 0, cardW, cardH), cardRadius, cardRadius),
            Child = cardGrid,
        };
        root.Children.Add(cardClipHost);
        if (hasImageBg)
        {
            root.Children.Add(new Border
            {
                Width = cardW,
                Height = cardH,
                Margin = new Thickness(outerMargin),
                CornerRadius = new CornerRadius(cardRadius),
                BorderBrush = FrozenBrush(Color.FromArgb(44, 255, 255, 255)),
                BorderThickness = new Thickness(Math.Max(1.0, 1.4 * s)),
                IsHitTestVisible = false,
            });
        }

        return root;
    }

    private static (double FontSize, double LineGap, double TrGap, double TotalHeight) FitLyricsMetrics(
        IReadOnlyList<ShareLyricLine> lines,
        bool showTranslation,
        FontFamily mainFont,
        FontWeight mainWeight,
        FontFamily subFont,
        FontWeight subWeight,
        double maxW,
        double maxAvailH,
        double baseMaxFontSize,
        double minFontSize,
        double hardMinFontSize,
        double lineSpacingMult,
        double lineGapRatio,
        double trGapRatio,
        double fontSizeScale,
        double scale)
    {
        var step = Math.Max(1.0, 2.0 * scale);
        var userScale = Math.Clamp(fontSizeScale, MinFontScale, MaxFontScale);

        (double FontSize, double LineGap, double TrGap, double TotalHeight) MeasureForSize(double size)
        {
            var lineGap = Math.Round(size * lineGapRatio);
            var trGap = Math.Round(size * trGapRatio);
            var h = MeasureLyricsHeight(
                lines, showTranslation, mainFont, mainWeight, subFont, subWeight,
                size, lineGap, trGap, lineSpacingMult, maxW, scale);
            return (size, lineGap, trGap, h);
        }

        (double FontSize, double LineGap, double TrGap, double TotalHeight) FindBestFit(double ceiling)
        {
            var floor = Math.Min(minFontSize, ceiling);
            for (var size = ceiling; size >= floor; size -= step)
            {
                var measured = MeasureForSize(size);
                if (measured.TotalHeight <= maxAvailH)
                    return measured;
            }
            for (var size = floor - step; size >= hardMinFontSize; size -= step)
            {
                var measured = MeasureForSize(size);
                if (measured.TotalHeight <= maxAvailH || size <= hardMinFontSize)
                    return measured;
            }
            return MeasureForSize(Math.Min(floor, hardMinFontSize));
        }

        if (userScale > 1.01)
        {
            var enlargedMax = Math.Min(Math.Round(baseMaxFontSize * userScale), Math.Round(108.0 * scale));
            return FindBestFit(enlargedMax);
        }

        var autoMetrics = FindBestFit(baseMaxFontSize);
        if (userScale < 0.99)
        {
            var shrunk = Math.Max(hardMinFontSize, Math.Round(autoMetrics.FontSize * userScale));
            return MeasureForSize(shrunk);
        }

        return autoMetrics;
    }

    private static double MeasureLyricsHeight(
        IReadOnlyList<ShareLyricLine> lines,
        bool showTranslation,
        FontFamily mainFont,
        FontWeight mainWeight,
        FontFamily subFont,
        FontWeight subWeight,
        double mainSize,
        double lineGap,
        double trGap,
        double lineSpacingMult,
        double maxW,
        double scale)
    {
        var subSize = ComputeSubFontSize(mainSize, scale);
        double total = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var item = lines[i];
            var text = string.IsNullOrWhiteSpace(item.Original) ? "♪" : item.Original.Trim();
            var mainTb = CreateLyricTextBlock(text, mainFont, mainWeight, mainSize, lineSpacingMult, Brushes.White);
            mainTb.Measure(new Size(maxW, double.PositiveInfinity));
            total += mainTb.DesiredSize.Height;

            if (showTranslation && !string.IsNullOrWhiteSpace(item.Translation))
            {
                var trTb = CreateLyricTextBlock(item.Translation!.Trim(), subFont, subWeight, subSize, 1.15, Brushes.White);
                trTb.Measure(new Size(maxW, double.PositiveInfinity));
                total += trGap + trTb.DesiredSize.Height;
            }

            if (i < lines.Count - 1)
                total += lineGap;
        }

        return total;
    }

    private static StackPanel BuildLyricsBlock(
        IReadOnlyList<ShareLyricLine> lines,
        bool showTranslation,
        FontFamily mainFont,
        FontWeight mainWeight,
        FontFamily subFont,
        FontWeight subWeight,
        double mainSize,
        double lineGap,
        double trGap,
        double lineSpacingMult,
        double maxW,
        Color mainColor,
        Color subColor,
        double scale)
    {
        var stack = new StackPanel { Width = maxW };
        var mainBrush = FrozenBrush(mainColor);
        var subBrush = FrozenBrush(subColor);
        var subSize = ComputeSubFontSize(mainSize, scale);

        for (var i = 0; i < lines.Count; i++)
        {
            var item = lines[i];
            var hasTr = showTranslation && !string.IsNullOrWhiteSpace(item.Translation);
            var isLast = i == lines.Count - 1;

            var text = string.IsNullOrWhiteSpace(item.Original) ? "♪" : item.Original.Trim();
            var mainTb = CreateLyricTextBlock(text, mainFont, mainWeight, mainSize, lineSpacingMult, mainBrush);
            if (!hasTr && !isLast)
                mainTb.Margin = new Thickness(0, 0, 0, lineGap);
            stack.Children.Add(mainTb);

            if (hasTr)
            {
                var trTb = CreateLyricTextBlock(item.Translation!.Trim(), subFont, subWeight, subSize, 1.15, subBrush);
                trTb.Margin = new Thickness(0, trGap, 0, isLast ? 0 : lineGap);
                stack.Children.Add(trTb);
            }
        }

        return stack;
    }

    private static double ComputeSubFontSize(double mainSize, double scale)
    {
        var minSub = mainSize >= 28 * scale ? 18 * scale : 11 * scale;
        return Math.Max(minSub, Math.Min(mainSize - 3 * scale, Math.Round(mainSize * 0.54)));
    }

    private static TextBlock CreateLyricTextBlock(
        string text,
        FontFamily font,
        FontWeight weight,
        double fontSize,
        double lineSpacingMult,
        Brush foreground) => new()
    {
        Text = text,
        FontFamily = font,
        FontWeight = weight,
        FontSize = fontSize,
        LineHeight = Math.Round(fontSize * lineSpacingMult),
        LineStackingStrategy = LineStackingStrategy.MaxHeight,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Left,
        Foreground = foreground,
    };

    private static readonly object BgProcessLock = new();
    private static BitmapSource? _cachedSourceBmp;
    private static int _cachedBlurRadius = -1;
    private static Color _cachedTintColor;
    private static int _cachedTintMilli = -1;
    private static BitmapSource? _cachedProcessedSharp;
    private static BitmapSource? _cachedProcessedBlurred;

    private static void AddFilteredBackgroundLayers(
        Grid root,
        BitmapSource bgImage,
        ShareCardOptions opt,
        double width,
        double height,
        out Color bottomAccentColor)
    {
        var fx = opt.CoverEffect ?? ShareCoverEffectSettings.FromPreset(ShareCoverEffect.SoftBlur);
        var blurAmt = Math.Clamp(fx.BlurAmount, 0.0, 1.0);
        var dimAmt = Math.Clamp(fx.DimAmount, 0.0, 0.88);
        var fadeAmt = Math.Clamp(fx.GradientFadeAmount, 0.0, 1.0);
        var tintAmt = Math.Clamp(fx.ColorTintAmount, 0.0, 1.0);

        var chroma = Math.Max(opt.BaseColor.R, Math.Max(opt.BaseColor.G, opt.BaseColor.B))
            - Math.Min(opt.BaseColor.R, Math.Min(opt.BaseColor.G, opt.BaseColor.B));
        var safeTint = chroma <= 12
            ? Color.FromRgb(26, 28, 32)
            : ClassicLyricsPalette.LiftToShareBrightness(opt.BaseColor, minLuminance: 0.045, maxLuminance: 0.165);
        var tintDark = ClassicLyricsPalette.BlendColor(safeTint, Colors.Black, 0.16);
        var tintDeep = ClassicLyricsPalette.BlendColor(safeTint, Colors.Black, 0.42);

        GetProcessedBackgroundBitmaps(bgImage, blurAmt, safeTint, tintAmt, out var sharpBmp, out var blurredBmp);

        if (blurAmt <= 0.01 || blurredBmp is null)
        {
            root.Children.Add(CreateCenterCropImage(sharpBmp, width, height));
        }
        else if (blurAmt < 0.22)
        {
            root.Children.Add(CreateCenterCropImage(sharpBmp, width, height));
            var blurLayer = CreateCenterCropImage(blurredBmp, width, height);
            blurLayer.Opacity = Math.Clamp(blurAmt / 0.22, 0.0, 1.0);
            root.Children.Add(blurLayer);
        }
        else
        {
            root.Children.Add(CreateCenterCropImage(blurredBmp, width, height));
        }

        if (dimAmt > 0.005)
        {
            var dimAlpha = (byte)Math.Round(dimAmt * 255.0);
            root.Children.Add(new Border { Background = FrozenBrush(Color.FromArgb(dimAlpha, 0, 0, 0)) });
        }

        if (fadeAmt > 0.01)
        {
            var fadeBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            };
            fadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(fadeAmt * 78), 0, 0, 0), 0.0));
            fadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(fadeAmt * 38), tintDark.R, tintDark.G, tintDark.B), 0.36));
            fadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(fadeAmt * 168), tintDark.R, tintDark.G, tintDark.B), 0.74));
            fadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(Math.Min(242, fadeAmt * 234)), tintDeep.R, tintDeep.G, tintDeep.B), 1.0));
            root.Children.Add(new Border { Background = fadeBrush });

            if (fadeAmt >= 0.38)
            {
                var vigAlpha = (byte)Math.Round(Math.Clamp((fadeAmt - 0.32) * 125.0, 0.0, 110.0));
                var vignette = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.44),
                    GradientOrigin = new Point(0.5, 0.44),
                    RadiusX = 0.72,
                    RadiusY = 0.72,
                };
                vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45));
                vignette.GradientStops.Add(new GradientStop(Color.FromArgb(vigAlpha, 0, 0, 0), 1.0));
                root.Children.Add(new Border { Background = vignette });
            }
        }

        bottomAccentColor = ClassicLyricsPalette.BlendColor(safeTint, tintDeep, Math.Clamp(fadeAmt * 0.65 + 0.30, 0.0, 1.0));
    }

    private static void GetProcessedBackgroundBitmaps(
        BitmapSource source,
        double blurAmount,
        Color tintColor,
        double tintAmount,
        out BitmapSource sharpResult,
        out BitmapSource? blurredResult)
    {
        var tintMilli = (int)Math.Round(tintAmount * 1000.0);
        var blurRadius = blurAmount <= 0.01 ? 0 : Math.Clamp((int)Math.Round(1 + blurAmount * 26.0), 1, 28);

        lock (BgProcessLock)
        {
            if (ReferenceEquals(_cachedSourceBmp, source) && _cachedTintColor == tintColor && _cachedTintMilli == tintMilli)
            {
                sharpResult = _cachedProcessedSharp ?? source;
                if (blurRadius == 0)
                {
                    blurredResult = null;
                    return;
                }
                if (_cachedBlurRadius == blurRadius && _cachedProcessedBlurred is not null)
                {
                    blurredResult = _cachedProcessedBlurred;
                    return;
                }
            }
            else
            {
                _cachedSourceBmp = source;
                _cachedTintColor = tintColor;
                _cachedTintMilli = tintMilli;
                _cachedProcessedSharp = null;
                _cachedProcessedBlurred = null;
                _cachedBlurRadius = -1;
            }

            if (_cachedProcessedSharp is null)
            {
                _cachedProcessedSharp = tintMilli <= 10
                    ? source
                    : CreateTintedBitmap(source, tintColor, tintAmount, maxDim: 960);
            }
            sharpResult = _cachedProcessedSharp;

            if (blurRadius == 0)
            {
                blurredResult = null;
                return;
            }

            blurredResult = CreateBlurredAndTintedBitmap(source, blurRadius, tintColor, tintAmount);
            _cachedBlurRadius = blurRadius;
            _cachedProcessedBlurred = blurredResult;
        }
    }

    private static BitmapSource CreateTintedBitmap(BitmapSource source, Color tintColor, double tintAmount, int maxDim)
    {
        var (pixels, w, h) = ExtractBgraBuffer(source, maxDim);
        ApplyDuotoneTintInPlace(pixels, w * h, tintColor, tintAmount);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource CreateBlurredAndTintedBitmap(BitmapSource source, int radius, Color tintColor, double tintAmount)
    {
        var (pixels, w, h) = ExtractBgraBuffer(source, maxDim: 240);
        if (tintAmount > 0.01)
            ApplyDuotoneTintInPlace(pixels, w * h, tintColor, tintAmount);
        BoxBlur3PassClampBgra(pixels, w, h, radius);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static (byte[] Pixels, int Width, int Height) ExtractBgraBuffer(BitmapSource source, int maxDim)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        BitmapSource scaled = source;
        if (longest > maxDim && maxDim > 0)
        {
            var ratio = (double)maxDim / longest;
            scaled = new TransformedBitmap(source, new ScaleTransform(ratio, ratio));
        }
        var bgra = scaled.Format == PixelFormats.Bgra32
            ? scaled
            : new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var w = Math.Max(1, bgra.PixelWidth);
        var h = Math.Max(1, bgra.PixelHeight);
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        return (pixels, w, h);
    }

    private static void ApplyDuotoneTintInPlace(byte[] bgra, int pixelCount, Color tintColor, double tintAmount)
    {
        var shadow = ClassicLyricsPalette.BlendColor(tintColor, Colors.Black, 0.62);
        var highlight = ClassicLyricsPalette.BlendColor(tintColor, Colors.White, 0.24);
        var mix = Math.Clamp(tintAmount, 0.0, 1.0);

        for (var i = 0; i < pixelCount; i++)
        {
            var idx = i * 4;
            double b = bgra[idx];
            double g = bgra[idx + 1];
            double r = bgra[idx + 2];
            var lum = (0.114 * b + 0.587 * g + 0.299 * r) / 255.0;
            lum = Math.Clamp((lum - 0.5) * 1.16 + 0.5, 0.0, 1.0);

            var tb = shadow.B + lum * (highlight.B - shadow.B);
            var tg = shadow.G + lum * (highlight.G - shadow.G);
            var tr = shadow.R + lum * (highlight.R - shadow.R);

            bgra[idx] = (byte)Math.Clamp(Math.Round(b + mix * (tb - b)), 0, 255);
            bgra[idx + 1] = (byte)Math.Clamp(Math.Round(g + mix * (tg - g)), 0, 255);
            bgra[idx + 2] = (byte)Math.Clamp(Math.Round(r + mix * (tr - r)), 0, 255);
            bgra[idx + 3] = 255;
        }
    }

    private static void BoxBlur3PassClampBgra(byte[] data, int w, int h, int radius)
    {
        if (w <= 1 || h <= 1 || radius <= 0) return;

        var temp = new byte[data.Length];
        var kernel = radius * 2 + 1;
        var halfK = kernel / 2;

        for (var pass = 0; pass < 3; pass++)
        {
            // Horizontal: data -> temp
            for (var y = 0; y < h; y++)
            {
                var rowOff = y * w * 4;
                int sumB = 0, sumG = 0, sumR = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sx = rowOff + Math.Clamp(k, 0, w - 1) * 4;
                    sumB += data[sx];
                    sumG += data[sx + 1];
                    sumR += data[sx + 2];
                }

                for (var x = 0; x < w; x++)
                {
                    var dst = rowOff + x * 4;
                    temp[dst] = (byte)((sumB + halfK) / kernel);
                    temp[dst + 1] = (byte)((sumG + halfK) / kernel);
                    temp[dst + 2] = (byte)((sumR + halfK) / kernel);
                    temp[dst + 3] = 255;

                    var outIdx = rowOff + Math.Clamp(x - radius, 0, w - 1) * 4;
                    var inIdx = rowOff + Math.Clamp(x + radius + 1, 0, w - 1) * 4;
                    sumB += data[inIdx] - data[outIdx];
                    sumG += data[inIdx + 1] - data[outIdx + 1];
                    sumR += data[inIdx + 2] - data[outIdx + 2];
                }
            }

            // Vertical: temp -> data
            var stride = w * 4;
            for (var x = 0; x < w; x++)
            {
                var colOff = x * 4;
                int sumB = 0, sumG = 0, sumR = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sy = Math.Clamp(k, 0, h - 1) * stride + colOff;
                    sumB += temp[sy];
                    sumG += temp[sy + 1];
                    sumR += temp[sy + 2];
                }

                for (var y = 0; y < h; y++)
                {
                    var dst = y * stride + colOff;
                    data[dst] = (byte)((sumB + halfK) / kernel);
                    data[dst + 1] = (byte)((sumG + halfK) / kernel);
                    data[dst + 2] = (byte)((sumR + halfK) / kernel);
                    data[dst + 3] = 255;

                    var outIdx = Math.Clamp(y - radius, 0, h - 1) * stride + colOff;
                    var inIdx = Math.Clamp(y + radius + 1, 0, h - 1) * stride + colOff;
                    sumB += temp[inIdx] - temp[outIdx];
                    sumG += temp[inIdx + 1] - temp[outIdx + 1];
                    sumR += temp[inIdx + 2] - temp[outIdx + 2];
                }
            }
        }
    }

    private static Border CreateCenterCropImage(BitmapSource source, double width, double height)
    {
        var img = new Image
        {
            Source = source,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return new Border
        {
            Width = width,
            Height = height,
            ClipToBounds = true,
            Child = img,
        };
    }

    private static Border CreateCoverThumbnail(BitmapSource? cover, double size, double radius)
    {
        var host = new Grid
        {
            Width = size,
            Height = size,
        };

        if (cover is { PixelWidth: > 0, PixelHeight: > 0 })
        {
            var img = new Image
            {
                Source = cover,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            host.Children.Add(img);
        }
        else
        {
            // 与 ClassicLyricsWindow 现有无封面占位保持一致（#282828 底色 + #B3B3B3 居中音符）
            host.Children.Add(new TextBlock
            {
                Text = "♫",
                FontFamily = SymbolFont,
                FontSize = Math.Round(size * 0.44),
                Foreground = FrozenBrush(Color.FromRgb(179, 179, 179)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return new Border
        {
            Width = size,
            Height = size,
            Background = FrozenBrush(Color.FromRgb(40, 40, 40)),
            CornerRadius = new CornerRadius(radius),
            Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius),
            Child = host,
        };
    }

    private static StackPanel BuildSpotifyBrand(Color badgeCircleColor, Color waveCutColor, Color textColor, double s)
    {
        const double r = 24;
        const double d = r * 2;
        var dOut = d * s;
        var waveBrush = FrozenBrush(waveCutColor);
        var textBrush = FrozenBrush(textColor);

        var waveTransform = new TransformGroup();
        waveTransform.Children.Add(new RotateTransform(8.5, r, r));
        waveTransform.Children.Add(new ScaleTransform(s, s));

        var badgeCanvas = new Canvas
        {
            Width = dOut,
            Height = dOut,
            RenderTransform = waveTransform,
        };
        badgeCanvas.Children.Add(new Path
        {
            Data = SpotifyWave1,
            Stroke = waveBrush,
            StrokeThickness = r * 0.165,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        badgeCanvas.Children.Add(new Path
        {
            Data = SpotifyWave2,
            Stroke = waveBrush,
            StrokeThickness = r * 0.135,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        badgeCanvas.Children.Add(new Path
        {
            Data = SpotifyWave3,
            Stroke = waveBrush,
            StrokeThickness = r * 0.112,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

        var circleGrid = new Grid { Width = dOut, Height = dOut };
        circleGrid.Children.Add(new Ellipse { Width = dOut, Height = dOut, Fill = FrozenBrush(badgeCircleColor) });
        circleGrid.Children.Add(badgeCanvas);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(circleGrid);
        row.Children.Add(new TextBlock
        {
            Text = "Spotify",
            FontFamily = ClassicLyricsFonts.Body,
            FontWeight = FontWeights.Bold,
            FontSize = 40 * s,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14 * s, -2 * s, 0, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = "®",
            FontFamily = ClassicLyricsFonts.Body,
            FontWeight = FontWeights.Bold,
            FontSize = 13 * s,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2 * s, 6 * s, 0, 0),
        });
        return row;
    }

    private static StackPanel BuildAppleMusicBrand(Color inkColor, double s)
    {
        var glyphH = 29 * s;
        var scale = glyphH / 28.0;
        var inkBrush = FrozenBrush(inkColor);

        var appleCanvas = new Canvas
        {
            Width = 24 * scale,
            Height = glyphH,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconGroup = new Canvas
        {
            RenderTransform = new ScaleTransform(scale, scale),
        };
        iconGroup.Children.Add(new Path { Data = AppleLeafGeometry, Fill = inkBrush });
        iconGroup.Children.Add(new Path { Data = AppleBodyGeometry, Fill = inkBrush });
        appleCanvas.Children.Add(iconGroup);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(appleCanvas);
        row.Children.Add(new TextBlock
        {
            Text = "music",
            FontFamily = ClassicLyricsFonts.Body,
            FontWeight = FontWeights.SemiBold,
            FontSize = 35 * s,
            Foreground = inkBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5 * s, -4 * s, 0, 0),
        });
        return row;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
