using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace Musebase.Android.UI;

internal sealed class RoundedOutlineProvider(float radiusPx) : ViewOutlineProvider
{
    public override void GetOutline(View? view, Outline? outline)
    {
        if (view is null || outline is null) return;
        outline.SetRoundRect(0, 0, view.Width, view.Height, radiusPx);
    }
}

internal static class UiHelpers
{
    public static int Dp(Context ctx, float dp) =>
        (int)Math.Round(dp * (ctx.Resources?.DisplayMetrics?.Density ?? 2f));

    public static GradientDrawable RoundedRect(Context ctx, Color fill, float radiusDp, Color? strokeColor = null, float strokeWidthDp = 1f)
    {
        var d = new GradientDrawable();
        d.SetColor(fill.ToArgb());
        d.SetCornerRadius(Dp(ctx, radiusDp));
        if (strokeColor is { } stroke)
            d.SetStroke(Math.Max(1, Dp(ctx, strokeWidthDp)), ColorStateList.ValueOf(stroke));
        return d;
    }

    public static FrameLayout IconButton(
        Context ctx,
        ClassicIconKind icon,
        Color tint,
        Action onClick,
        float sizeDp = 38,
        float iconFraction = 0.56f,
        bool active = false,
        Color? circleBackground = null)
    {
        var host = new FrameLayout(ctx)
        {
            Clickable = true,
            Focusable = true,
        };
        if (circleBackground is { } circle)
            host.Background = RoundedRect(ctx, circle, sizeDp / 2f);

        var glyph = new VectorIconView(ctx, icon);
        glyph.Configure(icon, tint, active, iconFraction);
        host.AddView(glyph, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        host.Click += (_, _) => onClick();
        return host;
    }
}
