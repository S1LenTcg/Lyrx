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
/// 独立全屏歌词分享编辑页：
/// 顶部含大圆角卡片样式切换（Spotify / Apple Music），中部实时高分辨率卡片预览，
/// 底部提供「颜色」（悬浮色板弹窗）与「图像」（自定义背景图）以及「保存图片」「分享图片」。
/// </summary>
[Activity(
    Label = "歌词分享",
    Theme = "@style/LyrxTheme",
    Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode)]
public sealed class LyricShareActivity : Activity
{
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
            owner.ApplySystemBarInsets(bars.Left, bars.Top, bars.Right, bars.Bottom);
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
            var (thumbColor, activeInk) = LyricsPalette.SoftMonetPill(themeColor, coverHueFallback);
            var trackTint = LyricsPalette.BlendColor(Color.Rgb(24, 24, 26), themeColor, 0.16);
            var borderTint = LyricsPalette.BlendColor(Color.Rgb(86, 86, 92), thumbColor, 0.34);

            _trackPaint.Color = Color.Argb(204, trackTint.R, trackTint.G, trackTint.B);
            _borderPaint.Color = Color.Argb(96, borderTint.R, borderTint.G, borderTint.B);
            _thumbPaint.Color = thumbColor;
            _activeInk = activeInk;
            _inactiveInk = LyricsPalette.BlendColor(Color.Rgb(196, 198, 200), thumbColor, 0.14);

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

    private const int RequestCodePickImage = 2001;

    private static LyricSharePayload? _pendingPayload;

    internal static void Launch(Activity caller, int requestCode, LyricSharePayload payload)
    {
        _pendingPayload = payload;
        var intent = new Intent(caller, typeof(LyricShareActivity));
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

    private Bitmap? _customBgBitmap;
    private Color _customBgThemeColor;
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
    private LinearLayout _toolsRow = null!;
    private LinearLayout _colorTile = null!;
    private View _colorDotView = null!;
    private LinearLayout _imageTile = null!;
    private VectorIconView _imageTileIcon = null!;
    private LinearLayout _actionsRow = null!;
    private LinearLayout _saveBtn = null!;
    private LinearLayout _shareBtn = null!;

    private StyleSwitchPillView _stylePill = null!;
    private FrameLayout? _translationHost;
    private VectorIconView? _translationIcon;
    private PopupWindow? _colorPopup;

    private float _cardDownX;
    private float _cardDownY;
    private bool _cardDragging;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplyEnterTransitionApi34();

        var payload = _pendingPayload;
        _pendingPayload = null;
        if (payload is null || payload.Lines.Count == 0)
        {
            Finish();
            return;
        }

        _payload = payload;
        _presets = LyricShareCardRenderer.BuildPresets(_payload.CoverBackground);
        _style = string.Equals(LyrxApp.Instance.Settings.LyricShareStyle, "apple-music", StringComparison.OrdinalIgnoreCase)
            ? LyricShareStyle.AppleMusic
            : LyricShareStyle.Spotify;
        _hasAnyTranslation = _payload.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation));
        _showTranslation = _hasAnyTranslation && _payload.DefaultShowTranslation;

        BuildPageUi();
        SetContentView(_rootLayout);
        ApplyChromeSystemBars();
        ApplyResponsiveLayout(DetectLandscape(Resources?.Configuration));
        RefreshAllVisuals();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        DismissColorPopup();
        ApplyResponsiveLayout(DetectLandscape(newConfig));
        ApplyChromeSystemBars();
        _rootLayout.RequestApplyInsets();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) ApplyChromeSystemBars();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
#pragma warning disable CA1422
        base.OnActivityResult(requestCode, resultCode, data);
#pragma warning restore CA1422
        if (requestCode != RequestCodePickImage || resultCode != Result.Ok || data?.Data is not { } uri)
            return;

        _ = Task.Run(() =>
        {
            var loaded = DecodeSampledImageFromUri(uri, maxDimension: 1600);
            RunOnUiThread(() =>
            {
                if (_disposed || IsFinishing || IsDestroyed)
                {
                    if (loaded is not null && !loaded.IsRecycled) loaded.Recycle();
                    return;
                }

                if (loaded is null)
                {
                    Toast.MakeText(this, "无法读取所选图片，请换一张重试", ToastLength.Short)?.Show();
                    return;
                }

                var prevCustom = _customBgBitmap;
                _customBgBitmap = loaded;
                (_customBgThemeColor, _) = LyricsPalette.FromCover(loaded);
                RefreshAllVisuals();
                if (prevCustom is not null && !prevCustom.IsRecycled)
                    prevCustom.Recycle();
            });
        });
    }

    public override void Finish()
    {
        DismissColorPopup();
        base.Finish();
        ApplyExitTransition();
    }

    protected override void OnDestroy()
    {
        _disposed = true;
        DismissColorPopup();
        ClearOutgoingPreview();
        _previewImage.SetImageDrawable(null);
        if (_previewBitmap is not null && !_previewBitmap.IsRecycled)
            _previewBitmap.Recycle();
        _previewBitmap = null;
        if (_customBgBitmap is not null && !_customBgBitmap.IsRecycled)
            _customBgBitmap.Recycle();
        _customBgBitmap = null;
        base.OnDestroy();
    }

    private void BuildPageUi()
    {
        _pageBgDrawable = new GradientDrawable(
            GradientDrawable.Orientation.TopBottom,
            [Color.Rgb(16, 16, 16).ToArgb(), Color.Rgb(16, 16, 16).ToArgb(), Color.Rgb(28, 48, 34).ToArgb()]);

        _rootLayout = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Background = _pageBgDrawable,
        };
        _rootLayout.SetOnApplyWindowInsetsListener(new InsetsListener(this));

        // 1. 顶部栏：返回箭头 + 大圆角卡片样式切换 + （可选译文切换 / 对称占位）
        _topBar = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        _topBar.SetGravity(GravityFlags.CenterVertical);

        var backBtn = UiHelpers.IconButton(
            this,
            ClassicIconKind.ArrowLeft,
            Color.White,
            () => { SetResult(Result.Canceled); Finish(); },
            sizeDp: 38,
            iconFraction: 0.62f);
        _topBar.AddView(backBtn, new LinearLayout.LayoutParams(Dp(38), Dp(38)));

        var switcherCenter = new FrameLayout(this);
        _stylePill = new StyleSwitchPillView(this, _style);
        _stylePill.StyleSelected += nextStyle => SwitchStyle(nextStyle, fromPill: true);

        switcherCenter.AddView(_stylePill, new FrameLayout.LayoutParams(ComputeStylePillWidth(), Dp(38))
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
                RefreshAllVisuals();
            };
            _translationHost = trHost;
            _translationIcon = trIcon;
            _topBar.AddView(trHost, new LinearLayout.LayoutParams(Dp(38), Dp(38)));
        }
        else
        {
            _topBar.AddView(new View(this), new LinearLayout.LayoutParams(Dp(38), Dp(38)));
        }

        _rootLayout.AddView(_topBar, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // 2. 主内容区（预览区 + 底部「颜色 / 图像」及「保存图片 / 分享图片」）
        _contentArea = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };

        _previewStage = new FrameLayout(this) { Clickable = true };
        _previewStage.LayoutChange += (_, _) => UpdatePreviewImageMaxBounds();
        _previewStage.Touch += OnPreviewStageTouch;

        _previewImage = new ImageView(this)
        {
            ClipToOutline = true,
            Elevation = Dp(14),
        };
        _previewImage.SetAdjustViewBounds(true);
        _previewImage.SetScaleType(ImageView.ScaleType.FitCenter);
        _previewStage.AddView(_previewImage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Center,
        });

        _controlsArea = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        _controlsArea.SetGravity(GravityFlags.CenterHorizontal);

        // 2a. 颜色 + 图像入口卡片行
        _toolsRow = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        _toolsRow.SetGravity(GravityFlags.Center);

        (_colorTile, _colorDotView) = BuildColorToolTile();
        _colorTile.Click += (_, _) => ToggleColorPickerPopup();

        (_imageTile, _imageTileIcon) = BuildImageToolTile();
        _imageTile.Click += (_, _) => OpenCustomBackgroundPicker();

        _toolsRow.AddView(_colorTile, new LinearLayout.LayoutParams(Dp(86), Dp(74))
        {
            RightMargin = Dp(16),
        });
        _toolsRow.AddView(_imageTile, new LinearLayout.LayoutParams(Dp(86), Dp(74)));

        // 2b. 保存图片 + 分享图片按钮行
        _actionsRow = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        _actionsRow.SetGravity(GravityFlags.CenterVertical);

        _saveBtn = BuildBottomActionPill(
            ClassicIconKind.Download,
            "保存图片",
            Color.Argb(205, 36, 36, 36),
            Color.White,
            Color.Argb(72, 255, 255, 255));
        _saveBtn.Click += (_, _) => ExportAndSaveToGallery();

        _shareBtn = BuildBottomActionPill(
            ClassicIconKind.Share,
            "分享图片",
            Color.White,
            Color.Rgb(18, 18, 18),
            null);
        _shareBtn.Elevation = Dp(4);
        _shareBtn.Click += (_, _) => ExportAndShareSystem();

        _actionsRow.AddView(_saveBtn, new LinearLayout.LayoutParams(0, Dp(46), 1f)
        {
            RightMargin = Dp(12),
        });
        _actionsRow.AddView(_shareBtn, new LinearLayout.LayoutParams(0, Dp(46), 1f));

        _controlsArea.AddView(_toolsRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(18),
        });
        _controlsArea.AddView(_actionsRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _rootLayout.AddView(_contentArea, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));
    }

    private int ComputeStylePillWidth()
    {
        var screenW = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
        var labelW = _stylePill is not null ? _stylePill.MeasureLongestLabelWidthPx() : Dp(73);
        var compactTarget = (int)MathF.Ceiling(labelW * 2f + Dp(44));
        var maxW = Math.Max(Dp(176), Math.Min(Dp(216), screenW - Dp(124)));
        var minW = Math.Min(Dp(182), maxW);
        return Math.Clamp(compactTarget, minW, maxW);
    }

    private void ApplyResponsiveLayout(bool isLandscape)
    {
        _isLandscapeLayout = isLandscape;
        var isTablet = UiHelpers.IsTabletLayout(this);

        if (_stylePill.LayoutParameters is FrameLayout.LayoutParams pillLp)
        {
            var nextPillW = ComputeStylePillWidth();
            if (pillLp.Width != nextPillW)
            {
                pillLp.Width = nextPillW;
                _stylePill.LayoutParameters = pillLp;
            }
        }

        if (_previewStage.Parent is ViewGroup p1) p1.RemoveView(_previewStage);
        if (_controlsArea.Parent is ViewGroup p2) p2.RemoveView(_controlsArea);
        _contentArea.RemoveAllViews();

        if (_toolsRow.LayoutParameters is LinearLayout.LayoutParams toolsLp)
        {
            toolsLp.BottomMargin = Dp(isLandscape ? 18 : 40);
            _toolsRow.LayoutParameters = toolsLp;
        }

        if (isLandscape)
        {
            _contentArea.Orientation = global::Android.Widget.Orientation.Horizontal;
            _contentArea.AddView(_previewStage, new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.MatchParent, 1.25f)
            {
                RightMargin = Dp(isTablet ? 28 : 16),
            });

            var rightColW = Dp(isTablet ? 320 : 256);
            var controlsLp = new LinearLayout.LayoutParams(rightColW, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.CenterVertical,
            };
            _contentArea.AddView(_controlsArea, controlsLp);
        }
        else
        {
            _contentArea.Orientation = global::Android.Widget.Orientation.Vertical;
            _contentArea.AddView(_previewStage, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f)
            {
                BottomMargin = Dp(isTablet ? 20 : 12),
            });

            var maxActionW = isTablet ? Dp(400) : Dp(340);
            var dmW = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
            var controlsW = Math.Min(maxActionW, Math.Max(Dp(240), dmW - Dp(40)));
            _contentArea.AddView(_controlsArea, new LinearLayout.LayoutParams(
                controlsW, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.CenterHorizontal,
            });
        }

        ApplySystemBarInsets(_insetLeft, _insetTop, _insetRight, _insetBottom);
        UpdatePreviewImageMaxBounds();
    }

    private void ApplySystemBarInsets(int left, int top, int right, int bottom)
    {
        _insetLeft = left;
        _insetTop = top;
        _insetRight = right;
        _insetBottom = bottom;

        var isTablet = UiHelpers.IsTabletLayout(this);
        var sidePad = Dp(isTablet ? 24 : 16);
        _topBar.SetPadding(left + sidePad, top + Dp(isTablet ? 12 : 8), right + sidePad, Dp(isTablet ? 10 : 6));
        _contentArea.SetPadding(
            left + sidePad,
            Dp(isTablet ? 10 : 6),
            right + sidePad,
            bottom + Dp(isTablet ? 24 : 18));
    }

    private void UpdatePreviewImageMaxBounds()
    {
        if (_disposed || _previewStage.Width <= 0 || _previewStage.Height <= 0) return;
        var isTablet = UiHelpers.IsTabletLayout(this);
        var maxSideCap = isTablet ? Dp(500) : _previewStage.Width;
        var maxW = Math.Min(_previewStage.Width - Dp(8), maxSideCap);
        var maxH = Math.Min(_previewStage.Height - Dp(8), Dp(isTablet ? 520 : 480));
        _previewImage.SetMaxWidth(Math.Max(Dp(120), maxW));
        _previewImage.SetMaxHeight(Math.Max(Dp(120), maxH));
    }

    private (LinearLayout Tile, View Dot) BuildColorToolTile()
    {
        var tile = MakeToolTileHost();
        var dot = new View(this);
        tile.AddView(dot, new LinearLayout.LayoutParams(Dp(30), Dp(30))
        {
            BottomMargin = Dp(6),
        });
        tile.AddView(MakeToolTileLabel("颜色"));
        return (tile, dot);
    }

    private (LinearLayout Tile, VectorIconView Icon) BuildImageToolTile()
    {
        var tile = MakeToolTileHost();
        var icon = new VectorIconView(this, ClassicIconKind.Image);
        icon.Configure(ClassicIconKind.Image, Color.White, iconFraction: 0.90f);
        tile.AddView(icon, new LinearLayout.LayoutParams(Dp(30), Dp(30))
        {
            BottomMargin = Dp(6),
        });
        tile.AddView(MakeToolTileLabel("图像"));
        return (tile, icon);
    }

    private LinearLayout MakeToolTileHost()
    {
        var tile = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Clickable = true,
            Focusable = true,
            Background = UiHelpers.RoundedRect(this, Color.Argb(168, 22, 24, 24), 16f, Color.Argb(46, 255, 255, 255), 1f),
        };
        tile.SetGravity(GravityFlags.Center);
        tile.SetPadding(Dp(8), Dp(10), Dp(8), Dp(8));
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
        tv.SetTextColor(Color.Argb(235, 255, 255, 255));
        tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
        UiHelpers.SetFontWeight(tv, UiHelpers.FontWeightRegular);
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
                ? UiHelpers.RoundedRect(this, bgColor, 23f, s, 1f)
                : UiHelpers.RoundedRect(this, bgColor, 23f),
            Clickable = true,
            Focusable = true,
        };
        pill.SetGravity(GravityFlags.Center);
        pill.SetPadding(Dp(12), 0, Dp(14), 0);

        var icon = new VectorIconView(this, iconKind);
        icon.Configure(iconKind, fgColor, iconFraction: 0.78f);
        pill.AddView(icon, new LinearLayout.LayoutParams(Dp(17), Dp(17))
        {
            RightMargin = Dp(7),
        });

        var tv = new TextView(this) { Text = labelText };
        tv.SetIncludeFontPadding(false);
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
                _cardDragging = false;
                e.Handled = true;
                break;
        }
    }

    private ShareCardOptions BuildCurrentOptions() => new(
        _style,
        _presets[_colorIndex].BaseColor,
        _showTranslation,
        _payload.TrackTitle,
        _payload.ArtistName,
        _payload.CoverBitmap,
        _payload.Lines,
        _customBgBitmap);

    private void RefreshAllVisuals(int animateDirection = 0)
    {
        var isSpotify = _style == LyricShareStyle.Spotify;

        if (_translationHost is not null && _translationIcon is not null)
        {
            _translationHost.Background = _showTranslation
                ? UiHelpers.RoundedRect(this, LyricsPalette.AccentGreen, 19f)
                : UiHelpers.RoundedRect(this, Color.Argb(185, 28, 28, 28), 19f, Color.Argb(60, 255, 255, 255), 1f);
            _translationIcon.Configure(
                ClassicIconKind.Bilingual,
                _showTranslation ? Color.Rgb(18, 18, 18) : Color.White,
                iconFraction: 0.58f);
        }

        var hasCustom = _customBgBitmap is { IsRecycled: false };
        var themeColor = hasCustom ? _customBgThemeColor : _presets[_colorIndex].BaseColor;
        _stylePill.SetMonetPalette(themeColor, _payload.CoverBackground);
        var bottomHue = LyricsPalette.BlendColor(themeColor, Color.Black, 0.25);
        var topDark = Color.Rgb(14, 14, 14);
        _pageBgDrawable.SetColors([topDark.ToArgb(), topDark.ToArgb(), bottomHue.ToArgb()]);

        _colorDotView.Background = UiHelpers.RoundedRect(
            this,
            _presets[_colorIndex].BaseColor,
            15f,
            hasCustom ? Color.Argb(150, 255, 255, 255) : Color.White,
            hasCustom ? 1.4f : 2f);

        _colorTile.Background = UiHelpers.RoundedRect(
            this,
            Color.Argb(168, 22, 24, 24),
            16f,
            hasCustom ? Color.Argb(46, 255, 255, 255) : Color.Argb(130, 255, 255, 255),
            hasCustom ? 1f : 1.3f);

        _imageTile.Background = UiHelpers.RoundedRect(
            this,
            Color.Argb(168, 22, 24, 24),
            16f,
            hasCustom ? LyricsPalette.AccentGreen : Color.Argb(46, 255, 255, 255),
            hasCustom ? 1.5f : 1f);
        _imageTileIcon.Configure(ClassicIconKind.Image, hasCustom ? LyricsPalette.AccentGreen : Color.White, iconFraction: 0.90f);

        var nextBmp = LyricShareCardRenderer.Render(this, BuildCurrentOptions());
        var oldBmp = _previewBitmap;
        var previewRadiusDp = isSpotify ? 22f : 18f;

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
            if (oldBmp is not null && !oldBmp.IsRecycled)
                oldBmp.Recycle();
        }
    }

    private void ClearOutgoingPreview()
    {
        if (_outgoingPreviewImage is { } oldView)
        {
            oldView.Animate()?.Cancel();
            oldView.SetImageDrawable(null);
            _previewStage?.RemoveView(oldView);
            _outgoingPreviewImage = null;
        }
        if (_outgoingPreviewBitmap is { IsRecycled: false } oldBmp)
            oldBmp.Recycle();
        _outgoingPreviewBitmap = null;
    }

    private void ToggleColorPickerPopup()
    {
        if (_colorPopup is { IsShowing: true })
        {
            DismissColorPopup();
            return;
        }

        var dm = Resources?.DisplayMetrics;
        var screenW = dm?.WidthPixels ?? 1080;
        var screenH = dm?.HeightPixels ?? 2200;
        var margin = Dp(14);
        var popupW = Math.Min(Dp(312), screenW - margin * 2);

        var card = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Background = UiHelpers.RoundedRect(this, Color.Argb(245, 26, 26, 28), 20f, Color.Argb(75, 255, 255, 255), 1f),
            Elevation = Dp(14),
        };
        card.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));

        var title = new TextView(this) { Text = "选择背景颜色" };
        title.SetIncludeFontPadding(false);
        title.SetTextColor(Color.Argb(210, 255, 255, 255));
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12.5f);
        UiHelpers.SetFontWeight(title, UiHelpers.FontWeightSemiBold);
        card.AddView(title, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(10),
        });

        var dotViews = new List<View>(_presets.Count);
        void SyncPopupDots()
        {
            var hasCustom = _customBgBitmap is { IsRecycled: false };
            for (var i = 0; i < dotViews.Count; i++)
            {
                var selected = !hasCustom && i == _colorIndex;
                dotViews[i].Background = UiHelpers.RoundedRect(
                    this,
                    _presets[i].BaseColor,
                    18f,
                    selected ? Color.White : Color.Argb(90, 255, 255, 255),
                    selected ? 2.4f : 1f);
            }
        }

        // 每行 4 个圆形色块 + 色板名（共 2 行 8 色）
        const int cols = 4;
        var rows = (_presets.Count + cols - 1) / cols;
        for (var r = 0; r < rows; r++)
        {
            var rowLayout = new LinearLayout(this)
            {
                Orientation = global::Android.Widget.Orientation.Horizontal,
            };
            for (var c = 0; c < cols; c++)
            {
                var idx = r * cols + c;
                if (idx >= _presets.Count)
                {
                    rowLayout.AddView(new View(this), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                    continue;
                }

                var cell = new LinearLayout(this)
                {
                    Orientation = global::Android.Widget.Orientation.Vertical,
                    Clickable = true,
                    Focusable = true,
                };
                cell.SetGravity(GravityFlags.CenterHorizontal);
                cell.SetPadding(Dp(2), Dp(4), Dp(2), Dp(4));

                var dot = new View(this);
                cell.AddView(dot, new LinearLayout.LayoutParams(Dp(36), Dp(36))
                {
                    BottomMargin = Dp(5),
                });
                dotViews.Add(dot);

                var label = new TextView(this)
                {
                    Text = _presets[idx].Label,
                    Gravity = GravityFlags.Center,
                };
                label.SetIncludeFontPadding(false);
                label.SetMaxLines(1);
                label.SetTextColor(Color.Argb(200, 255, 255, 255));
                label.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10.5f);
                UiHelpers.SetFontWeight(label, UiHelpers.FontWeightRegular);
                cell.AddView(label);

                var capturedIdx = idx;
                cell.Click += (_, _) =>
                {
                    var oldCustom = _customBgBitmap;
                    _customBgBitmap = null;
                    _colorIndex = capturedIdx;
                    RefreshAllVisuals();
                    SyncPopupDots();
                    if (oldCustom is not null && !oldCustom.IsRecycled)
                        oldCustom.Recycle();
                };

                rowLayout.AddView(cell, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            }

            var rowLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            if (r < rows - 1) rowLp.BottomMargin = Dp(6);
            card.AddView(rowLayout, rowLp);
        }

        SyncPopupDots();

        var popup = new PopupWindow(card, popupW, ViewGroup.LayoutParams.WrapContent, focusable: true)
        {
            OutsideTouchable = true,
            Elevation = Dp(14),
            AnimationStyle = Resource.Style.AudioOutputPopupAnim,
        };
        popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        popup.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_colorPopup, popup))
                _colorPopup = null;
        };
        _colorPopup = popup;

        card.Measure(
            View.MeasureSpec.MakeMeasureSpec(popupW, MeasureSpecMode.Exactly),
            View.MeasureSpec.MakeMeasureSpec(screenH, MeasureSpecMode.AtMost));
        var popupH = card.MeasuredHeight;

        var anchorLoc = new int[2];
        _colorTile.GetLocationInWindow(anchorLoc);
        var x = Math.Clamp(anchorLoc[0] + (_colorTile.Width - popupW) / 2, margin, screenW - popupW - margin);
        var y = Math.Clamp(anchorLoc[1] - popupH - Dp(10), _insetTop + margin, screenH - popupH - margin);
        popup.ShowAtLocation(_rootLayout, GravityFlags.NoGravity, x, y);
    }

    private void DismissColorPopup()
    {
        if (_colorPopup is { IsShowing: true } p)
            p.Dismiss();
        _colorPopup = null;
    }

    private void OpenCustomBackgroundPicker()
    {
        DismissColorPopup();
        try
        {
            Intent intent;
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                intent = new Intent(MediaStore.ActionPickImages);
                intent.SetType("image/*");
            }
            else
            {
                intent = new Intent(Intent.ActionGetContent);
                intent.SetType("image/*");
                intent.AddCategory(Intent.CategoryOpenable);
            }
#pragma warning disable CA1422
            StartActivityForResult(Intent.CreateChooser(intent, "选择自定义背景图"), RequestCodePickImage);
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
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
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

            var opts = new BitmapFactory.Options { InSampleSize = sample };
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

            var matrix = new Matrix();
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
                default:
                    return decoded;
            }

            var rotated = Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true);
            if (rotated is not null && !ReferenceEquals(rotated, decoded))
            {
                decoded.Recycle();
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
        SetExportBusy(true);
        var opt = BuildCurrentOptions();
        var baseName = string.IsNullOrWhiteSpace(_payload.TrackTitle) ? "Lyrx_Lyrics" : _payload.TrackTitle!;

        _ = Task.Run(() =>
        {
            using var exportBmp = LyricShareCardRenderer.Render(this, opt);
            string? savedPath = null;
            string? errorMsg = null;
            try
            {
                savedPath = ShareImageHelper.SaveToGallery(this, exportBmp, baseName);
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }

            RunOnUiThread(() =>
            {
                SetExportBusy(false);
                if (_disposed || IsFinishing || IsDestroyed) return;
                if (savedPath is not null)
                {
                    Toast.MakeText(this, $"已保存到相册 {savedPath}", ToastLength.Short)?.Show();
                    SetResult(Result.Ok);
                    Finish();
                }
                else
                {
                    Toast.MakeText(this, $"保存失败：{errorMsg}", ToastLength.Short)?.Show();
                }
            });
        });
    }

    private void ExportAndShareSystem()
    {
        if (_isExporting) return;
        SetExportBusy(true);
        var opt = BuildCurrentOptions();
        var baseName = string.IsNullOrWhiteSpace(_payload.TrackTitle) ? "Lyrx_Lyrics" : _payload.TrackTitle!;

        _ = Task.Run(() =>
        {
            using var exportBmp = LyricShareCardRenderer.Render(this, opt);
            global::Android.Net.Uri? shareUri = null;
            string? errorMsg = null;
            try
            {
                shareUri = ShareImageHelper.SaveTempSharePng(this, exportBmp, baseName);
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }

            RunOnUiThread(() =>
            {
                SetExportBusy(false);
                if (_disposed || IsFinishing || IsDestroyed) return;
                if (shareUri is null)
                {
                    Toast.MakeText(this, $"生成分享图片失败：{errorMsg}", ToastLength.Short)?.Show();
                    return;
                }

                try
                {
                    var send = new Intent(Intent.ActionSend);
                    send.SetType("image/png");
                    send.PutExtra(Intent.ExtraStream, shareUri);
                    send.ClipData = ClipData.NewRawUri("Lyrx", shareUri);
                    send.AddFlags(ActivityFlags.GrantReadUriPermission);
                    StartActivity(Intent.CreateChooser(send, "分享歌词卡片"));
                    SetResult(Result.Ok);
                    Finish();
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, $"无法打开系统分享：{ex.Message}", ToastLength.Short)?.Show();
                }
            });
        });
    }

    private void SetExportBusy(bool busy)
    {
        _isExporting = busy;
        _saveBtn.Alpha = busy ? 0.6f : 1f;
        _shareBtn.Alpha = busy ? 0.65f : 1f;
    }

    private void ApplyChromeSystemBars()
    {
        try
        {
            UiHelpers.HideStatusBar(Window, edgeToEdge: true, navigationBarColor: Color.Rgb(16, 16, 16));
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
