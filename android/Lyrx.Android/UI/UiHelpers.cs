using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AFont = Android.Graphics.Fonts.Font;
using AFontFamily = Android.Graphics.Fonts.FontFamily;
using AFontSlant = Android.Graphics.Fonts.FontSlant;
using AFontStyle = Android.Graphics.Fonts.FontStyle;

namespace Musebase.Android.UI;

internal sealed class RoundedOutlineProvider : ViewOutlineProvider
{
    private readonly float _radiusPx;

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(RoundedOutlineProvider))]
    public RoundedOutlineProvider()
    {
    }

    public RoundedOutlineProvider(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    public RoundedOutlineProvider(float radiusPx) => _radiusPx = radiusPx;

    public override void GetOutline(View? view, Outline? outline)
    {
        if (view is null || outline is null) return;
        outline.SetRoundRect(0, 0, view.Width, view.Height, _radiusPx);
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

    public static Typeface? ResolveFontWeight(Context? context, int weight, bool useTitleFont = false)
    {
        try
        {
            var customTf = ResolveCustomTypeface(context?.Assets, weight, useTitleFont);
            if (customTf is not null)
                return customTf;

            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                var tf = CreateSystemWeightTypefaceApi28(weight);
                if (tf is not null)
                    return tf;
            }
        }
        catch
        {
            // 部分精简 ROM 自定义字重创建失败时回退至默认字体样式
        }

        try
        {
            return Typeface.Create(Typeface.Default, weight >= FontWeightSemiBold ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
        catch
        {
            return Typeface.Default;
        }
    }

    public static void SetFontWeight(TextView tv, int weight, bool useTitleFont = false)
    {
        try
        {
            var resolved = ResolveFontWeight(tv.Context, weight, useTitleFont);
            if (resolved is not null)
            {
                tv.Typeface = resolved;
                return;
            }
        }
        catch
        {
            // 忽略字体设置异常
        }

        try
        {
            tv.SetTypeface(Typeface.Default, weight >= FontWeightSemiBold ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
        catch
        {
            // 忽略极端系统字体异常
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    private static Typeface? CreateSystemWeightTypefaceApi28(int weight) => weight switch
    {
        >= FontWeightBold => _tfFallbackBold ??= Typeface.Create(Typeface.Default, FontWeightBold, false),
        >= FontWeightSemiBold => _tfFallbackSemiBold ??= Typeface.Create(Typeface.Default, FontWeightSemiBold, false),
        _ => _tfFallbackRegular ??= Typeface.Create(Typeface.Default, FontWeightRegular, false),
    };

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

    [MethodImpl(MethodImplOptions.NoInlining)]
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private static Typeface BuildCustomTypeface(AFontFamily family, int weight) =>
        new Typeface.CustomFallbackBuilder(family)
            .SetStyle(new AFontStyle(weight, AFontSlant.Upright))
            .SetSystemFallback("sans-serif")
            .Build();

    [MethodImpl(MethodImplOptions.NoInlining)]
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
        try
        {
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
                ApplyInsetsControllerApi30(window, decor, edgeToEdge);
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
        catch
        {
            // 部分定制系统禁止或延迟修改状态栏属性时静默忽略
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private static void ApplyInsetsControllerApi30(global::Android.Views.Window window, View decor, bool edgeToEdge)
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

    public readonly record struct HorizontalAnchorBounds(int Left, int Right, bool ConstrainedToContainer);

    /// <summary>
    /// 基于当前窗口实际可用短边判断是否为平板（Pad / 折叠屏展开态）布局，兼容分屏缩小场景。
    /// </summary>
    public static bool IsTabletLayout(Context? ctx)
    {
        var res = ctx?.Resources;
        var dm = res?.DisplayMetrics;
        if (dm is not null && dm.Density > 0f && dm.WidthPixels > 0 && dm.HeightPixels > 0)
        {
            var minDimDp = Math.Min(dm.WidthPixels, dm.HeightPixels) / dm.Density;
            return minDimDp >= 580f;
        }

        return (res?.Configuration?.SmallestScreenWidthDp ?? 0) >= 580;
    }

    public static HorizontalAnchorBounds ResolveHorizontalAnchorBounds(
        Context ctx,
        View? anchorContainer,
        int screenW,
        int margin)
    {
        if (anchorContainer is not null && anchorContainer.Width > 0)
        {
            var containerLoc = new int[2];
            anchorContainer.GetLocationInWindow(containerLoc);
            var containerLeft = containerLoc[0];
            var containerRight = containerLeft + anchorContainer.Width;
            var leftPad = Math.Max(Dp(ctx, 8), anchorContainer.PaddingLeft - Dp(ctx, 4));
            var rightPad = Math.Max(Dp(ctx, 10), anchorContainer.PaddingRight - Dp(ctx, 4));
            var innerLeft = containerLeft + leftPad;
            var innerRight = containerRight - rightPad;
            if (innerRight - innerLeft >= Dp(ctx, 180))
                return new HorizontalAnchorBounds(innerLeft, innerRight, ConstrainedToContainer: true);
        }

        return new HorizontalAnchorBounds(margin, Math.Max(margin + 1, screenW - margin), ConstrainedToContainer: false);
    }
}
