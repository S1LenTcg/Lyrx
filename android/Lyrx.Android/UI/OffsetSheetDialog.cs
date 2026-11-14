using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace Musebase.Android.UI;

/// <summary>
/// 复刻桌面端 <c>ClassicLyricsWindow._offsetPopup</c> 的轻量级非模态时间偏移浮窗：
/// 不遮黑全屏、不另弹独立窗口，直接锚定在底栏时钟按钮上方，仅保留核心微调选项。
/// </summary>
internal static class OffsetSheetDialog
{
    private static readonly Color TrackBg = Color.Rgb(22, 22, 22);
    private static readonly Color ChipBg = Color.Rgb(42, 42, 42);
    private static readonly Color ChipBorder = Color.Rgb(58, 58, 58);
    private static readonly Color ActiveScopeBg = Color.Rgb(44, 62, 50);
    private static readonly Color ActiveScopeBorder = Color.Argb(80, 30, 215, 96);

    private static PopupWindow? _activePopup;
    private static long _lastDismissAtMs;

    public static void Show(Activity activity, View anchor, Action onChanged)
    {
        if (activity.IsFinishing || activity.IsDestroyed) return;

        if (_activePopup is { IsShowing: true })
        {
            _activePopup.Dismiss();
            _activePopup = null;
            return;
        }

        // 防止点击锚点触发 outside-touch 关闭后在同一次点击事件内立刻重开
        if (SystemClock.UptimeMillis() - _lastDismissAtMs < 200)
            return;

        var app = LyrxApp.Instance;
        var hasTrack = app.NowPlaying.CurrentTrack is not null;
        var perTrack = hasTrack;

        var dm = activity.Resources?.DisplayMetrics;
        var screenW = dm?.WidthPixels ?? 1080;
        var popupWidth = Math.Min(UiHelpers.Dp(activity, 278), (int)(screenW * 0.90));

        var card = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Background = UiHelpers.RoundedRect(activity, LyricsPalette.PopupBackground, 16, LyricsPalette.PopupBorder, 1f),
            Elevation = UiHelpers.Dp(activity, 12),
        };
        card.SetPadding(
            UiHelpers.Dp(activity, 14),
            UiHelpers.Dp(activity, 12),
            UiHelpers.Dp(activity, 14),
            UiHelpers.Dp(activity, 13));

        // 1. 顶部标题 + 仅此曲/全部 分段开关
        var header = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        header.SetGravity(GravityFlags.CenterVertical);

        var title = new TextView(activity) { Text = "音频同步调整" };
        title.SetTextColor(Color.White);
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
        title.SetTypeface(null, TypefaceStyle.Bold);
        header.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var scopeHost = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            Background = UiHelpers.RoundedRect(activity, TrackBg, 12, LyricsPalette.PopupBorder, 1f),
        };
        var segPad = UiHelpers.Dp(activity, 2);
        scopeHost.SetPadding(segPad, segPad, segPad, segPad);

        var scopeTrackBtn = MakeScopeChip(activity, "仅此曲");
        var scopeGlobalBtn = MakeScopeChip(activity, "全部");
        var segH = UiHelpers.Dp(activity, 22);
        scopeHost.AddView(scopeTrackBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, segH));
        scopeHost.AddView(scopeGlobalBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, segH));
        header.AddView(scopeHost);

        card.AddView(header, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(activity, 10),
        });

        // 2. 当前数值 + 状态说明 + 右侧「归零」胶囊按钮
        var readoutRow = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        readoutRow.SetGravity(GravityFlags.CenterVertical);

        var readoutLeft = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        readoutLeft.SetGravity(GravityFlags.CenterVertical);

        var valueView = new TextView(activity);
        valueView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15.5f);
        valueView.SetTypeface(null, TypefaceStyle.Bold);

        var dirView = new TextView(activity);
        dirView.SetTextColor(LyricsPalette.MutedGray);
        dirView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);

        readoutLeft.AddView(valueView);
        readoutLeft.AddView(dirView, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            LeftMargin = UiHelpers.Dp(activity, 7),
        });
        readoutRow.AddView(readoutLeft, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var resetBtn = new Button(activity)
        {
            Text = "归零",
            Background = UiHelpers.RoundedRect(activity, ChipBg, 14, ChipBorder, 1f),
            Gravity = GravityFlags.Center,
        };
        resetBtn.SetAllCaps(false);
        resetBtn.SetTextColor(Color.White);
        resetBtn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
        resetBtn.SetPadding(UiHelpers.Dp(activity, 12), 0, UiHelpers.Dp(activity, 12), 0);
        resetBtn.SetMinWidth(0);
        resetBtn.SetMinimumWidth(0);
        resetBtn.SetMinHeight(0);
        resetBtn.SetMinimumHeight(0);

        readoutRow.AddView(resetBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, UiHelpers.Dp(activity, 28)));
        card.AddView(readoutRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(activity, 10),
        });

        void RefreshReadout()
        {
            var val = perTrack ? app.Coordinator.TrackOffsetSeconds : app.Settings.ManualOffsetSeconds;
            var isZero = Math.Abs(val) < 0.005;
            valueView.Text = isZero ? "0.0s" : $"{val:+0.0;-0.0;0.0}s";
            valueView.SetTextColor(isZero ? Color.White : LyricsPalette.AccentGreen);

            dirView.Text = isZero ? "默认同步" : val > 0 ? "歌词提前" : "歌词延后";

            resetBtn.Enabled = !isZero;
            resetBtn.Alpha = isZero ? 0.42f : 1f;

            scopeTrackBtn.Enabled = hasTrack;
            scopeTrackBtn.Alpha = hasTrack ? 1f : 0.40f;
            StyleScopeChip(activity, scopeTrackBtn, perTrack);
            StyleScopeChip(activity, scopeGlobalBtn, !perTrack);
        }

        void ApplyDelta(double? delta)
        {
            if (perTrack) app.AdjustTrackOffsetDelta(app.NowPlaying.CurrentTrack, delta);
            else app.AdjustGlobalOffsetDelta(delta);
            RefreshReadout();
            onChanged();
        }

        scopeTrackBtn.Click += (_, _) =>
        {
            if (!hasTrack) return;
            perTrack = true;
            RefreshReadout();
        };
        scopeGlobalBtn.Click += (_, _) =>
        {
            perTrack = false;
            RefreshReadout();
        };
        resetBtn.Click += (_, _) => ApplyDelta(null);

        // 3. 单行 4 个步进胶囊按钮：[-0.5s, -0.1s, +0.1s, +0.5s]（对齐 PC _offsetPopup）
        var stepRow = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        (string Label, double Delta)[] steps =
        [
            ("-0.5s", -0.5),
            ("-0.1s", -0.1),
            ("+0.1s", 0.1),
            ("+0.5s", 0.5),
        ];

        for (var i = 0; i < steps.Length; i++)
        {
            var (label, delta) = steps[i];
            var btn = new Button(activity)
            {
                Text = label,
                Background = UiHelpers.RoundedRect(activity, ChipBg, 15, ChipBorder, 1f),
                Gravity = GravityFlags.Center,
            };
            btn.SetAllCaps(false);
            btn.SetTextColor(Color.White);
            btn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
            btn.SetTypeface(null, TypefaceStyle.Bold);
            btn.SetPadding(0, 0, 0, 0);
            btn.SetMinWidth(0);
            btn.SetMinimumWidth(0);
            btn.SetMinHeight(0);
            btn.SetMinimumHeight(0);
            btn.SetSingleLine(true);
            btn.Click += (_, _) => ApplyDelta(delta);

            var lp = new LinearLayout.LayoutParams(0, UiHelpers.Dp(activity, 30), 1f);
            if (i < steps.Length - 1) lp.RightMargin = UiHelpers.Dp(activity, 6);
            stepRow.AddView(btn, lp);
        }
        card.AddView(stepRow);

        RefreshReadout();

        var popup = new PopupWindow(card, popupWidth, ViewGroup.LayoutParams.WrapContent, focusable: true)
        {
            OutsideTouchable = true,
            Elevation = UiHelpers.Dp(activity, 12),
            AttachedInDecor = true,
        };
        popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        popup.DismissEvent += (_, _) =>
        {
            _lastDismissAtMs = SystemClock.UptimeMillis();
            if (ReferenceEquals(_activePopup, popup)) _activePopup = null;
        };

        card.Measure(
            View.MeasureSpec.MakeMeasureSpec(popupWidth, MeasureSpecMode.Exactly),
            View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
        var popupHeight = card.MeasuredHeight;

        var loc = new int[2];
        anchor.GetLocationInWindow(loc);
        var margin = UiHelpers.Dp(activity, 12);

        _activePopup = popup;
        if (loc[1] > 0 || anchor.Height > 0)
        {
            var x = Math.Clamp(
                loc[0] + anchor.Width + UiHelpers.Dp(activity, 64) - popupWidth,
                margin,
                Math.Max(margin, screenW - popupWidth - margin));
            var y = Math.Max(margin, loc[1] - popupHeight - UiHelpers.Dp(activity, 10));
            popup.ShowAtLocation(anchor, GravityFlags.Top | GravityFlags.Start, x, y);
        }
        else
        {
            popup.ShowAtLocation(anchor, GravityFlags.Bottom | GravityFlags.End, margin, UiHelpers.Dp(activity, 118));
        }
    }

    private static TextView MakeScopeChip(Context ctx, string label)
    {
        var tv = new TextView(ctx)
        {
            Text = label,
            Gravity = GravityFlags.Center,
            Clickable = true,
        };
        tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
        tv.SetPadding(UiHelpers.Dp(ctx, 10), 0, UiHelpers.Dp(ctx, 10), 0);
        return tv;
    }

    private static void StyleScopeChip(Context ctx, TextView chip, bool active)
    {
        chip.Background = active
            ? UiHelpers.RoundedRect(ctx, ActiveScopeBg, 10, ActiveScopeBorder, 1f)
            : null;
        chip.SetTextColor(active ? LyricsPalette.AccentGreen : LyricsPalette.MutedGray);
        chip.SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
    }
}
