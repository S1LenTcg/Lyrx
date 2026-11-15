using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AFont = Android.Graphics.Fonts.Font;
using AFontFamily = Android.Graphics.Fonts.FontFamily;
using AFontSlant = Android.Graphics.Fonts.FontSlant;
using AFontStyle = Android.Graphics.Fonts.FontStyle;

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
    public const int FontWeightRegular = 400;
    public const int FontWeightSemiBold = 600;
    public const int FontWeightBold = 700;

    private const string UiRegularAsset = "fonts/SpotifyMixUI-Regular.ttf";
    private const string UiBoldAsset = "fonts/SpotifyMixUI-Bold.ttf";
    private const string TitleBoldAsset = "fonts/SpotifyMixUITitle-700.ttf";

    private static readonly object FontLock = new();
    private static bool _fontsInitialized;
    private static Typeface? _tfUiRegular;
    private static Typeface? _tfUiSemiBold;
    private static Typeface? _tfUiBold;
    private static Typeface? _tfTitleBold;
    private static Typeface? _tfFallbackRegular;
    private static Typeface? _tfFallbackSemiBold;
    private static Typeface? _tfFallbackBold;

    public static void SetFontWeight(TextView tv, int weight, bool useTitleFont = false)
    {
        var customTf = ResolveCustomTypeface(tv.Context?.Assets, weight, useTitleFont);
        if (customTf is not null)
        {
            tv.Typeface = customTf;
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var tf = weight switch
            {
                >= FontWeightBold => _tfFallbackBold ??= Typeface.Create(Typeface.Default, FontWeightBold, false),
                >= FontWeightSemiBold => _tfFallbackSemiBold ??= Typeface.Create(Typeface.Default, FontWeightSemiBold, false),
                _ => _tfFallbackRegular ??= Typeface.Create(Typeface.Default, FontWeightRegular, false),
            };
            tv.Typeface = tf;
        }
        else
        {
            tv.SetTypeface(Typeface.Default, weight >= FontWeightSemiBold ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
    }

    private static Typeface? ResolveCustomTypeface(AssetManager? assets, int weight, bool useTitleFont)
    {
        EnsureFontsLoaded(assets);
        if (useTitleFont)
            return _tfTitleBold ?? _tfUiBold;

        return weight switch
        {
            >= FontWeightBold => _tfUiBold,
            >= FontWeightSemiBold => _tfUiSemiBold ?? _tfUiBold,
            _ => _tfUiRegular,
        };
    }

    private static void EnsureFontsLoaded(AssetManager? assets)
    {
        if (_fontsInitialized || assets is null) return;
        lock (FontLock)
        {
            if (_fontsInitialized) return;
            _fontsInitialized = true;

            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                TryLoadApi29Fonts(assets);
                return;
            }

            TryLoadLegacyFonts(assets);
        }
    }

    [global::System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private static void TryLoadApi29Fonts(AssetManager assets)
    {
        try
        {
            var regFont = new AFont.Builder(assets, UiRegularAsset)
                .SetWeight(FontWeightRegular)
                .SetSlant(AFontSlant.Upright)
                .Build();
            var boldFont = new AFont.Builder(assets, UiBoldAsset)
                .SetWeight(FontWeightBold)
                .SetSlant(AFontSlant.Upright)
                .Build();
            var uiFamily = new AFontFamily.Builder(regFont)
                .AddFont(boldFont)
                .Build();

            _tfUiRegular = BuildCustomTypeface(uiFamily, FontWeightRegular);
            _tfUiSemiBold = BuildCustomTypeface(uiFamily, FontWeightSemiBold);
            _tfUiBold = BuildCustomTypeface(uiFamily, FontWeightBold);
        }
        catch
        {
            // 资源缺失或损坏时自动回退至系统字体
        }

        try
        {
            var titleFont = new AFont.Builder(assets, TitleBoldAsset)
                .SetWeight(FontWeightBold)
                .SetSlant(AFontSlant.Upright)
                .Build();
            var titleFamily = new AFontFamily.Builder(titleFont).Build();
            _tfTitleBold = BuildCustomTypeface(titleFamily, FontWeightBold);
        }
        catch
        {
            // 回退至 UI 粗体或系统字体
        }
    }

    [global::System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private static Typeface BuildCustomTypeface(AFontFamily family, int weight) =>
        new Typeface.CustomFallbackBuilder(family)
            .SetStyle(new AFontStyle(weight, AFontSlant.Upright))
            .SetSystemFallback("sans-serif")
            .Build();

    private static void TryLoadLegacyFonts(AssetManager assets)
    {
        try
        {
            var reg = Typeface.CreateFromAsset(assets, UiRegularAsset);
            var bold = Typeface.CreateFromAsset(assets, UiBoldAsset);
            var title = Typeface.CreateFromAsset(assets, TitleBoldAsset);

            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                _tfUiRegular = reg is null ? null : Typeface.Create(reg, FontWeightRegular, false);
                _tfUiSemiBold = bold is null ? null : Typeface.Create(bold, FontWeightSemiBold, false);
                _tfUiBold = bold is null ? null : Typeface.Create(bold, FontWeightBold, false);
                _tfTitleBold = title is null ? null : Typeface.Create(title, FontWeightBold, false);
            }
            else
            {
                _tfUiRegular = reg;
                _tfUiSemiBold = bold;
                _tfUiBold = bold;
                _tfTitleBold = title;
            }
        }
        catch
        {
            // 回退至系统默认字体
        }
    }

    public static void SetTabularNumerals(TextView tv)
    {
        tv.FontFeatureSettings = "tnum";
    }

    public static void HideStatusBar(
        global::Android.Views.Window? window,
        bool edgeToEdge = false,
        Color? navigationBarColor = null)
    {
        if (window is null) return;
        // 先确保 DecorView 已创建，避免在 API 35+ 上直接访问 Window.InsetsController 触发 NPE
        if (window.DecorView is not { } decor) return;

        if (edgeToEdge)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var attrs = window.Attributes;
                if (attrs is not null && attrs.LayoutInDisplayCutoutMode != LayoutInDisplayCutoutMode.Always)
                {
                    attrs.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Always;
                    window.Attributes = attrs;
                }
            }
            else if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                var attrs = window.Attributes;
                if (attrs is not null && attrs.LayoutInDisplayCutoutMode != LayoutInDisplayCutoutMode.ShortEdges)
                {
                    attrs.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                    window.Attributes = attrs;
                }
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(29) && !OperatingSystem.IsAndroidVersionAtLeast(35))
            {
#pragma warning disable CA1422
                window.StatusBarContrastEnforced = false;
#pragma warning restore CA1422
            }
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (edgeToEdge && !OperatingSystem.IsAndroidVersionAtLeast(35))
            {
#pragma warning disable CA1422
                window.SetDecorFitsSystemWindows(false);
#pragma warning restore CA1422
            }

            if ((window.InsetsController ?? decor.WindowInsetsController) is { } controller)
            {
                controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                controller.Hide(WindowInsets.Type.StatusBars());
                if (edgeToEdge)
                {
                    controller.SetSystemBarsAppearance(
                        0,
                        (int)(WindowInsetsControllerAppearance.LightStatusBars
                            | WindowInsetsControllerAppearance.LightNavigationBars));
                }
            }
        }
        else
        {
            window.AddFlags(WindowManagerFlags.Fullscreen);
#pragma warning disable CS0618
            decor.SystemUiVisibility = (StatusBarVisibility)(
                SystemUiFlags.LayoutStable
                | SystemUiFlags.LayoutFullscreen
                | SystemUiFlags.Fullscreen
                | SystemUiFlags.ImmersiveSticky);
#pragma warning restore CS0618
        }

        if (edgeToEdge && !OperatingSystem.IsAndroidVersionAtLeast(35))
        {
#pragma warning disable CS0618
            window.SetStatusBarColor(Color.Transparent);
            if (navigationBarColor is { } navColor)
                window.SetNavigationBarColor(navColor);
#pragma warning restore CS0618
        }
    }

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
