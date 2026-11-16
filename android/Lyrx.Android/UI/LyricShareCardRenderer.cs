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

internal readonly record struct SharePalettePreset(string Id, string Label, Color BaseColor, bool IsDynamic = false);

internal sealed record ShareCardOptions(
    LyricShareStyle Style,
    Color BaseColor,
    bool ShowTranslation,
    string? TrackTitle,
    string? ArtistName,
    Bitmap? CoverBitmap,
    IReadOnlyList<ShareLyricLine> Lines,
    Bitmap? CustomBackgroundBitmap = null);

/// <summary>
/// 高分辨率歌词分享卡片位图渲染器，同时供预览窗与导出/分享使用：
/// 支持 Spotify 样式、Apple Music 样式（四周等宽外衬）与自定义背景图。
/// </summary>
internal static class LyricShareCardRenderer
{
    public static IReadOnlyList<SharePalettePreset> BuildPresets(Color dynamicBaseColor) =>
    [
        new("dynamic", "封面取色", dynamicBaseColor, IsDynamic: true),
        new("forest-green", "经典深绿", Color.Rgb(27, 77, 52)),
        new("spotify-teal", "深海青灰", Color.Rgb(36, 61, 68)),
        new("apple-amber", "温暖琥珀", Color.Rgb(84, 68, 42)),
        new("ocean-blue", "午夜蓝", Color.Rgb(28, 56, 86)),
        new("burgundy", "酒红", Color.Rgb(79, 29, 40)),
        new("warm-gray", "高级暖灰", Color.Rgb(54, 54, 58)),
        new("charcoal", "极夜黑", Color.Rgb(24, 24, 26)),
    ];

    public static Bitmap Render(Context context, ShareCardOptions options) =>
        options.Style == LyricShareStyle.AppleMusic
            ? RenderAppleMusicCard(context, options)
            : RenderSpotifyCard(context, options);

    private static Bitmap RenderSpotifyCard(Context ctx, ShareCardOptions opt)
    {
        const int size = 1080;
        const float padX = 68f;
        const float padTop = 64f;
        const float coverSize = 106f;

        var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
        var canvas = new Canvas(bitmap);

        var hasCustomBg = opt.CustomBackgroundBitmap is { IsRecycled: false } customBg
            && customBg.Width > 0 && customBg.Height > 0;
        Color badgeBottomBg;

        if (hasCustomBg)
        {
            DrawBitmapCenterCrop(canvas, opt.CustomBackgroundBitmap!, new RectF(0f, 0f, size, size));
            using var scrimPaint = new Paint(PaintFlags.AntiAlias);
            scrimPaint.SetShader(new LinearGradient(
                0f, 0f, 0f, size,
                [Color.Argb(96, 0, 0, 0).ToArgb(), Color.Argb(148, 0, 0, 0).ToArgb()],
                null,
                Shader.TileMode.Clamp!));
            canvas.DrawRect(0f, 0f, size, size, scrimPaint);
            var (customDark, _) = LyricsPalette.FromCover(opt.CustomBackgroundBitmap);
            badgeBottomBg = LyricsPalette.BlendColor(customDark, Color.Black, 0.45);
        }
        else
        {
            var topBg = LyricsPalette.BlendColor(opt.BaseColor, Color.White, 0.08);
            var bottomBg = LyricsPalette.BlendColor(opt.BaseColor, Color.Black, 0.30);

            using var bgPaint = new Paint(PaintFlags.AntiAlias);
            bgPaint.SetShader(new LinearGradient(
                0f, 0f, size * 0.35f, size,
                [topBg.ToArgb(), bottomBg.ToArgb()],
                null,
                Shader.TileMode.Clamp!));
            canvas.DrawRect(0f, 0f, size, size, bgPaint);
            badgeBottomBg = LyricsPalette.BlendColor(topBg, bottomBg, 0.90);
        }

        var titleTf = UiHelpers.ResolveFontWeight(ctx, UiHelpers.FontWeightBold, useTitleFont: true);
        var boldTf = UiHelpers.ResolveFontWeight(ctx, UiHelpers.FontWeightBold);
        var regTf = UiHelpers.ResolveFontWeight(ctx, UiHelpers.FontWeightRegular);

        // 1. 顶部专辑封面 + 歌曲名 + 「歌曲 · 歌手」
        var coverRect = new RectF(padX, padTop, padX + coverSize, padTop + coverSize);
        DrawCoverThumbnail(canvas, opt.CoverBitmap, coverRect, radius: 14f, titleTf);

        var metaX = padX + coverSize + 24f;
        var metaW = size - padX - metaX;
        var titleText = string.IsNullOrWhiteSpace(opt.TrackTitle) ? "未知歌曲" : opt.TrackTitle!.Trim();
        var subText = string.IsNullOrWhiteSpace(opt.ArtistName) ? "歌曲" : $"歌曲 · {opt.ArtistName!.Trim()}";

        var titlePaint = CreateTextPaint(boldTf, 34f, Color.White);
        var subPaint = CreateTextPaint(regTf, 27f, Color.Argb(224, 255, 255, 255));
        var titleLayout = BuildSingleLineLayout(titleText, titlePaint, metaW);
        var subLayout = BuildSingleLineLayout(subText, subPaint, metaW);
        var metaTotalH = titleLayout.Height + 6f + subLayout.Height;
        var metaTop = padTop + (coverSize - metaTotalH) / 2f;

        DrawLayoutAt(canvas, titleLayout, metaX, metaTop);
        DrawLayoutAt(canvas, subLayout, metaX, metaTop + titleLayout.Height + 6f);

        // 2. 底部左下角 Spotify 样式标识
        DrawSpotifyBrand(canvas, padX, size - 78f, boldTf, badgeBottomBg);

        // 3. 中部自适应歌词块
        const float lyricsTopBound = 216f;
        const float lyricsBottomBound = 928f;
        var lyricsMaxW = size - padX * 2f;
        var maxAvailH = lyricsBottomBound - lyricsTopBound;

        var (fontSize, _, _, totalH) = FitLyricsMetrics(
            opt.Lines,
            opt.ShowTranslation,
            titleTf,
            regTf,
            lyricsMaxW,
            maxAvailH,
            maxFontSp: opt.Lines.Count <= 2 ? 70f : 62f,
            minFontSp: 28f,
            lineSpacingMult: 1.12f);

        var startY = lyricsTopBound + MathF.Max(0f, (maxAvailH - totalH) / 2f);
        DrawLyricsBlock(
            canvas,
            opt.Lines,
            opt.ShowTranslation,
            titleTf,
            regTf,
            fontSize,
            lineSpacingMult: 1.12f,
            x: padX,
            y: startY,
            maxW: lyricsMaxW,
            mainColor: Color.White,
            subColor: Color.Argb(196, 255, 255, 255));

        return bitmap;
    }

    private static Bitmap RenderAppleMusicCard(Context ctx, ShareCardOptions opt)
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

        var (fontSize, _, _, lyricsH) = FitLyricsMetrics(
            opt.Lines,
            opt.ShowTranslation,
            lyricRegTf,
            lyricRegTf,
            textW,
            maxAvailH: 760f,
            maxFontSp: opt.Lines.Count <= 3 ? 58f : 52f,
            minFontSp: 30f,
            lineSpacingMult: 1.16f);

        var upperH = MathF.Max(236f, lyricsH + upperPadY * 2f);
        var cardH = upperH + lowerH;
        var canvasH = (int)MathF.Round(cardH + outerMargin * 2f);

        var bitmap = Bitmap.CreateBitmap(canvasW, canvasH, Bitmap.Config.Argb8888!)!;
        var canvas = new Canvas(bitmap);

        var hasCustomBg = opt.CustomBackgroundBitmap is { IsRecycled: false } customBg
            && customBg.Width > 0 && customBg.Height > 0;

        Color effectiveBase = opt.BaseColor;
        if (hasCustomBg)
        {
            DrawBitmapCenterCrop(canvas, opt.CustomBackgroundBitmap!, new RectF(0f, 0f, canvasW, canvasH));
            using var outerDim = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(64, 0, 0, 0) };
            canvas.DrawRect(0f, 0f, canvasW, canvasH, outerDim);
            (effectiveBase, _) = LyricsPalette.FromCover(opt.CustomBackgroundBitmap);
        }
        else
        {
            // 1. 封面色调外背景底衬（保持饱和度与四周等宽留白）
            var backdropBase = EnsureMinBrightness(LyricsPalette.BlendColor(effectiveBase, Color.White, 0.06), minSum: 115);
            var backdropTop = LyricsPalette.BlendColor(backdropBase, Color.White, 0.06);
            var backdropBottom = LyricsPalette.BlendColor(backdropBase, Color.Black, 0.10);

            using (var outerPaint = new Paint(PaintFlags.AntiAlias))
            {
                outerPaint.SetShader(new LinearGradient(
                    0f, 0f, 0f, canvasH,
                    [backdropTop.ToArgb(), backdropBottom.ToArgb()],
                    null,
                    Shader.TileMode.Clamp!));
                canvas.DrawRect(0f, 0f, canvasW, canvasH, outerPaint);
            }

            if (opt.CoverBitmap is { IsRecycled: false } coverBmp && (coverBmp.Width > 12 || coverBmp.Height > 12))
            {
                using var tiny = Bitmap.CreateScaledBitmap(coverBmp, 12, 12, true);
                if (tiny is not null)
                {
                    using var washPaint = new Paint(PaintFlags.AntiAlias | PaintFlags.FilterBitmap)
                    {
                        Alpha = 28,
                    };
                    canvas.DrawBitmap(tiny, null, new RectF(0f, 0f, canvasW, canvasH), washPaint);
                }
            }
            effectiveBase = backdropBase;
        }

        // 2. 卡片柔和投影
        var cardLeft = outerMargin;
        var cardTop = outerMargin;
        var cardRect = new RectF(cardLeft, cardTop, cardLeft + cardW, cardTop + cardH);

        using (var shadowPaint = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(72, 0, 0, 0) })
        {
            shadowPaint.SetMaskFilter(new BlurMaskFilter(20f, BlurMaskFilter.Blur.Normal!));
            var shadowRect = new RectF(cardRect.Left, cardRect.Top + 6f, cardRect.Right, cardRect.Bottom + 6f);
            canvas.DrawRoundRect(shadowRect, cardRadius, cardRadius, shadowPaint);
        }

        // 3. 圆角双色卡片主体
        var clipPath = new Path();
        clipPath.AddRoundRect(cardRect, cardRadius, cardRadius, Path.Direction.Cw!);

        canvas.Save();
        canvas.ClipPath(clipPath);

        var upperColor = EnsureMinBrightness(LyricsPalette.BlendColor(effectiveBase, Color.Black, 0.16), minRgb: 38);
        var lowerColor = EnsureMinBrightness(LyricsPalette.BlendColor(effectiveBase, Color.Black, 0.30), minRgb: 26);
        var panelAlpha = hasCustomBg ? 232 : 255;

        using (var panelPaint = new Paint(PaintFlags.AntiAlias))
        {
            panelPaint.Color = Color.Argb(panelAlpha, upperColor.R, upperColor.G, upperColor.B);
            canvas.DrawRect(cardLeft, cardTop, cardLeft + cardW, cardTop + upperH, panelPaint);

            panelPaint.Color = Color.Argb(panelAlpha, lowerColor.R, lowerColor.G, lowerColor.B);
            canvas.DrawRect(cardLeft, cardTop + upperH, cardLeft + cardW, cardTop + cardH, panelPaint);
        }

        // 上半区歌词（采用更清透的系统 Regular 字重，保证中英文观感统一）
        var lyricsStartY = cardTop + (upperH - lyricsH) / 2f;
        DrawLyricsBlock(
            canvas,
            opt.Lines,
            opt.ShowTranslation,
            lyricRegTf,
            lyricRegTf,
            fontSize,
            lineSpacingMult: 1.16f,
            x: cardLeft + cardPadX,
            y: lyricsStartY,
            maxW: textW,
            mainColor: Color.Argb(245, 255, 255, 255),
            subColor: Color.Argb(182, 255, 255, 255));

        // 下半区专辑封面 + 歌名（Medium） + 歌手（Regular） + Apple Music 标识
        const float appleCoverSize = 142f;
        var lowerTop = cardTop + upperH;
        var appleCoverRect = new RectF(
            cardLeft + cardPadX,
            lowerTop + (lowerH - appleCoverSize) / 2f,
            cardLeft + cardPadX + appleCoverSize,
            lowerTop + (lowerH + appleCoverSize) / 2f);
        DrawCoverThumbnail(canvas, opt.CoverBitmap, appleCoverRect, radius: 16f, metaMedTf);

        var infoX = appleCoverRect.Right + 30f;
        var infoW = cardRect.Right - cardPadX - infoX;
        var titleStr = string.IsNullOrWhiteSpace(opt.TrackTitle) ? "未知歌曲" : opt.TrackTitle!.Trim();
        var artistStr = string.IsNullOrWhiteSpace(opt.ArtistName) ? "" : opt.ArtistName!.Trim();

        var aTitlePaint = CreateTextPaint(metaMedTf, 35f, Color.Argb(245, 255, 255, 255));
        var aTitleLayout = BuildSingleLineLayout(titleStr, aTitlePaint, infoW);

        StaticLayout? aArtistLayout = null;
        if (!string.IsNullOrEmpty(artistStr))
        {
            var aArtistPaint = CreateTextPaint(lyricRegTf, 32f, Color.Argb(204, 255, 255, 255));
            aArtistLayout = BuildSingleLineLayout(artistStr, aArtistPaint, infoW);
        }

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
        DrawAppleMusicBrand(canvas, infoX, curY, brandTf, Color.Argb(115, 255, 255, 255));

        canvas.Restore();
        return bitmap;
    }

    private static (float FontSize, float LineGap, float TrGap, float TotalHeight) FitLyricsMetrics(
        IReadOnlyList<ShareLyricLine> lines,
        bool showTranslation,
        Typeface? mainTf,
        Typeface? subTf,
        float maxW,
        float maxAvailH,
        float maxFontSp,
        float minFontSp,
        float lineSpacingMult)
    {
        for (var size = maxFontSp; size >= minFontSp; size -= 2f)
        {
            var lineGap = MathF.Round(size * 0.24f);
            var trGap = MathF.Round(size * 0.10f);
            var h = MeasureLyricsHeight(lines, showTranslation, mainTf, subTf, size, lineGap, trGap, lineSpacingMult, maxW);
            if (h <= maxAvailH || size <= minFontSp)
                return (size, lineGap, trGap, h);
        }

        var fallbackGap = MathF.Round(minFontSp * 0.24f);
        var fallbackTrGap = MathF.Round(minFontSp * 0.10f);
        var fallbackH = MeasureLyricsHeight(lines, showTranslation, mainTf, subTf, minFontSp, fallbackGap, fallbackTrGap, lineSpacingMult, maxW);
        return (minFontSp, fallbackGap, fallbackTrGap, fallbackH);
    }

    private static float MeasureLyricsHeight(
        IReadOnlyList<ShareLyricLine> lines,
        bool showTranslation,
        Typeface? mainTf,
        Typeface? subTf,
        float mainSize,
        float lineGap,
        float trGap,
        float lineSpacingMult,
        float maxW)
    {
        var mainPaint = CreateTextPaint(mainTf, mainSize, Color.White);
        var subSize = MathF.Max(18f, MathF.Round(mainSize * 0.54f));
        var subPaint = CreateTextPaint(subTf, subSize, Color.White);

        float total = 0f;
        for (var i = 0; i < lines.Count; i++)
        {
            var item = lines[i];
            var text = string.IsNullOrWhiteSpace(item.Original) ? "♪" : item.Original;
            var mainLayout = BuildMultilineLayout(text, mainPaint, maxW, lineSpacingMult);
            total += mainLayout.Height;

            if (showTranslation && !string.IsNullOrWhiteSpace(item.Translation))
            {
                var trLayout = BuildMultilineLayout(item.Translation!, subPaint, maxW, 1.15f);
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
        Color subColor)
    {
        var lineGap = MathF.Round(mainSize * 0.24f);
        var trGap = MathF.Round(mainSize * 0.10f);
        var mainPaint = CreateTextPaint(mainTf, mainSize, mainColor);
        var subSize = MathF.Max(18f, MathF.Round(mainSize * 0.54f));
        var subPaint = CreateTextPaint(subTf, subSize, subColor);

        var curY = y;
        for (var i = 0; i < lines.Count; i++)
        {
            var item = lines[i];
            var text = string.IsNullOrWhiteSpace(item.Original) ? "♪" : item.Original;
            var mainLayout = BuildMultilineLayout(text, mainPaint, maxW, lineSpacingMult);
            DrawLayoutAt(canvas, mainLayout, x, curY);
            curY += mainLayout.Height;

            if (showTranslation && !string.IsNullOrWhiteSpace(item.Translation))
            {
                curY += trGap;
                var trLayout = BuildMultilineLayout(item.Translation!, subPaint, maxW, 1.15f);
                DrawLayoutAt(canvas, trLayout, x, curY);
                curY += trLayout.Height;
            }

            curY += lineGap;
        }
    }

    private static void DrawBitmapCenterCrop(Canvas canvas, Bitmap src, RectF dst)
    {
        var srcRatio = (float)src.Width / src.Height;
        var dstRatio = dst.Width() / dst.Height();
        Rect srcRect;
        if (srcRatio > dstRatio)
        {
            var cropW = (int)MathF.Round(src.Height * dstRatio);
            var left = (src.Width - cropW) / 2;
            srcRect = new Rect(left, 0, left + cropW, src.Height);
        }
        else
        {
            var cropH = (int)MathF.Round(src.Width / dstRatio);
            var top = (src.Height - cropH) / 2;
            srcRect = new Rect(0, top, src.Width, top + cropH);
        }

        using var bmpPaint = new Paint(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
        canvas.DrawBitmap(src, srcRect, dst, bmpPaint);
    }

    private static void DrawCoverThumbnail(Canvas canvas, Bitmap? cover, RectF rect, float radius, Typeface? noteTf)
    {
        var clip = new Path();
        clip.AddRoundRect(rect, radius, radius, Path.Direction.Cw!);

        canvas.Save();
        canvas.ClipPath(clip);

        using (var fallbackBg = new Paint(PaintFlags.AntiAlias) { Color = LyricsPalette.CoverFrameBackground })
            canvas.DrawRect(rect, fallbackBg);

        if (cover is { IsRecycled: false } && cover.Width > 0 && cover.Height > 0)
        {
            DrawBitmapCenterCrop(canvas, cover, rect);
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

    private static void DrawSpotifyBrand(Canvas canvas, float leftX, float centerY, Typeface? boldTf, Color waveCutColor)
    {
        const float r = 24f;
        var cx = leftX + r;

        using (var circlePaint = new Paint(PaintFlags.AntiAlias) { Color = Color.White })
        {
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
            var arcPath = new Path();

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

        using var wordPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.White,
            TextSize = 40f,
            LetterSpacing = -0.02f,
        };
        if (boldTf is not null) wordPaint.SetTypeface(boldTf);
        var fm = wordPaint.GetFontMetrics()!;
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
            var leaf = new Path();
            leaf.MoveTo(12.2f, 6.2f);
            leaf.CubicTo(12.0f, 2.8f, 14.4f, 0.4f, 17.2f, 0.2f);
            leaf.CubicTo(17.3f, 3.4f, 14.9f, 6.0f, 12.2f, 6.2f);
            leaf.Close();
            canvas.DrawPath(leaf, applePaint);

            var body = new Path();
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

    private static TextPaint CreateTextPaint(Typeface? typeface, float textSizePx, Color color)
    {
        var tp = new TextPaint(PaintFlags.AntiAlias | PaintFlags.SubpixelText)
        {
            Color = color,
            TextSize = textSizePx,
        };
        if (typeface is not null) tp.SetTypeface(typeface);
        return tp;
    }

    private static StaticLayout BuildSingleLineLayout(string text, TextPaint paint, float widthPx)
    {
        var w = Math.Max(1, (int)MathF.Floor(widthPx));
        return StaticLayout.Builder
            .Obtain(text, 0, text.Length, paint, w)!
            .SetAlignment(global::Android.Text.Layout.Alignment.AlignNormal!)
            .SetMaxLines(1)
            .SetEllipsize(TextUtils.TruncateAt.End!)
            .SetIncludePad(false)
            .Build()!;
    }

    private static StaticLayout BuildMultilineLayout(string text, TextPaint paint, float widthPx, float spacingMult)
    {
        var w = Math.Max(1, (int)MathF.Floor(widthPx));
        return StaticLayout.Builder
            .Obtain(text, 0, text.Length, paint, w)!
            .SetAlignment(global::Android.Text.Layout.Alignment.AlignNormal!)
            .SetLineSpacing(0f, spacingMult)
            .SetIncludePad(false)
            .Build()!;
    }

    private static void DrawLayoutAt(Canvas canvas, StaticLayout layout, float x, float y)
    {
        canvas.Save();
        canvas.Translate(x, y);
        layout.Draw(canvas);
        canvas.Restore();
    }

    private static Color EnsureMinBrightness(Color color, int minRgb = 0, int minSum = 0)
    {
        int r = color.R;
        int g = color.G;
        int b = color.B;
        if (minRgb > 0)
        {
            r = Math.Max(r, minRgb);
            g = Math.Max(g, minRgb);
            b = Math.Max(b, minRgb);
        }
        if (minSum > 0 && r + g + b < minSum)
        {
            var delta = (minSum - (r + g + b) + 2) / 3;
            r = Math.Min(255, r + delta);
            g = Math.Min(255, g + delta);
            b = Math.Min(255, b + delta);
        }
        return Color.Rgb(r, g, b);
    }
}
