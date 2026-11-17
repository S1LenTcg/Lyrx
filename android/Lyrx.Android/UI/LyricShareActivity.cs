using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Android.Animation;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using Musebase.Android.Services;
using Musebase.Android.UI;

namespace Musebase.Android;

internal sealed record LyricSharePayload(
    string? TrackTitle,
    string? ArtistName,
    Bitmap? CoverBitmap,
    Color CoverBackground,
    IReadOnlyList<ShareLyricLine> Lines,
    bool DefaultShowTranslation);

/// <summary>
/// 独立全屏歌词分享编辑页，支持卡片预览、背景和排版编辑以及 PNG 导出。
/// </summary>
[Activity(
    Label = "歌词分享",
    Theme = "@style/LyrxTheme",
    Exported = false,
    ScreenOrientation = ScreenOrientation.Behind,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode)]
public sealed partial class LyricShareActivity : Activity
{
    private const int RequestCodeSaveToGallery = 4103;

    private sealed class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        private readonly WeakReference<LyricShareActivity>? _owner;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(InsetsListener))]
        public InsetsListener()
        {
        }

        public InsetsListener(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public InsetsListener(LyricShareActivity owner) => _owner = new WeakReference<LyricShareActivity>(owner);

        public WindowInsets OnApplyWindowInsets(View? v, WindowInsets? insets)
        {
            if (insets is not null && _owner is not null && _owner.TryGetTarget(out var owner))
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    ApplyInsetsApi30(owner, insets);
                }
                else
                {
#pragma warning disable CS0618
                    var left = insets.SystemWindowInsetLeft;
                    var top = insets.SystemWindowInsetTop;
                    var right = insets.SystemWindowInsetRight;
                    var bottom = insets.SystemWindowInsetBottom;
#pragma warning restore CS0618
                    if (OperatingSystem.IsAndroidVersionAtLeast(28))
                        MergeCutoutInsetsApi28(insets, ref left, ref top, ref right, ref bottom);
                    owner.ApplySystemBarInsets(left, top, right, bottom);
                }
            }
            return insets!;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [global::System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
        private static void ApplyInsetsApi30(LyricShareActivity owner, WindowInsets insets)
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            var left = bars.Left;
            var top = bars.Top;
            var right = bars.Right;
            var bottom = bars.Bottom;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                MergeRoundedCornerInsetsApi31(insets, ref left, ref top, ref right, ref bottom);
            owner.ApplySystemBarInsets(left, top, right, bottom);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [global::System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
        private static void MergeRoundedCornerInsetsApi31(
            WindowInsets insets,
            ref int left,
            ref int top,
            ref int right,
            ref int bottom)
        {
            var topLeftR = insets.GetRoundedCorner((int)RoundedCornerPosition.TopLeft)?.Radius ?? 0;
            var topRightR = insets.GetRoundedCorner((int)RoundedCornerPosition.TopRight)?.Radius ?? 0;
            var bottomLeftR = insets.GetRoundedCorner((int)RoundedCornerPosition.BottomLeft)?.Radius ?? 0;
            var bottomRightR = insets.GetRoundedCorner((int)RoundedCornerPosition.BottomRight)?.Radius ?? 0;

            static int SafeCornerInset(int radius) => radius > 0 ? (int)Math.Ceiling(radius * 0.35) : 0;

            left = Math.Max(left, Math.Max(SafeCornerInset(topLeftR), SafeCornerInset(bottomLeftR)));
            right = Math.Max(right, Math.Max(SafeCornerInset(topRightR), SafeCornerInset(bottomRightR)));
            top = Math.Max(top, Math.Max(SafeCornerInset(topLeftR), SafeCornerInset(topRightR)));
            bottom = Math.Max(bottom, Math.Max(SafeCornerInset(bottomLeftR), SafeCornerInset(bottomRightR)));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [global::System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
        private static void MergeCutoutInsetsApi28(
            WindowInsets insets,
            ref int left,
            ref int top,
            ref int right,
            ref int bottom)
        {
            if (insets.DisplayCutout is not { } cutout) return;
            left = Math.Max(left, cutout.SafeInsetLeft);
            top = Math.Max(top, cutout.SafeInsetTop);
            right = Math.Max(right, cutout.SafeInsetRight);
            bottom = Math.Max(bottom, cutout.SafeInsetBottom);
        }
    }

    private sealed class StyleSwitchPillView : FrameLayout
    {
        private readonly Paint _trackPaint = new(PaintFlags.AntiAlias);
        private readonly Paint _borderPaint = new(PaintFlags.AntiAlias);
        private readonly Paint _thumbPaint = new(PaintFlags.AntiAlias);
        private readonly TextView _spotifyLabel;
        private readonly TextView _appleLabel;
        private readonly int _padPx;
        private readonly int _touchSlop;
        private ValueAnimator? _snapAnim;

        private float _progress;
        private int _boldSide = -1;
        private LyricShareStyle _currentStyle = LyricShareStyle.Spotify;
        private Color _activeInk;
        private Color _inactiveInk;
        private float _downX;
        private float _startProg;
        private bool _dragging;

        public event Action<LyricShareStyle>? StyleSelected;

        public StyleSwitchPillView(Context context, LyricShareStyle initialStyle) : base(context)
        {
            SetWillNotDraw(false);
            Clickable = true;
            Focusable = true;

            _padPx = UiHelpers.Dp(context, 3);
            _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? UiHelpers.Dp(context, 6);
            _currentStyle = initialStyle;
            _progress = initialStyle == LyricShareStyle.AppleMusic ? 1f : 0f;

            _trackPaint.SetStyle(Paint.Style.Fill);
            _borderPaint.SetStyle(Paint.Style.Stroke);
            _borderPaint.StrokeWidth = Math.Max(1f, UiHelpers.Dp(context, 1f));
            _thumbPaint.SetStyle(Paint.Style.Fill);

            var row = new LinearLayout(context)
            {
                Orientation = global::Android.Widget.Orientation.Horizontal,
            };
            row.SetPadding(_padPx, _padPx, _padPx, _padPx);

            _spotifyLabel = MakeLabel(context, "Spotify");
            _appleLabel = MakeLabel(context, "Apple Music");
            row.AddView(_spotifyLabel, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));
            row.AddView(_appleLabel, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));

            AddView(row, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            SetMonetPalette(Color.Rgb(36, 61, 68), null);
        }

        public void SetMonetPalette(Color themeColor, Color? coverHueFallback)
        {
            var (monetSurface, activeInk) = LyricsPalette.SoftMonetPill(themeColor, coverHueFallback);
            var thumbColor = LyricsPalette.BlendColor(monetSurface, Color.Rgb(245, 247, 246), 0.42);
            var trackTint = LyricsPalette.BlendColor(Color.Rgb(10, 12, 12), themeColor, 0.14);

            _trackPaint.Color = Color.Argb(118, trackTint.R, trackTint.G, trackTint.B);
            _borderPaint.Color = Color.Argb(34, 255, 255, 255);
            _thumbPaint.Color = thumbColor;
            _activeInk = activeInk;
            _inactiveInk = Color.Argb(212, 255, 255, 255);

            SyncLabelAppearance();
            Invalidate();
        }

        private static TextView MakeLabel(Context ctx, string text)
        {
            var tv = new TextView(ctx)
            {
                Text = text,
                Gravity = GravityFlags.Center,
            };
            tv.SetIncludeFontPadding(false);
            tv.SetMaxLines(1);
            tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12.5f);
            return tv;
        }

        public float MeasureLongestLabelWidthPx()
        {
            using var paint = new Paint(_appleLabel.Paint);
            var bold = UiHelpers.ResolveFontWeight(Context!, UiHelpers.FontWeightBold);
            if (bold is not null)
                paint.SetTypeface(bold);
            return MathF.Max(paint.MeasureText("Spotify"), paint.MeasureText("Apple Music"));
        }

        public void SetProgress(float progress)
        {
            _progress = Math.Clamp(progress, 0f, 1f);
            SyncLabelAppearance();
            Invalidate();
        }

        public void AnimateToStyle(LyricShareStyle style)
        {
            _currentStyle = style;
            var target = style == LyricShareStyle.AppleMusic ? 1f : 0f;
            _snapAnim?.Cancel();
            if (MathF.Abs(_progress - target) < 0.005f)
            {
                SetProgress(target);
                return;
            }

            if (ValueAnimator.OfFloat(_progress, target) is not { } anim)
            {
                SetProgress(target);
                return;
            }
            anim.SetDuration(220);
            anim.SetInterpolator(new DecelerateInterpolator(1.6f));
            anim.Update += (_, e) =>
            {
                if (e.Animation?.AnimatedValue is Java.Lang.Float val)
                    SetProgress(val.FloatValue());
            };
            _snapAnim = anim;
            anim.Start();
        }

        private void SyncLabelAppearance()
        {
            _spotifyLabel.SetTextColor(LyricsPalette.BlendColor(_activeInk, _inactiveInk, _progress));
            _appleLabel.SetTextColor(LyricsPalette.BlendColor(_inactiveInk, _activeInk, _progress));

            var side = _progress >= 0.5f ? 1 : 0;
            if (side != _boldSide)
            {
                _boldSide = side;
                UiHelpers.SetFontWeight(_spotifyLabel, side == 0 ? UiHelpers.FontWeightBold : UiHelpers.FontWeightRegular);
                UiHelpers.SetFontWeight(_appleLabel, side == 1 ? UiHelpers.FontWeightBold : UiHelpers.FontWeightRegular);
            }
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e is null) return base.OnTouchEvent(e);
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _snapAnim?.Cancel();
                    _downX = e.GetX();
                    _startProg = _progress;
                    _dragging = false;
                    return true;

                case MotionEventActions.Move:
                    var dx = e.GetX() - _downX;
                    if (!_dragging && MathF.Abs(dx) > _touchSlop)
                    {
                        _dragging = true;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                    }
                    if (_dragging)
                    {
                        var slideSpan = Width / 2f - _padPx;
                        if (slideSpan > 1f)
                            SetProgress(_startProg + dx / slideSpan);
                    }
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    var nextStyle = _dragging
                        ? (_progress >= 0.5f ? LyricShareStyle.AppleMusic : LyricShareStyle.Spotify)
                        : e.ActionMasked == MotionEventActions.Up
                            ? (e.GetX() < Width / 2f ? LyricShareStyle.Spotify : LyricShareStyle.AppleMusic)
                            : _currentStyle;
                    var changed = nextStyle != _currentStyle;
                    AnimateToStyle(nextStyle);
                    if (changed)
                        StyleSelected?.Invoke(nextStyle);
                    return true;
            }
            return base.OnTouchEvent(e);
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var w = Width;
            var h = Height;
            if (w <= 0 || h <= 0) return;

            var outerR = h / 2f;
            var borderHalf = _borderPaint.StrokeWidth / 2f;
            var trackRect = new RectF(borderHalf, borderHalf, w - borderHalf, h - borderHalf);
            canvas.DrawRoundRect(trackRect, outerR, outerR, _trackPaint);
            canvas.DrawRoundRect(trackRect, outerR, outerR, _borderPaint);

            var segW = MathF.Max(1f, (w - _padPx * 2f) / 2f);
            var thumbLeft = _padPx + _progress * segW;
            var innerR = MathF.Max(1f, (h - _padPx * 2f) / 2f);
            var thumbRect = new RectF(thumbLeft, _padPx, thumbLeft + segW, h - _padPx);
            canvas.DrawRoundRect(thumbRect, innerR, innerR, _thumbPaint);
        }
    }

    private sealed class EditorBackCallback : Java.Lang.Object, global::Android.Window.IOnBackInvokedCallback
    {
        private readonly WeakReference<LyricShareActivity>? _owner;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(EditorBackCallback))]
        public EditorBackCallback()
        {
        }

        public EditorBackCallback(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public EditorBackCallback(LyricShareActivity owner) => _owner = new WeakReference<LyricShareActivity>(owner);

        public void OnBackInvoked()
        {
            if (_owner is not null && _owner.TryGetTarget(out var owner))
                owner.CloseEditor();
        }
    }

    private sealed class SpotifyToggleView : View
    {
        private readonly Paint _trackPaint = new(PaintFlags.AntiAlias);
        private readonly Paint _thumbPaint = new(PaintFlags.AntiAlias) { Color = Color.White };
        private ValueAnimator? _anim;
        private float _progress;
        private bool _checked;

        public bool Checked => _checked;

        public SpotifyToggleView(Context context, bool initialChecked) : base(context)
        {
            _checked = initialChecked;
            _progress = initialChecked ? 1f : 0f;
            _trackPaint.SetStyle(Paint.Style.Fill);
            _thumbPaint.SetStyle(Paint.Style.Fill);
        }

        public void SetChecked(bool value, bool animate)
        {
            if (_checked == value) return;
            _checked = value;
            var target = value ? 1f : 0f;
            _anim?.Cancel();
            if (!animate)
            {
                _progress = target;
                Invalidate();
                return;
            }

            if (ValueAnimator.OfFloat(_progress, target) is not { } anim)
            {
                _progress = target;
                Invalidate();
                return;
            }
            anim.SetDuration(160);
            anim.SetInterpolator(new DecelerateInterpolator(1.5f));
            anim.Update += (_, e) =>
            {
                if (e.Animation?.AnimatedValue is Java.Lang.Float val)
                {
                    _progress = Math.Clamp(val.FloatValue(), 0f, 1f);
                    Invalidate();
                }
            };
            _anim = anim;
            anim.Start();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var w = Width;
            var h = Height;
            if (w <= 0 || h <= 0) return;

            var r = h / 2f;
            var offColor = Color.Argb(76, 255, 255, 255);
            var onColor = LyricsPalette.AccentGreen;
            _trackPaint.Color = Color.Argb(
                (byte)Math.Round(offColor.A + (onColor.A - offColor.A) * _progress),
                (byte)Math.Round(offColor.R + (onColor.R - offColor.R) * _progress),
                (byte)Math.Round(offColor.G + (onColor.G - offColor.G) * _progress),
                (byte)Math.Round(offColor.B + (onColor.B - offColor.B) * _progress));
            canvas.DrawRoundRect(0f, 0f, w, h, r, r, _trackPaint);

            var pad = MathF.Max(2f, h * 0.14f);
            var thumbR = (h - pad * 2f) / 2f;
            var travel = MathF.Max(0f, w - pad * 2f - thumbR * 2f);
            var cx = pad + thumbR + travel * _progress;
            canvas.DrawCircle(cx, h / 2f, thumbR, _thumbPaint);
        }
    }

    private sealed class SpotifySliderView : View
    {
        private readonly Paint _bgPaint = new(PaintFlags.AntiAlias);
        private readonly Paint _activePaint = new(PaintFlags.AntiAlias) { Color = LyricsPalette.AccentGreen };
        private readonly Paint _haloPaint = new(PaintFlags.AntiAlias) { Color = Color.Argb(48, 255, 255, 255) };
        private readonly Paint _thumbPaint = new(PaintFlags.AntiAlias) { Color = Color.White };
        private ValueAnimator? _dragAnim;
        private int _min;
        private int _max = 100;
        private int _value;
        private float _dragScale;
        private bool _dragging;

        public event Action<int>? ValueChanged;

        public int Value => _value;

        public SpotifySliderView(Context context, int min, int max, int initialValue) : base(context)
        {
            _min = min;
            _max = Math.Max(min + 1, max);
            _value = Math.Clamp(initialValue, _min, _max);
            _bgPaint.Color = Color.Argb(58, 255, 255, 255);
            _bgPaint.SetStyle(Paint.Style.Fill);
            _activePaint.SetStyle(Paint.Style.Fill);
            _haloPaint.SetStyle(Paint.Style.Fill);
            _thumbPaint.SetStyle(Paint.Style.Fill);
            Clickable = true;
            Focusable = true;
        }

        public void SetValue(int value)
        {
            var clamped = Math.Clamp(value, _min, _max);
            if (_value == clamped) return;
            _value = clamped;
            Invalidate();
        }

        public void SetRange(int min, int max, int value)
        {
            _min = min;
            _max = Math.Max(min + 1, max);
            _value = Math.Clamp(value, _min, _max);
            Invalidate();
        }

        private void AnimateDrag(bool active)
        {
            var target = active ? 1f : 0f;
            _dragAnim?.Cancel();
            if (ValueAnimator.OfFloat(_dragScale, target) is not { } anim)
            {
                _dragScale = target;
                Invalidate();
                return;
            }
            anim.SetDuration(active ? 110 : 170);
            anim.SetInterpolator(new DecelerateInterpolator(1.5f));
            anim.Update += (_, e) =>
            {
                if (e.Animation?.AnimatedValue is Java.Lang.Float val)
                {
                    _dragScale = Math.Clamp(val.FloatValue(), 0f, 1f);
                    Invalidate();
                }
            };
            _dragAnim = anim;
            anim.Start();
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e is null || !Enabled) return base.OnTouchEvent(e);
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    _dragging = true;
                    AnimateDrag(true);
                    UpdateFromX(e.GetX());
                    return true;

                case MotionEventActions.Move:
                    if (_dragging)
                        UpdateFromX(e.GetX());
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (_dragging)
                    {
                        if (e.ActionMasked == MotionEventActions.Up)
                            UpdateFromX(e.GetX());
                        _dragging = false;
                        AnimateDrag(false);
                    }
                    return true;
            }
            return base.OnTouchEvent(e);
        }

        private void UpdateFromX(float x)
        {
            var pad = UiHelpers.Dp(Context!, 10);
            var span = MathF.Max(1f, Width - pad * 2f);
            var t = Math.Clamp((x - pad) / span, 0f, 1f);
            var next = (int)MathF.Round(_min + t * (_max - _min));
            if (next != _value)
            {
                _value = next;
                Invalidate();
                ValueChanged?.Invoke(_value);
            }
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var w = Width;
            var h = Height;
            if (w <= 0 || h <= 0) return;

            var pad = UiHelpers.Dp(Context!, 10);
            var span = MathF.Max(1f, w - pad * 2f);
            var cy = h / 2f;
            var trackH = UiHelpers.Dp(Context!, 4);
            var trackR = trackH / 2f;

            var t = Math.Clamp((_value - _min) / (float)(_max - _min), 0f, 1f);
            var thumbX = pad + span * t;

            canvas.DrawRoundRect(pad, cy - trackR, w - pad, cy + trackR, trackR, trackR, _bgPaint);
            if (thumbX > pad)
                canvas.DrawRoundRect(pad, cy - trackR, thumbX, cy + trackR, trackR, trackR, _activePaint);

            if (_dragScale > 0.01f)
            {
                var haloR = UiHelpers.Dp(Context!, 15) * _dragScale;
                _haloPaint.Alpha = (int)(52 * _dragScale);
                canvas.DrawCircle(thumbX, cy, haloR, _haloPaint);
            }

            var thumbR = UiHelpers.Dp(Context!, 7.5f) + UiHelpers.Dp(Context!, 2f) * _dragScale;
            canvas.DrawCircle(thumbX, cy, thumbR, _thumbPaint);
        }
    }

    private const int RequestCodePickImage = 2001;

    internal static void Launch(
        Activity caller,
        int requestCode,
        LyricSharePayload payload,
        ScreenOrientation? overrideOrientation = null)
    {
        using var intent = new Intent(caller, typeof(LyricShareActivity));
        intent.PutExtra("title", payload.TrackTitle);
        intent.PutExtra("artist", payload.ArtistName);
        intent.PutExtra("coverColor", payload.CoverBackground.ToArgb());
        intent.PutExtra("originals", payload.Lines.Select(line => line.Original).ToArray());
        intent.PutExtra("translations", payload.Lines.Select(line => line.Translation ?? "").ToArray());
        intent.PutExtra("showTranslation", payload.DefaultShowTranslation);
        var orientation = overrideOrientation ?? (LyrxApp.Instance.Settings.OrientationMode switch
        {
            "landscape" => ScreenOrientation.SensorLandscape,
            "portrait" => ScreenOrientation.Portrait,
            _ => ScreenOrientation.Behind,
        });
        intent.PutExtra("orientation", (int)orientation);
        if (payload.CoverBitmap is { IsRecycled: false } cover)
        {
            try
            {
                var scale = Math.Min(1f, 960f / Math.Max(cover.Width, cover.Height));
                using var thumbnail = scale < 1f
                    ? Bitmap.CreateScaledBitmap(cover, Math.Max(1, (int)(cover.Width * scale)), Math.Max(1, (int)(cover.Height * scale)), true)
                    : null;
                using var coverUri = ShareImageHelper.SaveTempSharePng(caller, thumbnail ?? cover, "editor-cover");
                intent.PutExtra("cover", coverUri.ToString());
            }
            catch (Exception ex)
            {
                AndroidLog.Write($"[share] 无法保存封面快照: {ex.Message}");
            }
        }
#pragma warning disable CA1422
        caller.StartActivityForResult(intent, requestCode);
        if (!OperatingSystem.IsAndroidVersionAtLeast(34))
            caller.OverridePendingTransition(Resource.Animation.share_page_enter, global::Android.Resource.Animation.FadeOut);
#pragma warning restore CA1422
    }

    private LyricSharePayload _payload = null!;
    private IReadOnlyList<SharePalettePreset> _presets = Array.Empty<SharePalettePreset>();
    private LyricShareStyle _style;
    private int _colorIndex;
    private bool _showTranslation;
    private bool _hasAnyTranslation;
    private bool _isExporting;
    private bool _disposed;
    private bool _isLandscapeLayout;
    private bool _hasExported;

    private Bitmap? _customBgBitmap;
    private Color _customBgThemeColor;
    private string? _customBgUri;
    private int _imageLoadVersion;
    private Bitmap? _previewBitmap;

    private int _insetLeft;
    private int _insetTop;
    private int _insetRight;
    private int _insetBottom;

    private GradientDrawable _pageBgDrawable = null!;
    private LinearLayout _rootLayout = null!;
    private LinearLayout _topBar = null!;
    private LinearLayout _contentArea = null!;
    private FrameLayout _previewStage = null!;
    private ImageView _previewImage = null!;
    private ImageView? _outgoingPreviewImage;
    private Bitmap? _outgoingPreviewBitmap;
    private LinearLayout _controlsArea = null!;
    private ScrollView _controlsScroll = null!;
    private FrameLayout _controlsHost = null!;
    private LinearLayout _toolsRow = null!;
    private LinearLayout _colorTile = null!;
    private View _colorDotView = null!;
    private LinearLayout _imageTile = null!;
    private VectorIconView _imageTileIcon = null!;
    private View _imageTileDot = null!;
    private LinearLayout _fontTile = null!;
    private VectorIconView _fontTileIcon = null!;
    private LinearLayout _actionsHost = null!;
    private LinearLayout _actionsSecondaryRow = null!;
    private LinearLayout _saveBtn = null!;
    private LinearLayout _shareBtn = null!;
    private LinearLayout _copyBtn = null!;
    private LinearLayout _backdropToggleRow = null!;
    private SpotifyToggleView _backdropToggle = null!;

    private StyleSwitchPillView _stylePill = null!;
    private FrameLayout? _translationHost;
    private VectorIconView? _translationIcon;
    private Color _currentNavBarColor = Color.Rgb(16, 16, 16);
    private object? _editorBackCallback;
    private ValueAnimator? _controlsSizeAnimator;

    private float _cardDownX;
    private float _cardDownY;
    private bool _cardDragging;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplyEnterTransitionApi34();

        var intent = Intent;
        if (intent?.GetStringArrayExtra("originals") is not { Length: > 0 } originals)
        {
            Finish();
            return;
        }

        var translations = intent.GetStringArrayExtra("translations") ?? [];
        Bitmap? cover = null;
        if (intent.GetStringExtra("cover") is { } savedCover
            && global::Android.Net.Uri.Parse(savedCover) is { } coverUri)
        {
            using (coverUri)
                cover = DecodeSampledImageFromUri(coverUri, maxDimension: 960);
        }
        _payload = new LyricSharePayload(
            intent.GetStringExtra("title"),
            intent.GetStringExtra("artist"),
            cover,
            new Color(intent.GetIntExtra("coverColor", LyricsPalette.DefaultBackground.ToArgb())),
            originals.Select((line, index) => new ShareLyricLine(line, translations.ElementAtOrDefault(index))).ToArray(),
            intent.GetBooleanExtra("showTranslation", false));
        var orientation = (ScreenOrientation)intent.GetIntExtra("orientation", (int)ScreenOrientation.Behind);
        if (RequestedOrientation != orientation)
            RequestedOrientation = orientation;
        _presets = LyricShareCardRenderer.BuildPresets(_payload.CoverBackground);
        _style = string.Equals(LyrxApp.Instance.Settings.LyricShareStyle, "apple-music", StringComparison.OrdinalIgnoreCase)
            ? LyricShareStyle.AppleMusic
            : LyricShareStyle.Spotify;
        _hasAnyTranslation = _payload.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation));
        _showTranslation = _hasAnyTranslation && (savedInstanceState?.GetBoolean("translation") ?? _payload.DefaultShowTranslation);
        _colorIndex = Math.Clamp(savedInstanceState?.GetInt("color") ?? 0, 0, _presets.Count - 1);
        _hasExported = savedInstanceState?.GetBoolean("exported") == true;
        LoadEditorPreferences();
        _lyricsFont = (ShareLyricsFont)Math.Clamp(savedInstanceState?.GetInt("font") ?? 0, 0, FontLabels.Length - 1);
        var restoredTab = (ShareEditorTab)Math.Clamp(savedInstanceState?.GetInt("editorTab") ?? 0, 0, (int)ShareEditorTab.Typography);

        BuildPageUi();
        SetContentView(_rootLayout);
        ApplyChromeSystemBars();
        ApplyResponsiveLayout(DetectLandscape(Resources?.Configuration));
        if (restoredTab != ShareEditorTab.None)
            OpenEditor(restoredTab, animate: false);
        RefreshAllVisuals();

        if (savedInstanceState?.GetString("customImage") is { } imageUri
            && global::Android.Net.Uri.Parse(imageUri) is { } restoredUri)
            LoadCustomBackground(restoredUri);
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        SaveEditorPreferences();
        outState.PutBoolean("translation", _showTranslation);
        outState.PutInt("color", _colorIndex);
        outState.PutInt("font", (int)_lyricsFont);
        outState.PutBoolean("exported", _hasExported);
        outState.PutString("customImage", _customBgUri);
        outState.PutInt("editorTab", (int)_activeEditor);
        base.OnSaveInstanceState(outState);
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        if (_rootLayout is null) return;
        ApplyResponsiveLayout(DetectLandscape(newConfig));
        if (_activeEditor != ShareEditorTab.None)
            RefreshActiveEditorPanel();
        ApplyChromeSystemBars();
        _rootLayout.RequestApplyInsets();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) ApplyChromeSystemBars();
    }

#pragma warning disable CA1422
    public override void OnBackPressed()
    {
        if (_activeEditor != ShareEditorTab.None)
        {
            CloseEditor();
            return;
        }
        base.OnBackPressed();
    }
#pragma warning restore CA1422

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
#pragma warning disable CA1422
        base.OnActivityResult(requestCode, resultCode, data);
#pragma warning restore CA1422
        if (requestCode != RequestCodePickImage || resultCode != Result.Ok || data?.Data is not { } uri)
            return;

        LoadCustomBackground(uri);
    }

    private void LoadCustomBackground(global::Android.Net.Uri uri)
    {
        var version = ++_imageLoadVersion;
        var uriText = uri.ToString();
        _ = Task.Run(() =>
        {
            using (uri)
            {
                var loaded = DecodeSampledImageFromUri(uri, maxDimension: 1600);
                var theme = loaded is null ? default : ReadCustomImageColor(loaded);
                RunOnUiThread(() =>
                {
                    if (_disposed || IsFinishing || IsDestroyed || version != _imageLoadVersion)
                    {
                        loaded?.Dispose();
                        return;
                    }

                    if (loaded is null)
                    {
                        Toast.MakeText(this, "无法读取所选图片，请换一张重试", ToastLength.Short)?.Show();
                        return;
                    }

                    _customBgBitmap = loaded;
                    _customBgUri = uriText;
                    _customBgThemeColor = theme;
                    _backgroundMode = ShareBackgroundMode.CustomImage;
                    RefreshAllVisuals(crossfade: true);
                    if (_activeEditor != ShareEditorTab.None)
                        RefreshActiveEditorPanel();
                });
            }
        });
    }

    private Color ReadCustomImageColor(Bitmap bitmap)
    {
        try { return LyricsPalette.ShareCardColor(LyricsPalette.FromCover(bitmap).Background); }
        catch (Exception ex)
        {
            AndroidLog.Write($"[share] 无法提取背景图颜色: {ex.Message}");
            return _presets[0].BaseColor;
        }
    }

    public override void Finish()
    {
        SaveEditorPreferences();
        UnregisterEditorBackCallback();
        if (_hasExported) SetResult(Result.Ok);
        base.Finish();
        ApplyExitTransition();
    }

    protected override void OnDestroy()
    {
        _disposed = true;
        SaveEditorPreferences();
        UnregisterEditorBackCallback();
        _controlsSizeAnimator?.Cancel();
        _controlsSizeAnimator?.Dispose();
        _controlsSizeAnimator = null;
        _controlsScroll?.Animate()?.Cancel();
        _editorPanel?.Animate()?.Cancel();
        _editorBody?.Animate()?.Cancel();
        _previewImage?.Animate()?.Cancel();
        CancelPreviewRefresh();
        ClearOutgoingPreview();
        _previewImage?.SetImageDrawable(null);
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        // 导出任务持有自己的图片引用，来源位图在任务结束后由运行时回收。
        _customBgBitmap = null;
        base.OnDestroy();
    }

    private void BuildPageUi()
    {
        _pageBgDrawable = new GradientDrawable(
            GradientDrawable.Orientation.TopBottom,
            [Color.Rgb(12, 14, 13).ToArgb(), Color.Rgb(16, 26, 20).ToArgb(), Color.Rgb(24, 72, 48).ToArgb()]);

        _rootLayout = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Background = _pageBgDrawable,
        };
        _rootLayout.SetOnApplyWindowInsetsListener(new InsetsListener(this));

        // 1. 顶部栏：极简返回箭头 + 样式胶囊 +（可选译文按钮）
        _topBar = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        _topBar.SetGravity(GravityFlags.CenterVertical);

        var backBtn = UiHelpers.IconButton(
            this,
            ClassicIconKind.ArrowLeft,
            Color.White,
            Finish,
            sizeDp: 38,
            iconFraction: 0.60f);
        backBtn.ContentDescription = "返回歌词选择";
        _topBar.AddView(backBtn, new LinearLayout.LayoutParams(Dp(38), Dp(38))
        {
            LeftMargin = Dp(4),
        });

        var switcherCenter = new FrameLayout(this);
        _stylePill = new StyleSwitchPillView(this, _style);
        _stylePill.StyleSelected += nextStyle => SwitchStyle(nextStyle, fromPill: true);

        switcherCenter.AddView(_stylePill, new FrameLayout.LayoutParams(ComputeStylePillWidth(), Dp(36))
        {
            Gravity = GravityFlags.Center,
        });
        _topBar.AddView(switcherCenter, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        if (_hasAnyTranslation)
        {
            var trHost = new FrameLayout(this)
            {
                Clickable = true,
                Focusable = true,
                ContentDescription = "显示或隐藏译文",
            };
            var trIcon = new VectorIconView(this, ClassicIconKind.Bilingual);
            trHost.AddView(trIcon, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            trHost.Click += (_, _) =>
            {
                _showTranslation = !_showTranslation;
                RefreshAllVisuals(crossfade: true);
            };
            AttachPressSpring(trHost);
            _translationHost = trHost;
            _translationIcon = trIcon;
            _topBar.AddView(trHost, new LinearLayout.LayoutParams(Dp(38), Dp(38))
            {
                RightMargin = Dp(4),
            });
        }
        else
        {
            _topBar.AddView(new View(this), new LinearLayout.LayoutParams(Dp(38), Dp(38))
            {
                RightMargin = Dp(4),
            });
        }
        AttachPressSpring(backBtn);

        _rootLayout.AddView(_topBar, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // 2. 主内容区（预览区 + 底部/右侧 Spotify 风格编辑控制区）
        _contentArea = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        _contentArea.LayoutChange += (_, _) => UpdateControlsHostBounds();

        _previewStage = new FrameLayout(this) { Clickable = true };
        _previewStage.LayoutChange += (_, _) => UpdatePreviewImageMaxBounds();
        _previewStage.Touch += OnPreviewStageTouch;

        _previewImage = new ImageView(this)
        {
            ClipToOutline = true,
            Elevation = Dp(16),
        };
        _previewImage.SetAdjustViewBounds(true);
        _previewImage.SetScaleType(ImageView.ScaleType.FitCenter);
        _previewStage.AddView(_previewImage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Center,
        });

        _controlsHost = new FrameLayout(this);

        _controlsArea = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        _controlsArea.SetGravity(GravityFlags.Center);
        _controlsScroll = new ScrollView(this)
        {
            VerticalScrollBarEnabled = false,
            FillViewport = true,
        };
        _controlsScroll.AddView(_controlsArea, new ScrollView.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // 2a. Spotify 风格方圆角工具块：配色 / 背景 / 排版
        _toolsRow = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        _toolsRow.SetGravity(GravityFlags.Center);

        (_colorTile, _colorDotView) = BuildColorToolTile();
        _colorTile.Click += (_, _) => OpenEditor(ShareEditorTab.Color);
        AttachPressSpring(_colorTile);

        (_imageTile, _imageTileIcon, _imageTileDot) = BuildImageToolTile();
        _imageTile.Click += (_, _) => OpenEditor(ShareEditorTab.Background);
        AttachPressSpring(_imageTile);

        (_fontTile, _fontTileIcon) = BuildTypographyToolTile();
        _fontTile.Click += (_, _) => OpenEditor(ShareEditorTab.Typography);
        AttachPressSpring(_fontTile);

        _toolsRow.AddView(_colorTile);
        _toolsRow.AddView(_imageTile);
        _toolsRow.AddView(_fontTile);

        // 2b. 保存、复制和分享
        _actionsHost = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        _actionsSecondaryRow = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        _actionsSecondaryRow.SetGravity(GravityFlags.CenterVertical);

        _saveBtn = BuildBottomActionPill(
            ClassicIconKind.Download,
            "保存",
            Color.Argb(98, 0, 0, 0),
            Color.White,
            Color.Argb(38, 255, 255, 255));
        _saveBtn.Click += (_, _) => ExportAndSaveToGallery();
        _saveBtn.ContentDescription = "保存图片";
        AttachPressSpring(_saveBtn);

        _copyBtn = BuildBottomActionPill(
            ClassicIconKind.Copy,
            "复制",
            Color.Argb(98, 0, 0, 0),
            Color.White,
            Color.Argb(38, 255, 255, 255));
        _copyBtn.Click += (_, _) => ExportAndCopy();
        _copyBtn.ContentDescription = "复制图片";
        AttachPressSpring(_copyBtn);

        _shareBtn = BuildBottomActionPill(
            ClassicIconKind.Share,
            "分享",
            Color.White,
            Color.Rgb(18, 18, 18),
            null);
        _shareBtn.Elevation = Dp(4);
        _shareBtn.Click += (_, _) => ExportAndShareSystem();
        _shareBtn.ContentDescription = "分享图片";
        AttachPressSpring(_shareBtn);

        // 2c. 剪贴板保留背景开关（Spotify 样式开关）
        _backdropToggleRow = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            Clickable = true,
            Focusable = true,
            Background = UiHelpers.RoundedRect(this, Color.Argb(64, 0, 0, 0), 18f),
        };
        _backdropToggleRow.SetGravity(GravityFlags.Center);
        _backdropToggleRow.SetPadding(Dp(14), Dp(7), Dp(12), Dp(7));

        var backdropLabel = new TextView(this)
        {
            Text = "保留卡片外背景",
        };
        backdropLabel.SetIncludeFontPadding(false);
        backdropLabel.SetTextColor(Color.Argb(222, 255, 255, 255));
        backdropLabel.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        UiHelpers.SetFontWeight(backdropLabel, UiHelpers.FontWeightMedium);

        _backdropToggle = new SpotifyToggleView(this, _clipboardKeepBackdrop);
        _backdropToggleRow.AddView(backdropLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            RightMargin = Dp(10),
        });
        _backdropToggleRow.AddView(_backdropToggle, new LinearLayout.LayoutParams(Dp(36), Dp(20)));
        _backdropToggleRow.ContentDescription = "保留卡片外背景";
        _backdropToggleRow.Click += (_, _) =>
        {
            _clipboardKeepBackdrop = !_clipboardKeepBackdrop;
            _backdropToggle.SetChecked(_clipboardKeepBackdrop, animate: true);
            var settings = LyrxApp.Instance.Settings;
            settings.LyricShareClipboardKeepBackdrop = _clipboardKeepBackdrop;
            settings.Save();
        };
        AttachPressSpring(_backdropToggleRow);

        _controlsArea.AddView(_toolsRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(14),
        });
        _controlsArea.AddView(_actionsHost, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(10),
        });
        _controlsArea.AddView(_backdropToggleRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        _controlsHost.AddView(_controlsScroll, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Center,
        });
        _editorPanel = BuildEditorPanel();
        _controlsHost.AddView(_editorPanel, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Center,
        });

        _rootLayout.AddView(_contentArea, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));
    }

    private int ComputeStylePillWidth()
    {
        var screenW = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
        var isTablet = UiHelpers.IsTabletLayout(this);
        var labelW = _stylePill is not null ? _stylePill.MeasureLongestLabelWidthPx() : Dp(73);
        var compactTarget = (int)MathF.Ceiling(labelW * 2f + Dp(44));
        var reservedSide = _insetLeft + _insetRight + Dp(isTablet ? 156 : 148);
        var maxW = Math.Max(Dp(156), Math.Min(Dp(216), screenW - reservedSide));
        var minW = Math.Min(Dp(172), maxW);
        return Math.Clamp(compactTarget, minW, maxW);
    }

    private void ApplyResponsiveLayout(bool isLandscape)
    {
        _isLandscapeLayout = isLandscape;
        var isTablet = UiHelpers.IsTabletLayout(this);

        _controlsSizeAnimator?.Cancel();
        _controlsScroll.Animate()?.Cancel();
        _editorPanel.Animate()?.Cancel();
        _controlsScroll.Alpha = 1f;
        _controlsScroll.TranslationX = 0f;
        _controlsScroll.TranslationY = 0f;
        _editorPanel.Alpha = 1f;
        _editorPanel.TranslationX = 0f;
        _editorPanel.TranslationY = 0f;
        _controlsScroll.Visibility = _activeEditor == ShareEditorTab.None ? ViewStates.Visible : ViewStates.Gone;
        _editorPanel.Visibility = _activeEditor == ShareEditorTab.None ? ViewStates.Gone : ViewStates.Visible;

        if (_stylePill.LayoutParameters is FrameLayout.LayoutParams pillLp)
        {
            var nextPillW = ComputeStylePillWidth();
            var nextPillH = Dp(isLandscape && !isTablet ? 34 : 36);
            if (pillLp.Width != nextPillW || pillLp.Height != nextPillH)
            {
                pillLp.Width = nextPillW;
                pillLp.Height = nextPillH;
                _stylePill.LayoutParameters = pillLp;
            }
        }

        ApplyToolTileDimensions(isLandscape, isTablet);
        LayoutActionButtons(isLandscape, isTablet);

        if (_previewStage.Parent is ViewGroup p1) p1.RemoveView(_previewStage);
        if (_controlsHost.Parent is ViewGroup p2) p2.RemoveView(_controlsHost);
        _contentArea.RemoveAllViews();

        var dmW = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
        var sidePad = Dp(isTablet ? 24 : 16);
        var availW = Math.Max(Dp(300), dmW - _insetLeft - _insetRight - sidePad * 2);

        if (isLandscape)
        {
            _contentArea.Orientation = global::Android.Widget.Orientation.Horizontal;
            _contentArea.AddView(_previewStage, new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.MatchParent, 1.15f)
            {
                RightMargin = Dp(isTablet ? 24 : 14),
            });

            var rightColW = isTablet
                ? Math.Clamp((int)(availW * 0.38f), Dp(340), Dp(420))
                : Math.Clamp((int)(availW * 0.45f), Dp(282), Dp(368));
            _contentArea.AddView(_controlsHost, new LinearLayout.LayoutParams(
                rightColW, ViewGroup.LayoutParams.MatchParent)
            {
                Gravity = GravityFlags.CenterVertical,
            });
        }
        else
        {
            _contentArea.Orientation = global::Android.Widget.Orientation.Vertical;
            _contentArea.AddView(_previewStage, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f)
            {
                BottomMargin = Dp(isTablet ? 18 : 10),
            });

            var maxControlsW = _activeEditor == ShareEditorTab.None
                ? Dp(isTablet ? 396 : 348)
                : Dp(isTablet ? 460 : 396);
            var controlsW = Math.Min(maxControlsW, availW);
            _contentArea.AddView(_controlsHost, new LinearLayout.LayoutParams(
                controlsW, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.CenterHorizontal,
            });
        }

        ApplySystemBarInsets(_insetLeft, _insetTop, _insetRight, _insetBottom);
        UpdateControlsHostBounds();
        UpdatePreviewImageMaxBounds();
    }

    private void ApplyToolTileDimensions(bool isLandscape, bool isTablet)
    {
        var tileW = Dp(isTablet ? 78 : (isLandscape ? 70 : 74));
        var tileH = Dp(isTablet ? 74 : (isLandscape ? 66 : 70));
        var tileGap = Dp(isLandscape && !isTablet ? 8 : 10);

        _colorTile.LayoutParameters = new LinearLayout.LayoutParams(tileW, tileH) { RightMargin = tileGap };
        _imageTile.LayoutParameters = new LinearLayout.LayoutParams(tileW, tileH) { RightMargin = tileGap };
        _fontTile.LayoutParameters = new LinearLayout.LayoutParams(tileW, tileH);

        if (_toolsRow.LayoutParameters is LinearLayout.LayoutParams toolsLp)
        {
            toolsLp.BottomMargin = Dp(isLandscape && !isTablet ? 10 : 14);
            _toolsRow.LayoutParameters = toolsLp;
        }
    }

    private void LayoutActionButtons(bool isLandscape, bool isTablet)
    {
        if (_saveBtn.Parent is ViewGroup pSave) pSave.RemoveView(_saveBtn);
        if (_copyBtn.Parent is ViewGroup pCopy) pCopy.RemoveView(_copyBtn);
        if (_shareBtn.Parent is ViewGroup pShare) pShare.RemoveView(_shareBtn);
        if (_actionsSecondaryRow.Parent is ViewGroup pSec) pSec.RemoveView(_actionsSecondaryRow);
        _actionsSecondaryRow.RemoveAllViews();
        _actionsHost.RemoveAllViews();

        if (isLandscape)
        {
            var secH = Dp(isTablet ? 44 : 40);
            var primaryH = Dp(isTablet ? 46 : 42);
            _actionsSecondaryRow.AddView(_saveBtn, new LinearLayout.LayoutParams(0, secH, 1f) { RightMargin = Dp(8) });
            _actionsSecondaryRow.AddView(_copyBtn, new LinearLayout.LayoutParams(0, secH, 1f));
            _actionsHost.AddView(_actionsSecondaryRow, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                BottomMargin = Dp(8),
            });
            _actionsHost.AddView(_shareBtn, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, primaryH));
        }
        else
        {
            var btnH = Dp(isTablet ? 46 : 42);
            _actionsSecondaryRow.AddView(_saveBtn, new LinearLayout.LayoutParams(0, btnH, 1f) { RightMargin = Dp(8) });
            _actionsSecondaryRow.AddView(_copyBtn, new LinearLayout.LayoutParams(0, btnH, 1f) { RightMargin = Dp(8) });
            _actionsSecondaryRow.AddView(_shareBtn, new LinearLayout.LayoutParams(0, btnH, 1.18f));
            _actionsHost.AddView(_actionsSecondaryRow, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        if (_actionsHost.LayoutParameters is LinearLayout.LayoutParams hostLp)
        {
            hostLp.BottomMargin = Dp(isLandscape && !isTablet ? 8 : 10);
            _actionsHost.LayoutParameters = hostLp;
        }
    }

    private void ApplySystemBarInsets(int left, int top, int right, int bottom)
    {
        _insetLeft = left;
        _insetTop = top;
        _insetRight = right;
        _insetBottom = bottom;

        var isTablet = UiHelpers.IsTabletLayout(this);
        var sidePad = Dp(isTablet ? 24 : 16);
        var compactLandscape = _isLandscapeLayout && !isTablet;
        var topSidePad = Dp(isTablet ? 28 : (_isLandscapeLayout ? 28 : 22));
        var minTopClearance = Dp(isTablet ? 14 : (compactLandscape ? 10 : 14));
        var topBarTopPad = Math.Max(top, minTopClearance) + Dp(isTablet ? 6 : (compactLandscape ? 4 : 6));
        _topBar.SetPadding(
            left + topSidePad,
            topBarTopPad,
            right + topSidePad,
            Dp(isTablet ? 10 : (compactLandscape ? 4 : 6)));
        if (_stylePill.LayoutParameters is FrameLayout.LayoutParams pillLp)
        {
            var nextPillW = ComputeStylePillWidth();
            var nextPillH = Dp(compactLandscape ? 34 : 36);
            if (pillLp.Width != nextPillW || pillLp.Height != nextPillH)
            {
                pillLp.Width = nextPillW;
                pillLp.Height = nextPillH;
                _stylePill.LayoutParameters = pillLp;
            }
        }
        _contentArea.SetPadding(
            left + sidePad,
            Dp(isTablet ? 10 : (compactLandscape ? 4 : 6)),
            right + sidePad,
            bottom + Dp(isTablet ? 22 : (compactLandscape ? 10 : 14)));
        UpdateControlsHostBounds();
    }

    private int ComputeUnifiedEditorHeight(int contentH, bool isTablet)
    {
        var maxAllowedH = Math.Max(Dp(200), contentH - Dp(isTablet ? 210 : 185));
        return Math.Clamp(
            (int)(contentH * (isTablet ? 0.42f : 0.44f)),
            Math.Min(Dp(isTablet ? 296 : 276), maxAllowedH),
            Math.Min(Dp(isTablet ? 344 : 306), maxAllowedH));
    }

    private void UpdateControlsHostBounds(bool animateSize = false)
    {
        if (_disposed || _controlsHost is null || _contentArea is null) return;
        var isTablet = UiHelpers.IsTabletLayout(this);
        var dmW = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
        var dmH = Resources?.DisplayMetrics?.HeightPixels ?? 2200;
        var sidePad = Dp(isTablet ? 24 : 16);
        var availW = Math.Max(Dp(300), dmW - _insetLeft - _insetRight - sidePad * 2);

        if (_controlsHost.LayoutParameters is not LinearLayout.LayoutParams hostLp) return;

        var contentH = _contentArea.Height > 0
            ? Math.Max(1, _contentArea.Height - _contentArea.PaddingTop - _contentArea.PaddingBottom)
            : Math.Max(Dp(240), dmH - _insetTop - _insetBottom - Dp(90));

        if (_isLandscapeLayout)
        {
            _controlsSizeAnimator?.Cancel();
            var rightColW = isTablet
                ? Math.Clamp((int)(availW * 0.38f), Dp(340), Dp(420))
                : Math.Clamp((int)(availW * 0.45f), Dp(282), Dp(368));
            var changed = false;
            if (hostLp.Width != rightColW)
            {
                hostLp.Width = rightColW;
                changed = true;
            }
            if (hostLp.Height != ViewGroup.LayoutParams.MatchParent)
            {
                hostLp.Height = ViewGroup.LayoutParams.MatchParent;
                changed = true;
            }
            if (changed) _controlsHost.LayoutParameters = hostLp;

            if (_controlsScroll.LayoutParameters is FrameLayout.LayoutParams scrollLp)
            {
                scrollLp.Height = ViewGroup.LayoutParams.MatchParent;
                _controlsScroll.LayoutParameters = scrollLp;
            }

            if (_editorPanel.LayoutParameters is FrameLayout.LayoutParams edLp)
            {
                var unifiedLandscapeH = contentH <= Dp(isTablet ? 356 : 318)
                    ? ViewGroup.LayoutParams.MatchParent
                    : Dp(isTablet ? 344 : 304);
                if (edLp.Height != unifiedLandscapeH)
                {
                    edLp.Height = unifiedLandscapeH;
                    _editorPanel.LayoutParameters = edLp;
                }
            }
            return;
        }

        var maxControlsW = _activeEditor == ShareEditorTab.None
            ? Dp(isTablet ? 396 : 348)
            : Dp(isTablet ? 460 : 396);
        var controlsW = Math.Min(maxControlsW, availW);
        var targetH = ViewGroup.LayoutParams.WrapContent;
        int measuredTargetH;

        if (_activeEditor != ShareEditorTab.None)
        {
            targetH = measuredTargetH = ComputeUnifiedEditorHeight(contentH, isTablet);
            if (_editorPanel.LayoutParameters is FrameLayout.LayoutParams edLp && edLp.Height != ViewGroup.LayoutParams.MatchParent)
            {
                edLp.Height = ViewGroup.LayoutParams.MatchParent;
                _editorPanel.LayoutParameters = edLp;
            }
        }
        else
        {
            var maxMainH = Math.Max(Dp(160), (int)(contentH * 0.50f));
            _controlsArea.Measure(
                View.MeasureSpec.MakeMeasureSpec(controlsW, MeasureSpecMode.Exactly),
                View.MeasureSpec.MakeMeasureSpec(maxMainH * 2, MeasureSpecMode.AtMost));
            measuredTargetH = Math.Max(Dp(120), Math.Min(_controlsArea.MeasuredHeight, maxMainH));
            targetH = _controlsArea.MeasuredHeight > maxMainH
                ? maxMainH
                : ViewGroup.LayoutParams.WrapContent;

            if (_controlsScroll.LayoutParameters is FrameLayout.LayoutParams scrollLp)
            {
                var nextScrollH = targetH == ViewGroup.LayoutParams.WrapContent
                    ? ViewGroup.LayoutParams.WrapContent
                    : ViewGroup.LayoutParams.MatchParent;
                if (scrollLp.Height != nextScrollH)
                {
                    scrollLp.Height = nextScrollH;
                    _controlsScroll.LayoutParameters = scrollLp;
                }
            }
        }

        var startW = _controlsHost.Width;
        var startH = _controlsHost.Height;
        if (animateSize && startW > 0 && startH > 0 && (startW != controlsW || startH != measuredTargetH))
        {
            _controlsSizeAnimator?.Cancel();
            var finalLayoutH = targetH;
            var isClosingToMain = _activeEditor == ShareEditorTab.None;
            var anim = ValueAnimator.OfFloat(0f, 1f);
            if (anim is not null)
            {
                anim.SetDuration(220);
                anim.SetInterpolator(new DecelerateInterpolator(1.65f));
                anim.Update += (_, e) =>
                {
                    if (_disposed || _controlsHost.LayoutParameters is not LinearLayout.LayoutParams curLp) return;
                    var t = e.Animation?.AnimatedFraction ?? 1f;
                    curLp.Width = (int)Math.Round(startW + (controlsW - startW) * t);
                    curLp.Height = (int)Math.Round(startH + (measuredTargetH - startH) * t);
                    _controlsHost.LayoutParameters = curLp;
                    UpdatePreviewImageMaxBounds();
                };
                anim.AnimationEnd += (_, _) =>
                {
                    if (_disposed || _controlsHost.LayoutParameters is not LinearLayout.LayoutParams endLp) return;
                    if ((_activeEditor == ShareEditorTab.None) != isClosingToMain) return;
                    endLp.Width = controlsW;
                    endLp.Height = finalLayoutH;
                    _controlsHost.LayoutParameters = endLp;
                    UpdatePreviewImageMaxBounds();
                };
                _controlsSizeAnimator = anim;
                anim.Start();
                return;
            }
        }

        _controlsSizeAnimator?.Cancel();
        if (hostLp.Width != controlsW || hostLp.Height != targetH)
        {
            hostLp.Width = controlsW;
            hostLp.Height = targetH;
            _controlsHost.LayoutParameters = hostLp;
        }
    }

    private void UpdatePreviewImageMaxBounds()
    {
        if (_disposed || _previewStage.Width <= 0 || _previewStage.Height <= 0) return;
        var isTablet = UiHelpers.IsTabletLayout(this);
        var maxSideCap = isTablet ? Dp(520) : Dp(440);
        var maxW = Math.Min(_previewStage.Width - Dp(12), maxSideCap);
        var maxH = Math.Min(_previewStage.Height - Dp(8), isTablet ? Dp(540) : _previewStage.Height - Dp(8));
        _previewImage.SetMaxWidth(Math.Max(1, maxW));
        _previewImage.SetMaxHeight(Math.Max(1, maxH));
    }

    private (LinearLayout Tile, View Dot) BuildColorToolTile()
    {
        var tile = MakeToolTileHost();
        var dot = new View(this);
        tile.AddView(dot, new LinearLayout.LayoutParams(Dp(26), Dp(26))
        {
            BottomMargin = Dp(6),
        });
        tile.AddView(MakeToolTileLabel("配色"));
        return (tile, dot);
    }

    private (LinearLayout Tile, VectorIconView Icon, View ActiveDot) BuildImageToolTile()
    {
        var tile = MakeToolTileHost();
        var iconBox = new FrameLayout(this);
        var icon = new VectorIconView(this, ClassicIconKind.SpotifyImage);
        icon.Configure(ClassicIconKind.SpotifyImage, Color.White, iconFraction: 0.94f);
        iconBox.AddView(icon, new FrameLayout.LayoutParams(Dp(26), Dp(26))
        {
            Gravity = GravityFlags.Center,
        });
        var dot = new View(this)
        {
            Background = UiHelpers.RoundedRect(this, LyricsPalette.AccentGreen, 3f),
            Visibility = ViewStates.Gone,
        };
        iconBox.AddView(dot, new FrameLayout.LayoutParams(Dp(6), Dp(6))
        {
            Gravity = GravityFlags.Top | GravityFlags.Right,
        });
        tile.AddView(iconBox, new LinearLayout.LayoutParams(Dp(30), Dp(26))
        {
            BottomMargin = Dp(6),
        });
        tile.AddView(MakeToolTileLabel("背景"));
        return (tile, icon, dot);
    }

    private (LinearLayout Tile, VectorIconView Icon) BuildTypographyToolTile()
    {
        var tile = MakeToolTileHost();
        var icon = new VectorIconView(this, ClassicIconKind.SpotifyTypography);
        icon.Configure(ClassicIconKind.SpotifyTypography, Color.White, iconFraction: 0.94f);
        tile.AddView(icon, new LinearLayout.LayoutParams(Dp(26), Dp(26))
        {
            BottomMargin = Dp(6),
        });
        tile.AddView(MakeToolTileLabel("排版"));
        return (tile, icon);
    }

    private LinearLayout MakeToolTileHost()
    {
        var tile = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Clickable = true,
            Focusable = true,
            Background = UiHelpers.RoundedRect(this, Color.Argb(92, 0, 0, 0), 12f),
        };
        tile.SetGravity(GravityFlags.Center);
        tile.SetPadding(Dp(6), Dp(8), Dp(6), Dp(8));
        return tile;
    }

    private TextView MakeToolTileLabel(string text)
    {
        var tv = new TextView(this)
        {
            Text = text,
            Gravity = GravityFlags.Center,
        };
        tv.SetIncludeFontPadding(false);
        tv.SetMaxLines(1);
        tv.SetTextColor(Color.Argb(242, 255, 255, 255));
        tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
        UiHelpers.SetFontWeight(tv, UiHelpers.FontWeightMedium);
        return tv;
    }

    private LinearLayout BuildBottomActionPill(
        ClassicIconKind iconKind,
        string labelText,
        Color bgColor,
        Color fgColor,
        Color? strokeColor)
    {
        var pill = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            Background = strokeColor is { } s
                ? UiHelpers.RoundedRect(this, bgColor, 22f, s, 1f)
                : UiHelpers.RoundedRect(this, bgColor, 22f),
            Clickable = true,
            Focusable = true,
        };
        pill.SetGravity(GravityFlags.Center);
        pill.SetPadding(Dp(10), 0, Dp(10), 0);

        var icon = new VectorIconView(this, iconKind);
        icon.Configure(iconKind, fgColor, iconFraction: 0.78f);
        pill.AddView(icon, new LinearLayout.LayoutParams(Dp(17), Dp(17))
        {
            RightMargin = Dp(5),
        });

        var tv = new TextView(this) { Text = labelText };
        tv.SetIncludeFontPadding(false);
        tv.SetMaxLines(1);
        tv.SetTextColor(fgColor);
        tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14f);
        UiHelpers.SetFontWeight(tv, UiHelpers.FontWeightBold);
        pill.AddView(tv);
        return pill;
    }

    private void SwitchStyle(LyricShareStyle nextStyle, bool fromPill = false)
    {
        if (_style == nextStyle)
        {
            if (!fromPill)
                _stylePill.AnimateToStyle(_style);
            return;
        }

        var animateDirection = nextStyle == LyricShareStyle.AppleMusic ? 1 : -1;
        _style = nextStyle;
        if (!fromPill)
            _stylePill.AnimateToStyle(nextStyle);

        LyrxApp.Instance.Settings.LyricShareStyle = nextStyle == LyricShareStyle.AppleMusic
            ? "apple-music"
            : "spotify";
        LyrxApp.Instance.SaveSettings();
        RefreshAllVisuals(animateDirection);
    }

    private void OnPreviewStageTouch(object? sender, View.TouchEventArgs e)
    {
        var ev = e.Event;
        if (ev is null)
        {
            e.Handled = false;
            return;
        }

        var touchSlop = ViewConfiguration.Get(this)?.ScaledTouchSlop ?? Dp(8);
        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down:
                _cardDownX = ev.RawX;
                _cardDownY = ev.RawY;
                _cardDragging = false;
                _previewImage.Animate()?.Cancel();
                e.Handled = true;
                break;

            case MotionEventActions.Move:
                var dx = ev.RawX - _cardDownX;
                var dy = ev.RawY - _cardDownY;
                if (!_cardDragging && MathF.Abs(dx) > touchSlop && MathF.Abs(dx) > MathF.Abs(dy) * 1.1f)
                    _cardDragging = true;

                if (_cardDragging)
                {
                    var towardOther = (_style == LyricShareStyle.Spotify && dx < 0)
                        || (_style == LyricShareStyle.AppleMusic && dx > 0);
                    _previewImage.TranslationX = towardOther ? dx * 0.55f : dx * 0.16f;
                    _previewImage.Alpha = towardOther
                        ? Math.Clamp(1f - MathF.Abs(dx) / (Dp(240) * 1.8f), 0.55f, 1f)
                        : 1f;

                    if (towardOther)
                    {
                        var span = Math.Max(Dp(120), _previewStage.Width * 0.45f);
                        var dragFrac = Math.Clamp(MathF.Abs(dx) / span, 0f, 0.48f);
                        _stylePill.SetProgress(_style == LyricShareStyle.Spotify ? dragFrac : 1f - dragFrac);
                    }
                }
                e.Handled = true;
                break;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                if (_cardDragging)
                {
                    var totalDx = ev.RawX - _cardDownX;
                    var threshold = Math.Max(Dp(46), _previewStage.Width * 0.15f);
                    if (ev.ActionMasked == MotionEventActions.Up && _style == LyricShareStyle.Spotify && totalDx < -threshold)
                    {
                        SwitchStyle(LyricShareStyle.AppleMusic, fromPill: false);
                    }
                    else if (ev.ActionMasked == MotionEventActions.Up && _style == LyricShareStyle.AppleMusic && totalDx > threshold)
                    {
                        SwitchStyle(LyricShareStyle.Spotify, fromPill: false);
                    }
                    else
                    {
                        _previewImage.Animate()
                            ?.TranslationX(0f)
                            ?.Alpha(1f)
                            ?.SetDuration(180)
                            ?.SetInterpolator(new DecelerateInterpolator(1.5f))
                            ?.Start();
                        _stylePill.AnimateToStyle(_style);
                    }
                }
                else if (ev.ActionMasked == MotionEventActions.Up
                    && _activeEditor != ShareEditorTab.None
                    && MathF.Abs(ev.RawX - _cardDownX) < touchSlop
                    && MathF.Abs(ev.RawY - _cardDownY) < touchSlop)
                {
                    CloseEditor();
                }
                _cardDragging = false;
                e.Handled = true;
                break;
        }
    }

    private static void AttachPressSpring(View view)
    {
        view.Touch += (s, e) =>
        {
            if (s is not View target || e.Event is not { } ev || !target.Enabled)
            {
                e.Handled = false;
                return;
            }
            switch (ev.ActionMasked)
            {
                case MotionEventActions.Down:
                    target.Animate()?.Cancel();
                    target.Animate()
                        ?.ScaleX(0.95f)
                        ?.ScaleY(0.95f)
                        ?.SetDuration(75)
                        ?.SetInterpolator(new DecelerateInterpolator(1.4f))
                        ?.Start();
                    break;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    target.Animate()?.Cancel();
                    target.Animate()
                        ?.ScaleX(1f)
                        ?.ScaleY(1f)
                        ?.SetDuration(165)
                        ?.SetInterpolator(new OvershootInterpolator(1.2f))
                        ?.Start();
                    break;
            }
            e.Handled = false;
        };
    }

    private SharePalettePreset SelectedPalette => _backgroundMode == ShareBackgroundMode.CustomImage && _colorIndex == 0
        ? new SharePalettePreset("图片取色", _customBgThemeColor, LyricsPalette.BlendColor(_customBgThemeColor, Color.Black, 0.28))
        : _presets[_colorIndex];

    private ShareCardOptions BuildCurrentOptions()
    {
        var palette = SelectedPalette;
        return new ShareCardOptions(
            _style,
            palette.BaseColor,
            _showTranslation,
            _payload.TrackTitle,
            _payload.ArtistName,
            _payload.CoverBitmap,
            _payload.Lines,
            _customBgBitmap,
            _backgroundMode,
            palette.GradientEndColor,
            _coverEffect,
            _lyricsFont,
            _fontPercent / 100f);
    }

    private void RefreshAllVisuals(int animateDirection = 0, bool crossfade = false)
    {
        CancelPreviewRefresh();
        var isSpotify = _style == LyricShareStyle.Spotify;

        if (_translationHost is not null && _translationIcon is not null)
        {
            _translationHost.Background = _showTranslation
                ? UiHelpers.RoundedRect(this, LyricsPalette.AccentGreen, 19f)
                : UiHelpers.RoundedRect(this, Color.Argb(96, 0, 0, 0), 19f, Color.Argb(36, 255, 255, 255), 1f);
            _translationIcon.Configure(
                ClassicIconKind.Bilingual,
                _showTranslation ? Color.Rgb(18, 18, 18) : Color.White,
                iconFraction: 0.56f);
            _translationHost.Selected = _showTranslation;
        }

        var themeColor = SelectedPalette.BaseColor;
        var isImageBackground = _backgroundMode is ShareBackgroundMode.CustomImage or ShareBackgroundMode.AlbumCover;
        _stylePill.SetMonetPalette(themeColor, _payload.CoverBackground);

        var isLightTheme = LyricsPalette.RelativeLuminance(themeColor) > 0.24;
        var topDark = LyricsPalette.BlendColor(themeColor, Color.Rgb(10, 12, 11), 0.88);
        var midHue = LyricsPalette.BlendColor(themeColor, Color.Rgb(12, 14, 13), isLightTheme ? 0.68 : 0.50);
        var bottomHue = LyricsPalette.BlendColor(themeColor, Color.Rgb(12, 14, 13), isLightTheme ? 0.44 : 0.16);
        _pageBgDrawable.SetColors([topDark.ToArgb(), midHue.ToArgb(), bottomHue.ToArgb()]);
        if (_currentNavBarColor != bottomHue)
        {
            _currentNavBarColor = bottomHue;
            ApplyChromeSystemBars();
        }

        _colorDotView.Background = UiHelpers.RoundedRect(
            this,
            themeColor,
            13f,
            Color.White,
            1.8f);

        _colorTile.Background = UiHelpers.RoundedRect(
            this,
            Color.Argb(_activeEditor == ShareEditorTab.Color ? 148 : 92, 0, 0, 0),
            12f,
            _activeEditor == ShareEditorTab.Color ? Color.Argb(110, 255, 255, 255) : null,
            1f);

        _imageTile.Background = UiHelpers.RoundedRect(
            this,
            Color.Argb(_activeEditor == ShareEditorTab.Background ? 148 : 92, 0, 0, 0),
            12f,
            isImageBackground ? Color.Argb(100, 30, 215, 96) : null,
            1f);
        _imageTile.ContentDescription = $"背景 · {BackgroundLabel(_backgroundMode)}";
        _imageTileIcon.Configure(
            ClassicIconKind.SpotifyImage,
            isImageBackground ? LyricsPalette.AccentGreen : Color.White,
            iconFraction: 0.94f);
        _imageTileDot.Visibility = isImageBackground ? ViewStates.Visible : ViewStates.Gone;

        _fontTile.Background = UiHelpers.RoundedRect(
            this,
            Color.Argb(_activeEditor == ShareEditorTab.Typography ? 148 : 92, 0, 0, 0),
            12f,
            _activeEditor == ShareEditorTab.Typography ? Color.Argb(110, 255, 255, 255) : null,
            1f);
        if (_activeEditor == ShareEditorTab.Color)
            SyncColorEditorDots();

        var nextBmp = LyricShareCardRenderer.Render(this, BuildCurrentOptions(), forPreview: true);
        var oldBmp = _previewBitmap;
        var previewRadiusDp = isSpotify ? 16f : 14f;

        ClearOutgoingPreview();

        if (animateDirection != 0 && oldBmp is { IsRecycled: false } && _previewImage.Width > 0 && _previewImage.Height > 0)
        {
            var outgoing = new ImageView(this)
            {
                ClipToOutline = true,
                OutlineProvider = _previewImage.OutlineProvider,
                Elevation = Dp(12),
                TranslationX = _previewImage.TranslationX,
                Alpha = _previewImage.Alpha,
            };
            outgoing.SetScaleType(ImageView.ScaleType.FitCenter);
            outgoing.SetImageBitmap(oldBmp);

            var outLp = new FrameLayout.LayoutParams(_previewImage.Width, _previewImage.Height)
            {
                Gravity = GravityFlags.Center,
            };
            var incomingIndex = _previewStage.IndexOfChild(_previewImage);
            _previewStage.AddView(outgoing, Math.Max(0, incomingIndex), outLp);

            _outgoingPreviewImage = outgoing;
            _outgoingPreviewBitmap = oldBmp;

            var slidePx = Dp(44);
            outgoing.Animate()
                ?.TranslationX(-animateDirection * slidePx)
                ?.ScaleX(0.94f)
                ?.ScaleY(0.94f)
                ?.Alpha(0f)
                ?.SetDuration(220)
                ?.SetInterpolator(new DecelerateInterpolator(1.6f))
                ?.WithEndAction(new Java.Lang.Runnable(() =>
                {
                    if (ReferenceEquals(_outgoingPreviewImage, outgoing))
                        ClearOutgoingPreview();
                }))
                ?.Start();

            _previewBitmap = nextBmp;
            _previewImage.OutlineProvider = new RoundedOutlineProvider(Dp(previewRadiusDp));
            _previewImage.SetImageBitmap(nextBmp);
            _previewImage.Animate()?.Cancel();
            _previewImage.TranslationX = animateDirection * slidePx;
            _previewImage.ScaleX = 0.94f;
            _previewImage.ScaleY = 0.94f;
            _previewImage.Alpha = 0f;
            _previewImage.Animate()
                ?.TranslationX(0f)
                ?.ScaleX(1f)
                ?.ScaleY(1f)
                ?.Alpha(1f)
                ?.SetDuration(240)
                ?.SetInterpolator(new DecelerateInterpolator(1.6f))
                ?.Start();
        }
        else if (crossfade && oldBmp is { IsRecycled: false }
            && _previewImage.Width > 0 && _previewImage.Height > 0
            && oldBmp.Width == nextBmp.Width && oldBmp.Height == nextBmp.Height)
        {
            var outgoing = new ImageView(this)
            {
                ClipToOutline = true,
                OutlineProvider = _previewImage.OutlineProvider,
                Elevation = Dp(14),
            };
            outgoing.SetScaleType(ImageView.ScaleType.FitCenter);
            outgoing.SetImageBitmap(oldBmp);

            var outLp = new FrameLayout.LayoutParams(_previewImage.Width, _previewImage.Height)
            {
                Gravity = GravityFlags.Center,
            };
            var incomingIndex = _previewStage.IndexOfChild(_previewImage);
            _previewStage.AddView(outgoing, Math.Max(0, incomingIndex), outLp);

            _outgoingPreviewImage = outgoing;
            _outgoingPreviewBitmap = oldBmp;

            _previewBitmap = nextBmp;
            _previewImage.OutlineProvider = new RoundedOutlineProvider(Dp(previewRadiusDp));
            _previewImage.SetImageBitmap(nextBmp);
            _previewImage.Animate()?.Cancel();
            _previewImage.TranslationX = 0f;
            _previewImage.ScaleX = 1f;
            _previewImage.ScaleY = 1f;
            _previewImage.Alpha = 0f;
            _previewImage.Animate()
                ?.Alpha(1f)
                ?.SetDuration(145)
                ?.SetInterpolator(new DecelerateInterpolator(1.4f))
                ?.WithEndAction(new Java.Lang.Runnable(() =>
                {
                    if (ReferenceEquals(_outgoingPreviewImage, outgoing))
                        ClearOutgoingPreview();
                }))
                ?.Start();
        }
        else
        {
            _previewBitmap = nextBmp;
            _previewImage.OutlineProvider = new RoundedOutlineProvider(Dp(previewRadiusDp));
            _previewImage.Animate()?.Cancel();
            _previewImage.TranslationX = 0f;
            _previewImage.ScaleX = 1f;
            _previewImage.ScaleY = 1f;
            _previewImage.Alpha = 1f;
            _previewImage.SetImageBitmap(nextBmp);
            oldBmp?.Dispose();
        }
    }

    private void ClearOutgoingPreview()
    {
        if (_outgoingPreviewImage is { } oldView)
        {
            _outgoingPreviewImage = null;
            oldView.Animate()?.Cancel();
            oldView.SetImageDrawable(null);
            _previewStage?.RemoveView(oldView);
        }
        _outgoingPreviewBitmap?.Dispose();
        _outgoingPreviewBitmap = null;
    }

    private void OpenCustomBackgroundPicker()
    {
        try
        {
#pragma warning disable CA1422
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                using var picker = new Intent(MediaStore.ActionPickImages);
                picker.SetType("image/*");
                try
                {
                    StartActivityForResult(picker, RequestCodePickImage);
                    return;
                }
                catch (ActivityNotFoundException) { }
            }

            using var intent = new Intent(Intent.ActionOpenDocument);
            intent.SetType("image/*");
            intent.AddCategory(Intent.CategoryOpenable);
            StartActivityForResult(intent, RequestCodePickImage);
#pragma warning restore CA1422
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"无法打开相册：{ex.Message}", ToastLength.Short)?.Show();
        }
    }

    private Bitmap? DecodeSampledImageFromUri(global::Android.Net.Uri uri, int maxDimension)
    {
        try
        {
            using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            using (var s1 = ContentResolver?.OpenInputStream(uri))
            {
                if (s1 is null) return null;
                BitmapFactory.DecodeStream(s1, null, bounds);
            }

            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

            var sample = 1;
            var longest = Math.Max(bounds.OutWidth, bounds.OutHeight);
            while (longest / sample > maxDimension)
                sample *= 2;

            using var opts = new BitmapFactory.Options { InSampleSize = sample, InPreferredConfig = Bitmap.Config.Argb8888 };
            Bitmap? decoded;
            using (var s2 = ContentResolver?.OpenInputStream(uri))
            {
                if (s2 is null) return null;
                decoded = BitmapFactory.DecodeStream(s2, null, opts);
            }
            if (decoded is null) return null;

            var exifOrientation = global::Android.Media.Orientation.Normal;
            try
            {
                using var s3 = ContentResolver?.OpenInputStream(uri);
                if (s3 is not null)
                {
                    using var exif = new global::Android.Media.ExifInterface(s3);
                    exifOrientation = (global::Android.Media.Orientation)exif.GetAttributeInt(
                        global::Android.Media.ExifInterface.TagOrientation,
                        (int)global::Android.Media.Orientation.Normal);
                }
            }
            catch { }

            using var matrix = new Matrix();
            switch (exifOrientation)
            {
                case global::Android.Media.Orientation.Rotate90:
                    matrix.PostRotate(90f);
                    break;
                case global::Android.Media.Orientation.Rotate180:
                    matrix.PostRotate(180f);
                    break;
                case global::Android.Media.Orientation.Rotate270:
                    matrix.PostRotate(270f);
                    break;
                case global::Android.Media.Orientation.FlipHorizontal:
                    matrix.PostScale(-1f, 1f);
                    break;
                case global::Android.Media.Orientation.FlipVertical:
                    matrix.PostScale(1f, -1f);
                    break;
                case global::Android.Media.Orientation.Transpose:
                    matrix.PostScale(-1f, 1f);
                    matrix.PostRotate(270f);
                    break;
                case global::Android.Media.Orientation.Transverse:
                    matrix.PostScale(-1f, 1f);
                    matrix.PostRotate(90f);
                    break;
                default:
                    return decoded;
            }

            var rotated = Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true);
            if (rotated is not null && !ReferenceEquals(rotated, decoded))
            {
                decoded.Dispose();
                return rotated;
            }
            return decoded;
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[share] 读取自定义背景图失败: {ex.Message}");
            return null;
        }
    }

    private void ExportAndSaveToGallery()
    {
        if (_isExporting) return;
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)
            && CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) != Permission.Granted)
        {
            RequestPermissions([global::Android.Manifest.Permission.WriteExternalStorage], RequestCodeSaveToGallery);
            return;
        }

        RunExport("保存图片",
            (bitmap, fileName) => ShareImageHelper.SaveToGallery(this, bitmap, fileName),
            path => Toast.MakeText(this, $"已保存到相册 {path}", ToastLength.Short)?.Show());
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != RequestCodeSaveToGallery || _disposed || IsFinishing || IsDestroyed) return;
        if (grantResults is [Permission.Granted])
            ExportAndSaveToGallery();
        else
            Toast.MakeText(this, "需要存储权限才能保存到系统相册。", ToastLength.Short)?.Show();
    }

    private void ExportAndShareSystem() =>
        RunExport("分享图片",
            (bitmap, fileName) => ShareImageHelper.SaveTempSharePng(this, bitmap, fileName),
            uri =>
            {
                using var send = new Intent(Intent.ActionSend);
                send.SetType("image/png");
                send.PutExtra(Intent.ExtraStream, uri);
                send.PutExtra(Intent.ExtraTitle, _payload.TrackTitle ?? "Lyrx 歌词卡片");
                using var clip = ClipData.NewRawUri("Lyrx", uri);
                send.ClipData = clip;
                send.AddFlags(ActivityFlags.GrantReadUriPermission);
                using var chooser = Intent.CreateChooser(send, "分享歌词卡片");
                StartActivity(chooser);
            });

    private void ExportAndCopy() =>
        RunExport("复制图片",
            (bitmap, fileName) => ShareImageHelper.SaveTempSharePng(this, bitmap, fileName, forClipboard: true),
            uri =>
            {
                var clipboard = GetSystemService(ClipboardService) as ClipboardManager
                    ?? throw new InvalidOperationException("系统剪贴板不可用");
                using var clip = ClipData.NewUri(ContentResolver!, "Lyrx 歌词卡片", uri);
                clipboard.PrimaryClip = clip;
                if (!OperatingSystem.IsAndroidVersionAtLeast(33))
                    Toast.MakeText(this, "已复制歌词卡片", ToastLength.Short)?.Show();
            });

    private async void RunExport<T>(string action, Func<Bitmap, string, T> write, Action<T> onSuccess)
    {
        if (_isExporting) return;
        SaveEditorPreferences();
        var opt = BuildCurrentOptions();
        var baseName = string.IsNullOrWhiteSpace(_payload.TrackTitle) ? "Lyrx_Lyrics" : _payload.TrackTitle!;
        var keepBackdrop = _clipboardKeepBackdrop;
        SetExportBusy(true);

        try
        {
            var result = await Task.Run(() =>
            {
                using var bitmap = LyricShareCardRenderer.RenderExport(this, opt, keepBackdrop);
                return write(bitmap, baseName);
            });
            using var disposable = result as IDisposable;
            if (_disposed || IsFinishing || IsDestroyed) return;
            onSuccess(result);
            _hasExported = true;
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[share] {action}失败: {ex}");
            if (!_disposed && !IsFinishing)
                Toast.MakeText(this, $"{action}失败：{ex.Message}", ToastLength.Short)?.Show();
        }
        finally
        {
            if (!_disposed && !IsFinishing)
                SetExportBusy(false);
        }
    }

    private void SetExportBusy(bool busy)
    {
        _isExporting = busy;
        _saveBtn.Enabled = _copyBtn.Enabled = _shareBtn.Enabled = !busy;
        _saveBtn.Alpha = _copyBtn.Alpha = _shareBtn.Alpha = busy ? 0.6f : 1f;
    }

    private void ApplyChromeSystemBars()
    {
        try
        {
            UiHelpers.HideStatusBar(
                Window,
                edgeToEdge: true,
                navigationBarColor: _currentNavBarColor,
                hideNavigationBar: LyrxApp.Instance.Settings.HideNavigationBar);
        }
        catch { }
    }

    private bool DetectLandscape(Configuration? config)
    {
        var orientation = config?.Orientation ?? Resources?.Configuration?.Orientation;
        if (orientation == global::Android.Content.Res.Orientation.Landscape) return true;
        if (orientation == global::Android.Content.Res.Orientation.Portrait) return false;
        var dm = Resources?.DisplayMetrics;
        return dm is not null && dm.WidthPixels > dm.HeightPixels;
    }

    private int Dp(float dp) => UiHelpers.Dp(this, dp);

    private void ApplyEnterTransitionApi34()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(34)) return;
        OverrideActivityTransition(
            OverrideTransition.Open,
            Resource.Animation.share_page_enter,
            global::Android.Resource.Animation.FadeOut);
    }

    private void ApplyExitTransition()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            OverrideActivityTransition(
                OverrideTransition.Close,
                global::Android.Resource.Animation.FadeIn,
                Resource.Animation.share_page_exit);
            return;
        }
#pragma warning disable CA1422
        OverridePendingTransition(global::Android.Resource.Animation.FadeIn, Resource.Animation.share_page_exit);
#pragma warning restore CA1422
    }
}
