using Android.Content;
using Android.Graphics;
using Android.Text;
using Path = global::Android.Graphics.Path;

namespace Musebase.Android.UI;

internal enum LyricShareStyle
{
    Spotify,
    AppleMusic,
}

internal readonly record struct ShareLyricLine(string Original, string? Translation);

internal readonly record struct SharePalettePreset(string Label, Color BaseColor, Color GradientEndColor);

internal sealed record ShareCardOptions(
    LyricShareStyle Style,
    Color BaseColor,
    bool ShowTranslation,
    string? TrackTitle,
    string? ArtistName,
    Bitmap? CoverBitmap,
    IReadOnlyList<ShareLyricLine> Lines,
    Bitmap? CustomBackgroundBitmap,
    ShareBackgroundMode BackgroundMode,
    Color GradientEndColor,
    ShareCoverEffectSettings CoverEffect,
    ShareLyricsFont LyricsFont,
    float FontScale)
{
    public Bitmap? BackgroundImage => BackgroundMode switch
    {
        ShareBackgroundMode.AlbumCover => CoverBitmap,
        ShareBackgroundMode.CustomImage => CustomBackgroundBitmap,
        _ => null,
    };
}

/// <summary>
/// 高分辨率歌词分享卡片位图渲染器，同时供预览窗与导出/分享使用：
/// 支持 Spotify 样式、Apple Music 样式（四周等宽外衬）与自定义背景图。
/// </summary>
internal static class LyricShareCardRenderer
{
    public const int MinFontPercent = 75;
    public const int MaxFontPercent = 135;
    private const float ExportScale = 2f;

    public static IReadOnlyList<SharePalettePreset> BuildPresets(Color coverColor)
    {
        var dynamicColor = LyricsPalette.ShareCardColor(coverColor);
        return
        [
            new("封面取色", dynamicColor, LyricsPalette.BlendColor(dynamicColor, Color.Black, 0.28)),
            new("松林幽绿", Color.Rgb(24, 106, 77), Color.Rgb(11, 61, 44)),
            new("雾霭靛蓝", Color.Rgb(42, 71, 110), Color.Rgb(20, 36, 59)),
            new("暮光暗紫", Color.Rgb(82, 62, 112), Color.Rgb(40, 29, 59)),
            new("赤陶暖阳", Color.Rgb(198, 76, 50), Color.Rgb(118, 34, 22)),
            new("奶油燕麦", Color.Rgb(237, 228, 211), Color.Rgb(210, 195, 170)),
            new("薄荷苏打", Color.Rgb(198, 240, 219), Color.Rgb(145, 212, 182)),
            new("曜石深炭", Color.Rgb(42, 42, 46), Color.Rgb(18, 18, 20)),
        ];
    }

    public static Bitmap Render(Context context, ShareCardOptions options, bool forPreview = false) =>
        options.Style == LyricShareStyle.AppleMusic
            ? RenderAppleMusicCard(context, options, forPreview ? 0.7f : ExportScale)
            : RenderSpotifyCard(context, options, forPreview ? 0.7f : ExportScale);

    public static Bitmap RenderClipboard(Context context, ShareCardOptions options, bool keepBackdrop) =>
        RenderExport(context, options, keepBackdrop);

    public static Bitmap RenderExport(Context context, ShareCardOptions options, bool keepBackdrop)
    {
        using var card = Render(context, options);
        var pad = keepBackdrop ? (int)(72 * ExportScale) : 0;
        var width = card.Width + pad * 2;
        var height = card.Height + pad * 2;
        var result = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(result);
        using var rect = new RectF(pad, pad, pad + card.Width, pad + card.Height);
        var radius = (options.Style == LyricShareStyle.Spotify ? 42 : 34) * ExportScale;

        if (keepBackdrop)
        {
            using var bounds = new RectF(0, 0, width, height);
            LyricShareBackground.DrawGradient(canvas, bounds,
                [Color.Rgb(10, 10, 12), LyricsPalette.BlendColor(options.BaseColor, Color.Black, 0.42),
                    LyricsPalette.BlendColor(options.BaseColor, Color.Black, 0.12)],
                [0f, 0.46f, 1f]);
            using var shadow = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(148, 0, 0, 0) };
            using var filter = new BlurMaskFilter(42f * ExportScale, BlurMaskFilter.Blur.Normal!);
            shadow.SetMaskFilter(filter);
            var shadowOffsetY = 14f * ExportScale;
            canvas.DrawRoundRect(
                rect.Left,
                rect.Top + shadowOffsetY,
                rect.Right,
                rect.Bottom + shadowOffsetY,
                radius,
                radius,
                shadow);
        }

        using var paint = new Paint(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
        using var shader = new BitmapShader(card, Shader.TileMode.Clamp!, Shader.TileMode.Clamp!);
        using var matrix = new Matrix();
        matrix.SetTranslate(pad, pad);
        shader.SetLocalMatrix(matrix);
        paint.SetShader(shader);
        canvas.DrawRoundRect(rect, radius, radius, paint);
        return result;
    }

    private static Bitmap RenderSpotifyCard(Context ctx, ShareCardOptions opt, float scale)
    {
        const int size = 1080;
        const float padX = 68f;
        const float padTop = 64f;
        const float coverSize = 106f;

        var bitmap = Bitmap.CreateBitmap((int)(size * scale), (int)(size * scale), Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        canvas.Scale(scale, scale);
        using var bounds = new RectF(0, 0, size, size);
        var image = opt.BackgroundImage;
        var badgeBottomBg = LyricShareBackground.Draw(canvas, bounds, opt, image);
        var isLight = image is null && LyricsPalette.RelativeLuminance(opt.BaseColor) > 0.36;
        var ink = isLight ? Color.Rgb(20, 23, 22) : Color.White;
        var subdued = isLight ? Color.Rgb(49, 53, 50) : Color.Argb(214, 255, 255, 255);
        var titleTf = UiHelpers.ResolveFontWeight(ctx, UiHelpers.FontWeightBold, useTitleFont: true);
        var boldTf = UiHelpers.ResolveFontWeight(ctx, UiHelpers.FontWeightBold);
        var regTf = UiHelpers.ResolveFontWeight(ctx, UiHelpers.FontWeightRegular);
        var (lyricTf, translationTf) = ResolveLyricsFonts(ctx, opt.LyricsFont, titleTf, regTf);

        // 1. 顶部专辑封面 + 歌曲名 + 「歌曲 · 歌手」
        using var coverRect = new RectF(padX, padTop, padX + coverSize, padTop + coverSize);
        DrawCoverThumbnail(canvas, opt.CoverBitmap, coverRect, radius: 14f, titleTf);

        var metaX = padX + coverSize + 24f;
        var metaW = size - padX - metaX;
        var titleText = string.IsNullOrWhiteSpace(opt.TrackTitle) ? "未知歌曲" : opt.TrackTitle!.Trim();
        var subText = string.IsNullOrWhiteSpace(opt.ArtistName) ? "歌曲" : $"歌曲 · {opt.ArtistName!.Trim()}";

        using var titlePaint = CreateTextPaint(boldTf, 34f, ink, shadow: image is not null);
        using var subPaint = CreateTextPaint(regTf, 27f, subdued, shadow: image is not null);
        using var titleLayout = BuildSingleLineLayout(titleText, titlePaint, metaW);
        using var subLayout = BuildSingleLineLayout(subText, subPaint, metaW);
        var metaTotalH = titleLayout.Height + 6f + subLayout.Height;
        var metaTop = padTop + (coverSize - metaTotalH) / 2f;

        DrawLayoutAt(canvas, titleLayout, metaX, metaTop);
        DrawLayoutAt(canvas, subLayout, metaX, metaTop + titleLayout.Height + 6f);

        // 2. 底部左下角 Spotify 样式标识
        DrawSpotifyBrand(canvas, padX, size - 78f, boldTf, ink, badgeBottomBg, image is not null);

        // 3. 中部自适应歌词块
        const float lyricsTopBound = 216f;
        const float lyricsBottomBound = 928f;
        var lyricsMaxW = size - padX * 2f;
        var maxAvailH = lyricsBottomBound - lyricsTopBound;

        var (fontSize, totalH, lineLimit) = FitLyricsMetrics(
            opt.Lines,
            opt.ShowTranslation,
            lyricTf,
            translationTf,
            lyricsMaxW,
            maxAvailH,
            maxFontSp: (opt.Lines.Count <= 2 ? 70f : 62f) * opt.FontScale,
            lineSpacingMult: 1.12f);

        var startY = lyricsTopBound + MathF.Max(0f, (maxAvailH - totalH) / 2f);
        DrawLyricsBlock(
            canvas,
            opt.Lines,
            opt.ShowTranslation,
            lyricTf,
            translationTf,
            fontSize,
            lineSpacingMult: 1.12f,
            x: padX,
            y: startY,
            maxW: lyricsMaxW,
            mainColor: ink,
            subColor: subdued,
            lineLimit: lineLimit,
            shadow: image is not null);

        return bitmap;
    }

    private static Bitmap RenderAppleMusicCard(Context ctx, ShareCardOptions opt, float scale)
    {
        // 四周外边距严格等宽（上 = 下 = 左 = 右 = outerMargin）
        const int canvasW = 1200;
        const float outerMargin = 68f;
        const float cardW = canvasW - outerMargin * 2f;
        const float cardPadX = 60f;
        const float textW = cardW - cardPadX * 2f;
        const float cardRadius = 34f;
        const float upperPadY = 54f;
        const float lowerH = 222f;

        var lyricRegTf = UiHelpers.ResolveSystemFontWeight(UiHelpers.FontWeightRegular);
        var metaMedTf = UiHelpers.ResolveSystemFontWeight(UiHelpers.FontWeightMedium);
        var brandTf = UiHelpers.ResolveFontWeight(ctx, UiHelpers.FontWeightSemiBold);
        var (mainTf, translationTf) = ResolveLyricsFonts(ctx, opt.LyricsFont, lyricRegTf, lyricRegTf);

        var (fontSize, lyricsH, lineLimit) = FitLyricsMetrics(
            opt.Lines,
            opt.ShowTranslation,
            mainTf,
            translationTf,
            textW,
            maxAvailH: 760f,
            maxFontSp: (opt.Lines.Count <= 3 ? 58f : 52f) * opt.FontScale,
            lineSpacingMult: 1.16f);

        var upperH = MathF.Max(236f, lyricsH + upperPadY * 2f);
        var cardH = upperH + lowerH;
        var canvasH = (int)MathF.Round(cardH + outerMargin * 2f);

        var bitmap = Bitmap.CreateBitmap((int)MathF.Ceiling(canvasW * scale), (int)MathF.Ceiling(canvasH * scale), Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        canvas.Scale(bitmap.Width / (float)canvasW, bitmap.Height / (float)canvasH);
        var image = opt.BackgroundImage;
        using var bounds = new RectF(0, 0, canvasW, canvasH);
        LyricShareBackground.Draw(canvas, bounds, opt, image);

        // 2. 卡片柔和投影
        var cardLeft = outerMargin;
        var cardTop = outerMargin;
        using var cardRect = new RectF(cardLeft, cardTop, cardLeft + cardW, cardTop + cardH);

        using (var shadowPaint = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(72, 0, 0, 0) })
        {
            using var blur = new BlurMaskFilter(20f, BlurMaskFilter.Blur.Normal!);
            shadowPaint.SetMaskFilter(blur);
            canvas.DrawRoundRect(cardRect.Left, cardRect.Top + 6f, cardRect.Right, cardRect.Bottom + 6f, cardRadius, cardRadius, shadowPaint);
        }

        // 3. 圆角双色卡片主体
        using var clipPath = new Path();
        clipPath.AddRoundRect(cardRect, cardRadius, cardRadius, Path.Direction.Cw!);

        canvas.Save();
        canvas.ClipPath(clipPath);

        var isLight = image is null && LyricsPalette.RelativeLuminance(opt.BaseColor) > 0.36;
        var ink = isLight ? Color.Rgb(20, 23, 22) : Color.White;
        var secondaryInk = isLight ? Color.Rgb(55, 58, 55) : Color.Argb(209, 255, 255, 255);
        var panelBase = image is null ? opt.BaseColor : LyricsPalette.ShareCardColor(opt.BaseColor);
        var upperColor = LyricsPalette.BlendColor(panelBase, isLight ? Color.White : Color.Black, isLight ? 0.42 : 0.13);
        var lowerColor = LyricsPalette.BlendColor(panelBase, isLight ? Color.White : Color.Black, isLight ? 0.18 : 0.35);

        using (var panelPaint = new Paint(PaintFlags.AntiAlias))
        {
            panelPaint.Color = Color.Argb(image is null ? 255 : 172, upperColor.R, upperColor.G, upperColor.B);
            canvas.DrawRect(cardLeft, cardTop, cardLeft + cardW, cardTop + upperH, panelPaint);

            panelPaint.Color = Color.Argb(image is null ? 255 : 222, lowerColor.R, lowerColor.G, lowerColor.B);
            canvas.DrawRect(cardLeft, cardTop + upperH, cardLeft + cardW, cardTop + cardH, panelPaint);
        }

        // 上半区歌词（采用更清透的系统 Regular 字重，保证中英文观感统一）
        var lyricsStartY = cardTop + (upperH - lyricsH) / 2f;
        DrawLyricsBlock(
            canvas,
            opt.Lines,
            opt.ShowTranslation,
            mainTf,
            translationTf,
            fontSize,
            lineSpacingMult: 1.16f,
            x: cardLeft + cardPadX,
            y: lyricsStartY,
            maxW: textW,
            mainColor: ink,
            subColor: secondaryInk,
            lineLimit: lineLimit,
            shadow: image is not null);

        // 下半区专辑封面 + 歌名（Medium） + 歌手（Regular） + Apple Music 标识
        const float appleCoverSize = 142f;
        var lowerTop = cardTop + upperH;
        using var appleCoverRect = new RectF(
            cardLeft + cardPadX,
            lowerTop + (lowerH - appleCoverSize) / 2f,
            cardLeft + cardPadX + appleCoverSize,
            lowerTop + (lowerH + appleCoverSize) / 2f);
        DrawCoverThumbnail(canvas, opt.CoverBitmap, appleCoverRect, radius: 16f, metaMedTf);

        var infoX = appleCoverRect.Right + 30f;
        var infoW = cardRect.Right - cardPadX - infoX;
        var titleStr = string.IsNullOrWhiteSpace(opt.TrackTitle) ? "未知歌曲" : opt.TrackTitle!.Trim();
        var artistStr = string.IsNullOrWhiteSpace(opt.ArtistName) ? "" : opt.ArtistName!.Trim();

        using var aTitlePaint = CreateTextPaint(metaMedTf, 35f, ink);
        using var aTitleLayout = BuildSingleLineLayout(titleStr, aTitlePaint, infoW);
        using var aArtistPaint = CreateTextPaint(lyricRegTf, 32f, secondaryInk);
        using var aArtistLayout = artistStr.Length > 0 ? BuildSingleLineLayout(artistStr, aArtistPaint, infoW) : null;

        const float brandHeight = 32f;
        const float gap1 = 6f;
        const float gap2 = 14f;
        var stackH = aTitleLayout.Height + (aArtistLayout is not null ? gap1 + aArtistLayout.Height : 0f) + gap2 + brandHeight;
        var stackY = lowerTop + (lowerH - stackH) / 2f;

        DrawLayoutAt(canvas, aTitleLayout, infoX, stackY);
        var curY = stackY + aTitleLayout.Height;
        if (aArtistLayout is not null)
        {
            curY += gap1;
            DrawLayoutAt(canvas, aArtistLayout, infoX, curY);
            curY += aArtistLayout.Height;
        }
        curY += gap2;
        DrawAppleMusicBrand(canvas, infoX, curY, brandTf, secondaryInk);

        canvas.Restore();
        return bitmap;
    }

    private static (Typeface? Original, Typeface? Translation) ResolveLyricsFonts(
        Context context, ShareLyricsFont font, Typeface? defaultMain, Typeface? defaultTranslation) => font switch
    {
        ShareLyricsFont.SansSerif => (Typeface.SansSerif, Typeface.SansSerif),
        ShareLyricsFont.Serif => (Typeface.Serif, Typeface.Serif),
        ShareLyricsFont.Monospace => (Typeface.Monospace, Typeface.Monospace),
        ShareLyricsFont.Spotify => (UiHelpers.ResolveFontWeight(context, UiHelpers.FontWeightBold, useTitleFont: true),
            UiHelpers.ResolveFontWeight(context, UiHelpers.FontWeightRegular)),
        _ => (defaultMain, defaultTranslation),
    };

    private static (float FontSize, float TotalHeight, int LineLimit) FitLyricsMetrics(
        IReadOnlyList<ShareLyricLine> lines,
        bool showTranslation,
        Typeface? mainTf,
        Typeface? subTf,
        float maxW,
        float maxAvailH,
        float maxFontSp,
        float lineSpacingMult)
    {
        const float minSize = 24f;
        for (var size = maxFontSp; ; size = Math.Max(minSize, size - 2f))
        {
            var height = MeasureLyricsHeight(lines, showTranslation, mainTf, subTf, size, lineSpacingMult, maxW, int.MaxValue);
            if (height <= maxAvailH)
                return (size, height, int.MaxValue);
            if (size <= minSize)
                break;
        }

        // 对超长单行保留每条选中歌词的位置，并在卡片可容纳的行数后显示省略号。
        for (var limit = 16; ; limit--)
        {
            var height = MeasureLyricsHeight(lines, showTranslation, mainTf, subTf, minSize, lineSpacingMult, maxW, limit);
            if (height <= maxAvailH || limit == 1)
                return (minSize, height, limit);
        }
    }

    private static float MeasureLyricsHeight(
        IReadOnlyList<ShareLyricLine> lines,
        bool showTranslation,
        Typeface? mainTf,
        Typeface? subTf,
        float mainSize,
        float lineSpacingMult,
        float maxW,
        int lineLimit)
    {
        var lineGap = MathF.Round(mainSize * 0.24f);
        var trGap = MathF.Round(mainSize * 0.10f);
        using var mainPaint = CreateTextPaint(mainTf, mainSize, Color.White);
        var subSize = MathF.Max(18f, MathF.Round(mainSize * 0.54f));
        using var subPaint = CreateTextPaint(subTf, subSize, Color.White);

        float total = 0f;
        for (var i = 0; i < lines.Count; i++)
        {
            var item = lines[i];
            var text = string.IsNullOrWhiteSpace(item.Original) ? "♪" : item.Original;
            using var mainLayout = BuildMultilineLayout(text, mainPaint, maxW, lineSpacingMult, lineLimit);
            total += mainLayout.Height;

            if (showTranslation && !string.IsNullOrWhiteSpace(item.Translation))
            {
                using var trLayout = BuildMultilineLayout(item.Translation!, subPaint, maxW, 1.15f, lineLimit);
                total += trGap + trLayout.Height;
            }

            if (i < lines.Count - 1)
                total += lineGap;
        }
        return total;
    }

    private static void DrawLyricsBlock(
        Canvas canvas,
        IReadOnlyList<ShareLyricLine> lines,
        bool showTranslation,
        Typeface? mainTf,
        Typeface? subTf,
        float mainSize,
        float lineSpacingMult,
        float x,
        float y,
        float maxW,
        Color mainColor,
        Color subColor,
        int lineLimit,
        bool shadow)
    {
        var lineGap = MathF.Round(mainSize * 0.24f);
        var trGap = MathF.Round(mainSize * 0.10f);
        using var mainPaint = CreateTextPaint(mainTf, mainSize, mainColor, shadow);
        var subSize = MathF.Max(18f, MathF.Round(mainSize * 0.54f));
        using var subPaint = CreateTextPaint(subTf, subSize, subColor, shadow);

        var curY = y;
        for (var i = 0; i < lines.Count; i++)
        {
            var item = lines[i];
            var text = string.IsNullOrWhiteSpace(item.Original) ? "♪" : item.Original;
            using var mainLayout = BuildMultilineLayout(text, mainPaint, maxW, lineSpacingMult, lineLimit);
            DrawLayoutAt(canvas, mainLayout, x, curY);
            curY += mainLayout.Height;

            if (showTranslation && !string.IsNullOrWhiteSpace(item.Translation))
            {
                curY += trGap;
                using var trLayout = BuildMultilineLayout(item.Translation!, subPaint, maxW, 1.15f, lineLimit);
                DrawLayoutAt(canvas, trLayout, x, curY);
                curY += trLayout.Height;
            }

            curY += lineGap;
        }
    }

    private static void DrawCoverThumbnail(Canvas canvas, Bitmap? cover, RectF rect, float radius, Typeface? noteTf)
    {
        using var clip = new Path();
        clip.AddRoundRect(rect, radius, radius, Path.Direction.Cw!);

        canvas.Save();
        canvas.ClipPath(clip);

        using (var fallbackBg = new Paint(PaintFlags.AntiAlias) { Color = LyricsPalette.CoverFrameBackground })
            canvas.DrawRect(rect, fallbackBg);

        if (cover is { IsRecycled: false } && cover.Width > 0 && cover.Height > 0)
        {
            LyricShareBackground.DrawCenterCrop(canvas, cover, rect);
        }
        else
        {
            using var notePaint = new Paint(PaintFlags.AntiAlias)
            {
                Color = LyricsPalette.MutedGray,
                TextSize = rect.Height() * 0.44f,
                TextAlign = Paint.Align.Center!,
            };
            if (noteTf is not null) notePaint.SetTypeface(noteTf);
            var fm = notePaint.GetFontMetrics()!;
            var baseY = rect.CenterY() - (fm.Ascent + fm.Descent) / 2f;
            canvas.DrawText("♫", rect.CenterX(), baseY, notePaint);
        }

        canvas.Restore();
    }

    private static void DrawSpotifyBrand(Canvas canvas, float leftX, float centerY, Typeface? boldTf, Color ink, Color waveCutColor, bool shadow)
    {
        const float r = 24f;
        var cx = leftX + r;

        using (var circlePaint = new Paint(PaintFlags.AntiAlias) { Color = ink })
        {
            if (shadow) circlePaint.SetShadowLayer(4f, 0f, 1.5f, Color.Black);
            circlePaint.SetStyle(Paint.Style.Fill);
            canvas.DrawCircle(cx, centerY, r, circlePaint);
        }

        canvas.Save();
        canvas.Translate(cx, centerY);
        canvas.Rotate(8.5f);
        using (var wavePaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = waveCutColor,
            StrokeCap = Paint.Cap.Round!,
        })
        {
            wavePaint.SetStyle(Paint.Style.Stroke);
            using var arcPath = new Path();

            wavePaint.StrokeWidth = r * 0.165f;
            arcPath.MoveTo(-r * 0.62f, -r * 0.22f);
            arcPath.QuadTo(0f, -r * 0.48f, r * 0.62f, -r * 0.22f);
            canvas.DrawPath(arcPath, wavePaint);

            arcPath.Reset();
            wavePaint.StrokeWidth = r * 0.135f;
            arcPath.MoveTo(-r * 0.50f, r * 0.10f);
            arcPath.QuadTo(0f, -r * 0.11f, r * 0.50f, r * 0.10f);
            canvas.DrawPath(arcPath, wavePaint);

            arcPath.Reset();
            wavePaint.StrokeWidth = r * 0.112f;
            arcPath.MoveTo(-r * 0.39f, r * 0.39f);
            arcPath.QuadTo(0f, r * 0.23f, r * 0.39f, r * 0.39f);
            canvas.DrawPath(arcPath, wavePaint);
        }
        canvas.Restore();

        using var wordPaint = CreateTextPaint(boldTf, 40f, ink, shadow);
        wordPaint.LetterSpacing = -0.02f;
        using var fm = wordPaint.GetFontMetrics()!;
        var baseline = centerY - (fm.Ascent + fm.Descent) / 2f;
        var wordX = cx + r + 14f;
        canvas.DrawText("Spotify", wordX, baseline, wordPaint);

        var wordW = wordPaint.MeasureText("Spotify");
        wordPaint.TextSize = 13f;
        canvas.DrawText("®", wordX + wordW + 2f, baseline + fm.Ascent * 0.62f, wordPaint);
    }

    private static void DrawAppleMusicBrand(Canvas canvas, float leftX, float topY, Typeface? boldTf, Color inkColor)
    {
        const float glyphH = 29f;
        var scale = glyphH / 28f;

        canvas.Save();
        canvas.Translate(leftX, topY + 1f);
        canvas.Scale(scale, scale);

        using (var applePaint = new Paint(PaintFlags.AntiAlias) { Color = inkColor })
        {
            applePaint.SetStyle(Paint.Style.Fill);
            using var leaf = new Path();
            leaf.MoveTo(12.2f, 6.2f);
            leaf.CubicTo(12.0f, 2.8f, 14.4f, 0.4f, 17.2f, 0.2f);
            leaf.CubicTo(17.3f, 3.4f, 14.9f, 6.0f, 12.2f, 6.2f);
            leaf.Close();
            canvas.DrawPath(leaf, applePaint);

            using var body = new Path();
            body.MoveTo(12.0f, 8.2f);
            body.CubicTo(14.2f, 6.4f, 18.2f, 6.4f, 20.4f, 8.8f);
            body.CubicTo(17.8f, 10.6f, 17.5f, 16.4f, 21.3f, 18.6f);
            body.CubicTo(20.2f, 22.8f, 17.6f, 27.2f, 15.2f, 27.2f);
            body.CubicTo(13.8f, 27.2f, 13.2f, 26.3f, 12.0f, 26.3f);
            body.CubicTo(10.8f, 26.3f, 10.2f, 27.2f, 8.8f, 27.2f);
            body.CubicTo(6.2f, 27.2f, 2.2f, 21.8f, 2.2f, 15.0f);
            body.CubicTo(2.2f, 9.6f, 6.0f, 6.4f, 9.2f, 6.4f);
            body.CubicTo(10.6f, 6.4f, 11.4f, 8.2f, 12.0f, 8.2f);
            body.Close();
            canvas.DrawPath(body, applePaint);
        }
        canvas.Restore();

        using var musicPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = inkColor,
            TextSize = 35f,
            LetterSpacing = -0.025f,
        };
        if (boldTf is not null) musicPaint.SetTypeface(boldTf);
        var baseline = topY + glyphH + 0.5f;
        canvas.DrawText("music", leftX + 24f * scale + 5f, baseline, musicPaint);
    }

    private static TextPaint CreateTextPaint(Typeface? typeface, float textSizePx, Color color, bool shadow = false)
    {
        var tp = new TextPaint(PaintFlags.AntiAlias | PaintFlags.SubpixelText)
        {
            Color = color,
            TextSize = textSizePx,
        };
        if (typeface is not null) tp.SetTypeface(typeface);
        if (shadow) tp.SetShadowLayer(4f, 0f, 1.5f, Color.Black);
        return tp;
    }

    private static StaticLayout BuildSingleLineLayout(string text, TextPaint paint, float widthPx)
    {
        var w = Math.Max(1, (int)MathF.Floor(widthPx));
        var builder = StaticLayout.Builder.Obtain(text, 0, text.Length, paint, w)!;
        return builder
            .SetAlignment(global::Android.Text.Layout.Alignment.AlignNormal!)
            .SetMaxLines(1)
            .SetEllipsize(TextUtils.TruncateAt.End!)
            .SetIncludePad(true)
            .Build()!;
    }

    private static StaticLayout BuildMultilineLayout(string text, TextPaint paint, float widthPx, float spacingMult, int lineLimit)
    {
        var w = Math.Max(1, (int)MathF.Floor(widthPx));
        var builder = StaticLayout.Builder.Obtain(text, 0, text.Length, paint, w)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            builder.SetUseLineSpacingFromFallbacks(true);
        if (lineLimit != int.MaxValue)
            builder.SetMaxLines(lineLimit).SetEllipsize(TextUtils.TruncateAt.End!);
        return builder
            .SetAlignment(global::Android.Text.Layout.Alignment.AlignNormal!)
            .SetLineSpacing(0f, spacingMult)
            .SetIncludePad(true)
            .Build()!;
    }

    private static void DrawLayoutAt(Canvas canvas, StaticLayout layout, float x, float y)
    {
        canvas.Save();
        canvas.Translate(x, y);
        layout.Draw(canvas);
        canvas.Restore();
    }

}
