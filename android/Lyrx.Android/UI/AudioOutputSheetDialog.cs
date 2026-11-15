using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Android.Animation;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using Musebase.Android.Services;
using Musebase.Engine;
using Path = global::Android.Graphics.Path;

namespace Musebase.Android.UI;

/// <summary>
/// 参照系统播放控制浮层设计的轻量级音频输出设备与媒体音量浮窗：
/// 点击底部蓝牙耳机名称时直接浮在播放器上方，内置当前输出设备的实时滑动音量条，
/// 不弹新窗口、不遮黑全屏。
/// </summary>
internal static class AudioOutputSheetDialog
{
    private const string ActionVolumeChanged = "android.media.VOLUME_CHANGED_ACTION";

    private static PopupWindow? _activePopup;
    private static long _lastDismissAtMs;

    public static void DismissIfShowing()
    {
        if (_activePopup is { IsShowing: true } popup)
        {
            try { popup.Dismiss(); } catch { }
        }
        _activePopup = null;
    }

    public static void Show(
        Activity activity,
        View anchor,
        Color coverBackground,
        View? anchorContainer = null,
        Action? onOutputChanged = null)
    {
        if (activity.IsFinishing || activity.IsDestroyed) return;

        if (_activePopup is { IsShowing: true })
        {
            _activePopup.Dismiss();
            _activePopup = null;
            return;
        }

        // 防止点击外部关闭弹窗后在同一次点击事件周期内立刻重开
        if (SystemClock.UptimeMillis() - _lastDismissAtMs < 200)
            return;

        var app = LyrxApp.Instance;
        var dm = activity.Resources?.DisplayMetrics;
        var screenW = dm?.WidthPixels ?? 1080;
        var screenH = dm?.HeightPixels ?? 2200;
        var margin = UiHelpers.Dp(activity, 12);

        var bounds = ResolveHorizontalBounds(activity, anchorContainer, screenW, margin);
        var maxAvailW = Math.Max(UiHelpers.Dp(activity, 180), bounds.Right - bounds.Left);
        var popupWidth = bounds.ConstrainedToContainer
            ? Math.Min(UiHelpers.Dp(activity, 286), maxAvailW)
            : Math.Min(UiHelpers.Dp(activity, 292), (int)(screenW * 0.86));

        var tintedGlass = LyricsPalette.BlendColor(LyricsPalette.ChromeBackground, coverBackground, 0.24);
        var cardBgColor = Color.Argb(255, tintedGlass.R, tintedGlass.G, tintedGlass.B);
        var cardBorderColor = Color.Argb(54, 255, 255, 255);

        var card = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Background = UiHelpers.RoundedRect(activity, cardBgColor, 22, cardBorderColor, 1f),
            Elevation = UiHelpers.Dp(activity, 14),
        };
        card.SetPadding(
            UiHelpers.Dp(activity, 14),
            UiHelpers.Dp(activity, 14),
            UiHelpers.Dp(activity, 14),
            UiHelpers.Dp(activity, 15));

        // 1. 顶部当前播放曲目摘要（封面缩略图 + 歌名/歌手 + 声波图标）
        var headerRow = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        headerRow.SetGravity(GravityFlags.CenterVertical);

        var coverSize = UiHelpers.Dp(activity, 42);
        var coverFrame = new FrameLayout(activity)
        {
            Background = UiHelpers.RoundedRect(activity, Color.Argb(180, 48, 50, 56), 9),
            ClipToOutline = true,
            OutlineProvider = new RoundedOutlineProvider(UiHelpers.Dp(activity, 9)),
        };

        var noteFallback = new TextView(activity)
        {
            Text = "♫",
            Gravity = GravityFlags.Center,
        };
        noteFallback.SetTextColor(Color.Argb(180, 255, 255, 255));
        noteFallback.SetTextSize(ComplexUnitType.Sp, 16);
        coverFrame.AddView(noteFallback, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var coverImage = new ImageView(activity);
        coverImage.SetScaleType(ImageView.ScaleType.CenterCrop);
        coverFrame.AddView(coverImage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        headerRow.AddView(coverFrame, new LinearLayout.LayoutParams(coverSize, coverSize)
        {
            RightMargin = UiHelpers.Dp(activity, 10),
        });

        var titleCol = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };

        var titleView = new TextView(activity)
        {
            Ellipsize = TextUtils.TruncateAt.End,
        };
        titleView.SetMaxLines(1);
        titleView.SetIncludeFontPadding(false);
        titleView.SetTextColor(Color.White);
        titleView.SetTextSize(ComplexUnitType.Sp, 14f);
        UiHelpers.SetFontWeight(titleView, UiHelpers.FontWeightSemiBold);

        var artistView = new TextView(activity)
        {
            Ellipsize = TextUtils.TruncateAt.End,
        };
        artistView.SetMaxLines(1);
        artistView.SetIncludeFontPadding(false);
        artistView.SetTextColor(Color.Argb(185, 255, 255, 255));
        artistView.SetTextSize(ComplexUnitType.Sp, 12f);
        UiHelpers.SetFontWeight(artistView, UiHelpers.FontWeightRegular);

        titleCol.AddView(titleView);
        titleCol.AddView(artistView, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = UiHelpers.Dp(activity, 2),
        });
        headerRow.AddView(titleCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        {
            RightMargin = UiHelpers.Dp(activity, 8),
        });

        var waveView = new AudioWaveView(activity);
        waveView.SetPlaying(app.NowPlaying.IsPlaying);
        headerRow.AddView(waveView, new LinearLayout.LayoutParams(UiHelpers.Dp(activity, 18), UiHelpers.Dp(activity, 18)));

        card.AddView(headerRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = UiHelpers.Dp(activity, 12),
        });

        // 2. 播放设备列表容器（未激活设备胶囊 + 底部激活设备交互音量条）
        var devicesHost = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        card.AddView(devicesHost, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        void RefreshHeader()
        {
            var track = app.NowPlaying.CurrentTrack;
            titleView.Text = string.IsNullOrWhiteSpace(track?.Title) ? "等待播放" : track!.Title;
            artistView.Text = string.IsNullOrWhiteSpace(track?.Artist) ? "音频输出" : track!.Artist;
            artistView.Visibility = ViewStates.Visible;

            var bmp = app.NowPlaying.GetCurrentArtworkBitmap();
            if (bmp is not null)
            {
                coverImage.SetImageBitmap(bmp);
                coverImage.Visibility = ViewStates.Visible;
            }
            else
            {
                coverImage.SetImageBitmap(null);
                coverImage.Visibility = ViewStates.Gone;
            }
        }

        VolumeOutputPillView? activePill = null;

        void RefreshDevices()
        {
            devicesHost.RemoveAllViews();
            activePill = null;

            var outputs = app.BluetoothAudio.GetAvailableOutputs();
            var (curVol, minVol, maxVol) = app.BluetoothAudio.GetMediaVolumeState();

            for (var i = 0; i < outputs.Count; i++)
            {
                var entry = outputs[i];
                var bottomGap = i < outputs.Count - 1 ? UiHelpers.Dp(activity, 8) : 0;

                if (entry.IsActive)
                {
                    var pill = new VolumeOutputPillView(activity, entry.Name, entry.Kind, curVol, minVol, maxVol, level =>
                    {
                        app.BluetoothAudio.SetMediaVolume(level);
                    });
                    activePill = pill;
                    devicesHost.AddView(pill, new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(activity, 54))
                    {
                        BottomMargin = bottomGap,
                    });
                }
                else
                {
                    var inactiveRow = CreateInactiveDevicePill(activity, entry, () =>
                    {
                        if (BluetoothAudioMonitor.TryOpenSystemOutputSwitcher(activity))
                            DismissIfShowing();
                        else
                            Toast.MakeText(activity, "请在系统控制中心/蓝牙设置中切换输出设备", ToastLength.Short)?.Show();
                        onOutputChanged?.Invoke();
                    });
                    devicesHost.AddView(inactiveRow, new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MatchParent, UiHelpers.Dp(activity, 46))
                    {
                        BottomMargin = bottomGap,
                    });
                }
            }
        }

        void SyncVolumeFromSystem()
        {
            if (activePill is null) return;
            var (curVol, minVol, maxVol) = app.BluetoothAudio.GetMediaVolumeState();
            activePill.SyncExternalVolume(curVol, minVol, maxVol);
        }

        RefreshHeader();
        RefreshDevices();

        var popup = new PopupWindow(card, popupWidth, ViewGroup.LayoutParams.WrapContent, focusable: true)
        {
            OutsideTouchable = true,
            Elevation = UiHelpers.Dp(activity, 14),
            AttachedInDecor = true,
            AnimationStyle = Resource.Style.AudioOutputPopupAnim,
        };
        popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));

        void PostUpdateOnUi(Action action)
        {
            if (activity.IsFinishing || activity.IsDestroyed) return;
            activity.RunOnUiThread(() =>
            {
                if (activity.IsFinishing || activity.IsDestroyed) return;
                if (!ReferenceEquals(_activePopup, popup) || !popup.IsShowing) return;
                action();
            });
        }

        void OnTrackChanged(TrackInfo? _) => PostUpdateOnUi(RefreshHeader);
        void OnArtworkChanged() => PostUpdateOnUi(RefreshHeader);
        void OnIsPlayingChanged(bool playing) => PostUpdateOnUi(() => waveView.SetPlaying(playing));
        void OnDeviceChanged(string? _) => PostUpdateOnUi(() =>
        {
            RefreshDevices();
            RepositionPopup(activity, anchor, card, popup, popupWidth, bounds, screenH);
        });

        app.NowPlaying.TrackChanged += OnTrackChanged;
        app.NowPlaying.ArtworkChanged += OnArtworkChanged;
        app.NowPlaying.IsPlayingChanged += OnIsPlayingChanged;
        app.BluetoothAudio.ConnectedDeviceChanged += OnDeviceChanged;

        var volumeReceiver = new VolumeChangedReceiver(() => PostUpdateOnUi(SyncVolumeFromSystem));
        var receiverRegistered = TryRegisterVolumeReceiver(activity, volumeReceiver);

        popup.DismissEvent += (_, _) =>
        {
            _lastDismissAtMs = SystemClock.UptimeMillis();
            if (ReferenceEquals(_activePopup, popup)) _activePopup = null;

            waveView.StopAnimation();
            app.NowPlaying.TrackChanged -= OnTrackChanged;
            app.NowPlaying.ArtworkChanged -= OnArtworkChanged;
            app.NowPlaying.IsPlayingChanged -= OnIsPlayingChanged;
            app.BluetoothAudio.ConnectedDeviceChanged -= OnDeviceChanged;

            if (receiverRegistered)
            {
                receiverRegistered = false;
                try { activity.UnregisterReceiver(volumeReceiver); } catch { }
            }
            try { volumeReceiver.Dispose(); } catch { }
        };

        _activePopup = popup;
        ShowAnchoredPopup(activity, anchor, card, popup, popupWidth, bounds, screenH);
    }

    private readonly record struct HorizontalBounds(int Left, int Right, bool ConstrainedToContainer);

    private static HorizontalBounds ResolveHorizontalBounds(
        Activity activity,
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
            var leftPad = Math.Max(UiHelpers.Dp(activity, 8), anchorContainer.PaddingLeft - UiHelpers.Dp(activity, 4));
            var innerLeft = containerLeft + leftPad;
            var innerRight = containerRight - UiHelpers.Dp(activity, 10);
            if (innerRight - innerLeft >= UiHelpers.Dp(activity, 180))
                return new HorizontalBounds(innerLeft, innerRight, ConstrainedToContainer: true);
        }

        return new HorizontalBounds(margin, Math.Max(margin + 1, screenW - margin), ConstrainedToContainer: false);
    }

    private static void ShowAnchoredPopup(
        Activity activity,
        View anchor,
        View card,
        PopupWindow popup,
        int popupWidth,
        HorizontalBounds bounds,
        int screenH)
    {
        var (x, y) = ComputeAnchorPosition(activity, anchor, card, popupWidth, bounds, screenH);
        popup.ShowAtLocation(anchor, GravityFlags.Top | GravityFlags.Start, x, y);
    }

    private static void RepositionPopup(
        Activity activity,
        View anchor,
        View card,
        PopupWindow popup,
        int popupWidth,
        HorizontalBounds bounds,
        int screenH)
    {
        var (x, y) = ComputeAnchorPosition(activity, anchor, card, popupWidth, bounds, screenH);
        popup.Update(x, y, popupWidth, ViewGroup.LayoutParams.WrapContent);
    }

    private static (int X, int Y) ComputeAnchorPosition(
        Activity activity,
        View anchor,
        View card,
        int popupWidth,
        HorizontalBounds bounds,
        int screenH)
    {
        card.Measure(
            View.MeasureSpec.MakeMeasureSpec(popupWidth, MeasureSpecMode.Exactly),
            View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
        var popupHeight = card.MeasuredHeight;

        var loc = new int[2];
        anchor.GetLocationInWindow(loc);
        var margin = UiHelpers.Dp(activity, 12);
        var maxX = Math.Max(bounds.Left, bounds.Right - popupWidth);

        if (loc[1] > 0 || anchor.Height > 0)
        {
            var x = Math.Clamp(loc[0], bounds.Left, maxX);
            // 竖屏模式下底部贴齐锚点容器上缘，完整遮住上方的圆形播放键；横屏留 8dp 间距悬浮于功能行之上。
            var gapAboveAnchor = bounds.ConstrainedToContainer
                ? UiHelpers.Dp(activity, 8)
                : 0;
            var y = Math.Clamp(
                loc[1] - popupHeight - gapAboveAnchor,
                margin,
                Math.Max(margin, screenH - popupHeight - margin));
            return (x, y);
        }

        return (bounds.Left, Math.Max(margin, screenH - popupHeight - UiHelpers.Dp(activity, 64)));
    }

    private static LinearLayout CreateInactiveDevicePill(Activity activity, AudioOutputEntry entry, Action onClick)
    {
        var row = new LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            Background = UiHelpers.RoundedRect(
                activity,
                Color.Argb(46, 255, 255, 255),
                23,
                Color.Argb(24, 255, 255, 255),
                1f),
            Clickable = true,
            Focusable = true,
        };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(UiHelpers.Dp(activity, 14), 0, UiHelpers.Dp(activity, 14), 0);

        var iconKind = ResolveIconKind(entry.Kind);
        var iconView = new VectorIconView(activity, iconKind);
        iconView.Configure(iconKind, Color.Argb(236, 255, 255, 255), iconFraction: 0.88f);
        row.AddView(iconView, new LinearLayout.LayoutParams(UiHelpers.Dp(activity, 19), UiHelpers.Dp(activity, 19))
        {
            RightMargin = UiHelpers.Dp(activity, 10),
        });

        var label = new TextView(activity)
        {
            Text = entry.Name,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        label.SetMaxLines(1);
        label.SetIncludeFontPadding(false);
        label.SetTextColor(Color.Argb(242, 255, 255, 255));
        label.SetTextSize(ComplexUnitType.Sp, 14f);
        UiHelpers.SetFontWeight(label, UiHelpers.FontWeightSemiBold);
        row.AddView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        {
            RightMargin = UiHelpers.Dp(activity, 6),
        });

        var chevron = new VectorIconView(activity, ClassicIconKind.ChevronDown) { Rotation = -90f };
        chevron.Configure(ClassicIconKind.ChevronDown, Color.Argb(115, 255, 255, 255), iconFraction: 0.72f);
        row.AddView(chevron, new LinearLayout.LayoutParams(UiHelpers.Dp(activity, 14), UiHelpers.Dp(activity, 14)));

        row.Click += (_, _) => onClick();
        return row;
    }

    private static ClassicIconKind ResolveIconKind(AudioOutputKind kind) => kind switch
    {
        AudioOutputKind.Bluetooth => ClassicIconKind.BluetoothAudio,
        AudioOutputKind.WiredOrUsb => ClassicIconKind.Headphones,
        _ => ClassicIconKind.PhoneSpeaker,
    };

    private static bool TryRegisterVolumeReceiver(Context context, BroadcastReceiver receiver)
    {
        try
        {
            var filter = new IntentFilter(ActionVolumeChanged);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                RegisterVolumeReceiverApi33(context, receiver, filter);
            else
                context.RegisterReceiver(receiver, filter);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private static void RegisterVolumeReceiverApi33(Context context, BroadcastReceiver receiver, IntentFilter filter) =>
        context.RegisterReceiver(receiver, filter, ReceiverFlags.Exported);

    /// <summary>
    /// 集成了横向音量条的当前音频输出胶囊视图：左滑右滑实时调节媒体音量，右侧显示勾选标识。
    /// </summary>
    private sealed class VolumeOutputPillView : View
    {
        private static readonly Color UnfilledPillColor = Color.Argb(132, 255, 255, 255);
        private static readonly Color FilledPillColor = Color.Argb(240, 255, 255, 255);
        private static readonly Color DarkInkColor = Color.Rgb(20, 21, 24);
        private static readonly Color SubtleInkColor = Color.Rgb(52, 54, 60);
        private static readonly Color CheckBadgeBgColor = Color.Rgb(24, 25, 28);

        private readonly string _deviceName = "";
        private readonly ClassicIconKind _iconKind;
        private readonly Action<int>? _onVolumeStepChanged;
        private readonly Path _clipPath = new();
        private readonly RectF _pillRect = new();
        private readonly RectF _fillRect = new();
        private readonly Paint _bgPaint = new(PaintFlags.AntiAlias) { Color = UnfilledPillColor };
        private readonly Paint _fillPaint = new(PaintFlags.AntiAlias) { Color = FilledPillColor };
        private readonly Paint _badgeBgPaint = new(PaintFlags.AntiAlias) { Color = CheckBadgeBgColor };
        private readonly Paint _checkPaint = new(PaintFlags.AntiAlias)
        {
            Color = Color.White,
            StrokeCap = Paint.Cap.Round,
            StrokeJoin = Paint.Join.Round,
        };
        private readonly TextPaint _titlePaint = new(PaintFlags.AntiAlias) { Color = DarkInkColor };
        private readonly TextPaint _subPaint = new(PaintFlags.AntiAlias) { Color = SubtleInkColor };
        private readonly float _titleAscent;
        private readonly float _titleHeight;
        private readonly float _subAscent;
        private readonly float _subHeight;

        private int _minVol;
        private int _maxVol = 15;
        private int _lastAppliedStep = -1;
        private float _visualFraction;
        private bool _isDragging;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(VolumeOutputPillView))]
        public VolumeOutputPillView(IntPtr javaReference, JniHandleOwnership transfer)
            : base(javaReference, transfer)
        {
        }

        public VolumeOutputPillView(
            Context context,
            string deviceName,
            AudioOutputKind kind,
            int currentVol,
            int minVol,
            int maxVol,
            Action<int> onVolumeStepChanged)
            : base(context)
        {
            _deviceName = deviceName;
            _iconKind = kind == AudioOutputKind.PhoneSpeaker ? ClassicIconKind.Speaker : ResolveIconKind(kind);
            _onVolumeStepChanged = onVolumeStepChanged;

            _bgPaint.SetStyle(Paint.Style.Fill);
            _fillPaint.SetStyle(Paint.Style.Fill);
            _badgeBgPaint.SetStyle(Paint.Style.Fill);
            _checkPaint.SetStyle(Paint.Style.Stroke);
            _checkPaint.StrokeWidth = Dp(2.0f);

            var density = context.Resources?.DisplayMetrics?.Density ?? 2f;
            _titlePaint.TextSize = 14f * density;
            _titlePaint.SetTypeface(UiHelpers.ResolveFontWeight(context, UiHelpers.FontWeightSemiBold));
            _subPaint.TextSize = 11.5f * density;
            _subPaint.SetTypeface(UiHelpers.ResolveFontWeight(context, UiHelpers.FontWeightRegular));

            var titleFm = _titlePaint.GetFontMetrics();
            _titleAscent = titleFm?.Ascent ?? -_titlePaint.TextSize * 0.8f;
            _titleHeight = titleFm is not null ? titleFm.Descent - titleFm.Ascent : _titlePaint.TextSize;

            var subFm = _subPaint.GetFontMetrics();
            _subAscent = subFm?.Ascent ?? -_subPaint.TextSize * 0.8f;
            _subHeight = subFm is not null ? subFm.Descent - subFm.Ascent : _subPaint.TextSize;

            SyncExternalVolume(currentVol, minVol, maxVol);
        }

        public void SyncExternalVolume(int currentVol, int minVol, int maxVol)
        {
            if (_isDragging) return;
            _minVol = Math.Max(0, minVol);
            _maxVol = Math.Max(_minVol + 1, maxVol);
            var clamped = Math.Clamp(currentVol, _minVol, _maxVol);
            _lastAppliedStep = clamped;
            _visualFraction = (float)(clamped - _minVol) / (_maxVol - _minVol);
            Invalidate();
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e is null || Width <= 0) return base.OnTouchEvent(e);

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _isDragging = true;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateFromTouchX(e.GetX());
                    return true;

                case MotionEventActions.Move:
                    if (_isDragging)
                        UpdateFromTouchX(e.GetX());
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (_isDragging)
                    {
                        if (e.ActionMasked == MotionEventActions.Up)
                            UpdateFromTouchX(e.GetX());
                        _isDragging = false;
                        var span = Math.Max(1, _maxVol - _minVol);
                        _visualFraction = Math.Clamp((float)(_lastAppliedStep - _minVol) / span, 0f, 1f);
                        Invalidate();
                        PerformClick();
                    }
                    return true;
            }
            return base.OnTouchEvent(e);
        }

        public override bool PerformClick() => base.PerformClick();

        private void UpdateFromTouchX(float x)
        {
            var pad = Dp(8f);
            var usable = Math.Max(1f, Width - pad * 2f);
            var fraction = Math.Clamp((x - pad) / usable, 0f, 1f);
            _visualFraction = fraction;

            var targetStep = Math.Clamp(
                _minVol + (int)Math.Round(fraction * (_maxVol - _minVol)),
                _minVol,
                _maxVol);

            if (targetStep != _lastAppliedStep)
            {
                _lastAppliedStep = targetStep;
                _onVolumeStepChanged?.Invoke(targetStep);
            }
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            if (Width <= 0 || Height <= 0) return;

            var w = (float)Width;
            var h = (float)Height;
            var radius = h / 2f;

            _pillRect.Set(0f, 0f, w, h);
            _clipPath.Reset();
            _clipPath.AddRoundRect(_pillRect, radius, radius, Path.Direction.Cw!);

            canvas.Save();
            canvas.ClipPath(_clipPath);

            // 未填充区域与左侧白色实时音量填充
            canvas.DrawRect(_pillRect, _bgPaint);
            var fillW = Math.Clamp(w * _visualFraction, 0f, w);
            if (fillW > 0f)
            {
                _fillRect.Set(0f, 0f, fillW, h);
                canvas.DrawRect(_fillRect, _fillPaint);
            }

            // 左侧音频设备图标
            var iconLeft = Dp(14f);
            var iconSize = Dp(21f);
            var iconTop = (h - iconSize) / 2f;
            canvas.Save();
            canvas.Translate(iconLeft, iconTop);
            ClassicIconRenderer.Draw(canvas, iconSize, iconSize, _iconKind, DarkInkColor, active: false, iconFraction: 0.90f);
            canvas.Restore();

            // 右侧深色勾选徽章
            var badgeRadius = Dp(10f);
            var badgeCx = w - Dp(23f);
            var badgeCy = h / 2f;
            canvas.DrawCircle(badgeCx, badgeCy, badgeRadius, _badgeBgPaint);
            var cs = Dp(4.2f);
            canvas.DrawLine(badgeCx - cs * 0.9f, badgeCy + cs * 0.05f, badgeCx - cs * 0.15f, badgeCy + cs * 0.85f, _checkPaint);
            canvas.DrawLine(badgeCx - cs * 0.15f, badgeCy + cs * 0.85f, badgeCx + cs * 1.0f, badgeCy - cs * 0.70f, _checkPaint);

            // 中间两行文字：设备名 + 实时媒体音量百分比
            var textLeft = iconLeft + iconSize + Dp(10f);
            var textRight = badgeCx - badgeRadius - Dp(10f);
            var textAvailW = Math.Max(0f, textRight - textLeft);

            if (textAvailW > Dp(16f))
            {
                var pct = (int)Math.Round(Math.Clamp(_visualFraction, 0f, 1f) * 100f);
                var ellipsizedTitle = TextUtils.Ellipsize(_deviceName, _titlePaint, textAvailW, TextUtils.TruncateAt.End)?.ToString()
                    ?? _deviceName;
                var subtitleText = $"媒体音量 {pct}%";

                var rowGap = Dp(1.5f);
                var totalH = _titleHeight + rowGap + _subHeight;
                var startY = (h - totalH) / 2f;

                var titleBaseline = startY - _titleAscent;
                var subBaseline = startY + _titleHeight + rowGap - _subAscent;

                canvas.DrawText(ellipsizedTitle, textLeft, titleBaseline, _titlePaint);
                canvas.DrawText(subtitleText, textLeft, subBaseline, _subPaint);
            }

            canvas.Restore();
        }

        private float Dp(float dp) => dp * (Resources?.DisplayMetrics?.Density ?? 2f);
    }

    private sealed class VolumeChangedReceiver : BroadcastReceiver
    {
        private readonly WeakReference<Action>? _callback;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(VolumeChangedReceiver))]
        public VolumeChangedReceiver()
        {
        }

        public VolumeChangedReceiver(IntPtr javaReference, JniHandleOwnership transfer)
            : base(javaReference, transfer)
        {
        }

        public VolumeChangedReceiver(Action callback) =>
            _callback = new WeakReference<Action>(callback);

        public override void OnReceive(Context? context, Intent? intent)
        {
            try
            {
                if (_callback is not null && _callback.TryGetTarget(out var cb))
                    cb();
            }
            catch
            {
                // 忽略系统广播回调异常
            }
        }
    }

    /// <summary>
    /// 播放中动态律动的多柱音频波浪指示器，暂停时自动静止以节省电量。
    /// </summary>
    private sealed class AudioWaveView : View
    {
        private const int BarCount = 5;
        private static readonly float[] BaseFractions = [0.34f, 0.62f, 0.90f, 0.56f, 0.36f];
        private static readonly int[] Cycles = [1, 2, 1, 2, 1];
        private static readonly float[] PhaseOffsets = [0.0f, 1.35f, 2.65f, 4.10f, 5.20f];

        private readonly Paint _paint = new(PaintFlags.AntiAlias)
        {
            StrokeCap = Paint.Cap.Round,
        };

        private ValueAnimator? _animator;
        private float _phase;
        private bool _isPlaying;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AudioWaveView))]
        public AudioWaveView(IntPtr javaReference, JniHandleOwnership transfer)
            : base(javaReference, transfer)
        {
        }

        public AudioWaveView(Context context) : base(context)
        {
            _paint.SetStyle(Paint.Style.Stroke);
            _paint.StrokeWidth = Math.Max(1.8f, Dp(1.75f));
        }

        public void SetPlaying(bool playing)
        {
            if (_isPlaying == playing && (_animator?.IsRunning ?? false) == playing)
                return;

            _isPlaying = playing;
            if (playing)
                StartAnimation();
            else
                StopAnimation();
            Invalidate();
        }

        private void StartAnimation()
        {
            if (_animator is { IsRunning: true }) return;
            _animator?.Cancel();
            _animator?.Dispose();

            var anim = ValueAnimator.OfFloat(0f, 1f)!;
            anim.SetDuration(1350);
            anim.RepeatCount = ValueAnimator.Infinite;
            anim.RepeatMode = ValueAnimatorRepeatMode.Restart;
            anim.SetInterpolator(new LinearInterpolator());
            anim.Update += (_, e) =>
            {
                _phase = e.Animation?.AnimatedFraction ?? 0f;
                Invalidate();
            };
            _animator = anim;
            anim.Start();
        }

        public void StopAnimation()
        {
            try
            {
                _animator?.Cancel();
                _animator?.Dispose();
            }
            catch { }
            _animator = null;
        }

        protected override void OnDetachedFromWindow()
        {
            StopAnimation();
            base.OnDetachedFromWindow();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            if (Width <= 0 || Height <= 0) return;

            var w = (float)Width;
            var h = (float)Height;
            _paint.Color = _isPlaying
                ? Color.Argb(228, 255, 255, 255)
                : Color.Argb(115, 255, 255, 255);

            var padX = _paint.StrokeWidth * 0.7f;
            var spanX = Math.Max(1f, w - padX * 2f);
            var stepX = BarCount > 1 ? spanX / (BarCount - 1) : 0f;
            var maxBarH = Math.Max(4f, h - _paint.StrokeWidth * 1.4f);
            var cy = h / 2f;

            for (var i = 0; i < BarCount; i++)
            {
                float frac;
                if (_isPlaying)
                {
                    var angle = MathF.Tau * Cycles[i] * _phase + PhaseOffsets[i];
                    var wave = 0.5f + 0.5f * MathF.Sin(angle);
                    frac = 0.24f + 0.76f * (0.35f * BaseFractions[i] + 0.65f * wave);
                }
                else
                {
                    frac = 0.24f + 0.22f * BaseFractions[i];
                }

                var halfBar = Math.Max(Dp(1.4f), maxBarH * frac * 0.5f);
                var x = padX + i * stepX;
                canvas.DrawLine(x, cy - halfBar, x, cy + halfBar, _paint);
            }
        }

        private float Dp(float dp) => dp * (Resources?.DisplayMetrics?.Density ?? 2f);
    }
}
