using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using Musebase.Android.UI;

namespace Musebase.Android;

internal enum ShareEditorTab
{
    None,
    Color,
    Background,
    Typography,
}

public sealed partial class LyricShareActivity
{
    private readonly Handler _previewHandler = new(Looper.MainLooper!);
    private Action? _pendingPreviewRefresh;
    private ShareEditorTab _activeEditor = ShareEditorTab.None;
    private LinearLayout _editorPanel = null!;
    private ScrollView _editorScroll = null!;
    private LinearLayout _editorBody = null!;
    private readonly Dictionary<ShareEditorTab, TextView> _editorTabButtons = new();
    private readonly List<View> _colorEditorDots = new();
    private readonly List<TextView> _colorEditorLabels = new();
    private int _selectedEffectParam;

    private ShareBackgroundMode _backgroundMode;
    private ShareBackgroundMode _nonCustomBackgroundMode;
    private ShareCoverEffectSettings _coverEffect;
    private ShareLyricsFont _lyricsFont;
    private int _fontPercent;
    private bool _clipboardKeepBackdrop;
    private bool _editorPreferencesDirty;

    private static readonly string[] BackgroundLabels = ["渐变", "纯色", "封面", "自选"];
    private static readonly string[] EffectLabels = ["清晰原图", "柔和虚化", "弥散光斑", "渐变融色", "氛围染印", "暗调电影"];
    private static readonly string[] FontLabels = ["跟随样式", "系统无衬线", "系统衬线", "系统等宽", "Spotify Mix"];
    private static readonly (string Label, int Min, int Max)[] EffectParams =
    [
        ("模糊", 0, 100),
        ("暗度", 0, 85),
        ("融色", 0, 100),
        ("染印", 0, 100),
    ];

    private static string BackgroundLabel(ShareBackgroundMode mode) => BackgroundLabels[(int)mode];

    private void LoadEditorPreferences()
    {
        var settings = LyrxApp.Instance.Settings;
        _backgroundMode = _nonCustomBackgroundMode = settings.LyricShareBackgroundMode switch
        {
            "solid" => ShareBackgroundMode.Solid,
            "album-cover" when _payload.CoverBitmap is not null => ShareBackgroundMode.AlbumCover,
            _ => ShareBackgroundMode.Gradient,
        };
        _fontPercent = Math.Clamp(settings.LyricShareFontPercent,
            LyricShareCardRenderer.MinFontPercent, LyricShareCardRenderer.MaxFontPercent);
        _clipboardKeepBackdrop = settings.LyricShareClipboardKeepBackdrop;

        var preset = ParseCoverEffectPreset(settings.LyricShareCoverEffectPreset);
        var defaults = ShareCoverEffectSettings.FromPreset(preset);
        _coverEffect = new ShareCoverEffectSettings(preset,
            Math.Clamp(settings.LyricShareCoverBlur ?? defaults.Blur, 0, 100),
            Math.Clamp(settings.LyricShareCoverDim ?? defaults.Dim, 0, 85),
            Math.Clamp(settings.LyricShareCoverFade ?? defaults.Fade, 0, 100),
            Math.Clamp(settings.LyricShareCoverTint ?? defaults.Tint, 0, 100));
    }

    private static ShareCoverEffect ParseCoverEffectPreset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ShareCoverEffect.SoftBlur;
        if (Enum.TryParse<ShareCoverEffect>(raw.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;

        return raw.Trim().ToLowerInvariant() switch
        {
            "clean" => ShareCoverEffect.Clean,
            "dreamy-blur" => ShareCoverEffect.DreamyBlur,
            "color-fade" => ShareCoverEffect.ColorFade,
            "color-tint" => ShareCoverEffect.ColorTint,
            "cinematic" => ShareCoverEffect.Cinematic,
            _ => ShareCoverEffect.SoftBlur,
        };
    }

    private void SwitchBackgroundMode(ShareBackgroundMode mode)
    {
        if (mode == ShareBackgroundMode.AlbumCover && _payload.CoverBitmap is null) return;
        _imageLoadVersion++;
        if (_backgroundMode == mode) return;
        _customBgBitmap = null;
        _customBgUri = null;
        _backgroundMode = _nonCustomBackgroundMode = mode;
        var settings = LyrxApp.Instance.Settings;
        settings.LyricShareBackgroundMode = mode switch
        {
            ShareBackgroundMode.Solid => "solid",
            ShareBackgroundMode.AlbumCover => "album-cover",
            _ => "gradient",
        };
        settings.Save();
        RefreshAllVisuals(crossfade: true);
    }

    private LinearLayout BuildEditorPanel()
    {
        var panel = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Background = UiHelpers.RoundedRect(this, Color.Argb(224, 16, 18, 20), 20f, Color.Argb(40, 255, 255, 255), 1f),
            Elevation = Dp(14),
            Visibility = ViewStates.Gone,
            Clickable = true,
        };

        var header = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(Dp(12), Dp(9), Dp(12), Dp(8));

        var tabs = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        tabs.SetGravity(GravityFlags.CenterVertical);

        _editorTabButtons.Clear();
        AddEditorHeaderTab(tabs, ShareEditorTab.Color, "配色");
        AddEditorHeaderTab(tabs, ShareEditorTab.Background, "背景");
        AddEditorHeaderTab(tabs, ShareEditorTab.Typography, "排版");
        header.AddView(tabs, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var doneBtn = new TextView(this)
        {
            Text = "完成",
            Gravity = GravityFlags.Center,
            Clickable = true,
            Focusable = true,
            Background = UiHelpers.RoundedRect(this, Color.White, 15f),
        };
        doneBtn.SetIncludeFontPadding(false);
        doneBtn.SetTextColor(Color.Rgb(18, 18, 18));
        doneBtn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12.5f);
        UiHelpers.SetFontWeight(doneBtn, UiHelpers.FontWeightBold);
        doneBtn.SetPadding(Dp(14), 0, Dp(14), 0);
        doneBtn.Click += (_, _) => CloseEditor();
        AttachPressSpring(doneBtn);
        header.AddView(doneBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)));
        panel.AddView(header, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var divider = new View(this);
        divider.SetBackgroundColor(Color.Argb(24, 255, 255, 255));
        panel.AddView(divider, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Math.Max(1, Dp(0.8f))));

        _editorBody = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        _editorBody.SetPadding(Dp(14), Dp(11), Dp(14), Dp(13));

        _editorScroll = new ScrollView(this) { VerticalScrollBarEnabled = false };
        _editorScroll.AddView(_editorBody, new ScrollView.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        panel.AddView(_editorScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        return panel;
    }

    private void AddEditorHeaderTab(LinearLayout container, ShareEditorTab tab, string label)
    {
        var btn = new TextView(this)
        {
            Text = label,
            Gravity = GravityFlags.Center,
            Clickable = true,
            Focusable = true,
        };
        btn.SetIncludeFontPadding(false);
        btn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12.5f);
        btn.SetPadding(Dp(12), 0, Dp(12), 0);
        btn.Click += (_, _) =>
        {
            if (_activeEditor != tab)
                OpenEditor(tab);
        };
        AttachPressSpring(btn);
        _editorTabButtons[tab] = btn;
        container.AddView(btn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30))
        {
            RightMargin = Dp(4),
        });
    }

    private void OpenEditor(ShareEditorTab tab, bool animate = true)
    {
        if (tab == ShareEditorTab.None)
        {
            CloseEditor();
            return;
        }

        if (_activeEditor == tab && _editorPanel.Visibility == ViewStates.Visible)
        {
            CloseEditor();
            return;
        }

        var prevTab = _activeEditor;
        var wasClosed = prevTab == ShareEditorTab.None;
        _activeEditor = tab;
        RegisterEditorBackCallback();

        RefreshActiveEditorPanel(animateTabSwitch: animate && !wasClosed ? (int)tab - (int)prevTab : 0);
        RefreshAllVisuals();
        UpdateControlsHostBounds(animateSize: animate && wasClosed);
        UpdatePreviewImageMaxBounds();

        _controlsScroll.Animate()?.Cancel();
        _editorPanel.Animate()?.Cancel();

        if (!animate)
        {
            _controlsScroll.Visibility = ViewStates.Gone;
            _controlsScroll.Alpha = 1f;
            _controlsScroll.TranslationX = 0f;
            _controlsScroll.TranslationY = 0f;
            _editorPanel.Visibility = ViewStates.Visible;
            _editorPanel.Alpha = 1f;
            _editorPanel.TranslationX = 0f;
            _editorPanel.TranslationY = 0f;
            _editorPanel.ScaleX = 1f;
            _editorPanel.ScaleY = 1f;
            return;
        }

        if (wasClosed)
        {
            if (_controlsScroll.Visibility == ViewStates.Visible)
            {
                _controlsScroll.Animate()
                    ?.Alpha(0f)
                    ?.TranslationY(_isLandscapeLayout ? 0f : Dp(12))
                    ?.TranslationX(_isLandscapeLayout ? Dp(12) : 0f)
                    ?.SetDuration(140)
                    ?.SetInterpolator(new DecelerateInterpolator(1.5f))
                    ?.WithEndAction(new Java.Lang.Runnable(() =>
                    {
                        if (_activeEditor != ShareEditorTab.None)
                            _controlsScroll.Visibility = ViewStates.Gone;
                        _controlsScroll.Alpha = 1f;
                        _controlsScroll.TranslationX = 0f;
                        _controlsScroll.TranslationY = 0f;
                    }))
                    ?.Start();
            }

            _editorPanel.Visibility = ViewStates.Visible;
            _editorPanel.Alpha = 0f;
            _editorPanel.ScaleX = 0.97f;
            _editorPanel.ScaleY = 0.97f;
            _editorPanel.TranslationY = _isLandscapeLayout ? 0f : Dp(22);
            _editorPanel.TranslationX = _isLandscapeLayout ? Dp(20) : 0f;
            _editorPanel.Animate()
                ?.Alpha(1f)
                ?.ScaleX(1f)
                ?.ScaleY(1f)
                ?.TranslationY(0f)
                ?.TranslationX(0f)
                ?.SetDuration(230)
                ?.SetInterpolator(new DecelerateInterpolator(1.75f))
                ?.Start();
        }
        else
        {
            _controlsScroll.Visibility = ViewStates.Gone;
            _editorPanel.Visibility = ViewStates.Visible;
            _editorPanel.Alpha = 1f;
            _editorPanel.ScaleX = 1f;
            _editorPanel.ScaleY = 1f;
            _editorPanel.TranslationX = 0f;
            _editorPanel.TranslationY = 0f;
        }
    }

    private void CloseEditor()
    {
        if (_activeEditor == ShareEditorTab.None) return;
        _activeEditor = ShareEditorTab.None;
        UnregisterEditorBackCallback();
        SaveEditorPreferences();
        RefreshAllVisuals();
        UpdateControlsHostBounds(animateSize: true);
        UpdatePreviewImageMaxBounds();

        _controlsScroll.Animate()?.Cancel();
        _editorPanel.Animate()?.Cancel();

        _editorPanel.Animate()
            ?.Alpha(0f)
            ?.ScaleX(0.97f)
            ?.ScaleY(0.97f)
            ?.TranslationY(_isLandscapeLayout ? 0f : Dp(18))
            ?.TranslationX(_isLandscapeLayout ? Dp(18) : 0f)
            ?.SetDuration(175)
            ?.SetInterpolator(new DecelerateInterpolator(1.5f))
            ?.WithEndAction(new Java.Lang.Runnable(() =>
            {
                if (_activeEditor == ShareEditorTab.None)
                    _editorPanel.Visibility = ViewStates.Gone;
                _editorPanel.Alpha = 1f;
                _editorPanel.ScaleX = 1f;
                _editorPanel.ScaleY = 1f;
                _editorPanel.TranslationX = 0f;
                _editorPanel.TranslationY = 0f;
            }))
            ?.Start();

        _controlsScroll.Visibility = ViewStates.Visible;
        _controlsScroll.Alpha = 0f;
        _controlsScroll.TranslationY = _isLandscapeLayout ? 0f : Dp(12);
        _controlsScroll.TranslationX = _isLandscapeLayout ? Dp(12) : 0f;
        _controlsScroll.Animate()
            ?.Alpha(1f)
            ?.TranslationY(0f)
            ?.TranslationX(0f)
            ?.SetDuration(215)
            ?.SetInterpolator(new DecelerateInterpolator(1.65f))
            ?.Start();
    }

    private void RefreshActiveEditorPanel(int animateTabSwitch = 0)
    {
        if (_activeEditor == ShareEditorTab.None) return;

        foreach (var (tab, btn) in _editorTabButtons)
        {
            var active = tab == _activeEditor;
            btn.Background = active ? UiHelpers.RoundedRect(this, Color.Argb(46, 255, 255, 255), 15f) : null;
            btn.SetTextColor(active ? Color.White : Color.Argb(172, 255, 255, 255));
            UiHelpers.SetFontWeight(btn, active ? UiHelpers.FontWeightBold : UiHelpers.FontWeightMedium);
        }

        _editorBody.Animate()?.Cancel();
        _editorBody.RemoveAllViews();
        _colorEditorDots.Clear();
        _colorEditorLabels.Clear();

        switch (_activeEditor)
        {
            case ShareEditorTab.Color:
                PopulateColorEditor();
                break;
            case ShareEditorTab.Background:
                PopulateBackgroundEditor();
                break;
            case ShareEditorTab.Typography:
                PopulateTypographyEditor();
                break;
        }

        _editorScroll.ScrollTo(0, 0);
        UpdateControlsHostBounds();

        if (animateTabSwitch != 0)
        {
            var dir = animateTabSwitch > 0 ? 1 : -1;
            _editorBody.Alpha = 0f;
            _editorBody.ScaleX = 0.985f;
            _editorBody.ScaleY = 0.985f;
            _editorBody.TranslationX = dir * Dp(18);
            _editorBody.Animate()
                ?.Alpha(1f)
                ?.ScaleX(1f)
                ?.ScaleY(1f)
                ?.TranslationX(0f)
                ?.SetDuration(205)
                ?.SetInterpolator(new DecelerateInterpolator(1.65f))
                ?.Start();
        }
        else
        {
            _editorBody.Alpha = 1f;
            _editorBody.ScaleX = 1f;
            _editorBody.ScaleY = 1f;
            _editorBody.TranslationX = 0f;
        }
    }

    private void PopulateColorEditor()
    {
        AddSectionCaption(_editorBody, "主题配色");
        var dotSize = Dp(_isLandscapeLayout && !UiHelpers.IsTabletLayout(this) ? 36 : 40);
        var dotRadius = dotSize / 2f / Math.Max(1f, Resources?.DisplayMetrics?.Density ?? 2f);
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
                cell.SetPadding(Dp(2), Dp(6), Dp(2), Dp(6));

                var dot = new View(this);
                cell.AddView(dot, new LinearLayout.LayoutParams(dotSize, dotSize)
                {
                    BottomMargin = Dp(6),
                });
                _colorEditorDots.Add(dot);

                var label = new TextView(this)
                {
                    Text = idx == 0 && _backgroundMode == ShareBackgroundMode.CustomImage ? "图片取色" : _presets[idx].Label,
                    Gravity = GravityFlags.Center,
                    TextAlignment = TextAlignment.Center,
                };
                label.SetIncludeFontPadding(false);
                label.SetMaxLines(1);
                label.SetTextColor(Color.Argb(210, 255, 255, 255));
                label.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11f);
                UiHelpers.SetFontWeight(label, UiHelpers.FontWeightMedium);
                cell.AddView(label, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
                _colorEditorLabels.Add(label);

                var capturedIdx = idx;
                AttachPressSpring(cell);
                cell.Click += (_, _) =>
                {
                    if (_colorIndex == capturedIdx) return;
                    _colorIndex = capturedIdx;
                    RefreshAllVisuals(crossfade: true);
                    dot.Animate()?.Cancel();
                    dot.ScaleX = 0.86f;
                    dot.ScaleY = 0.86f;
                    dot.Animate()?.ScaleX(1f)?.ScaleY(1f)?.SetDuration(200)?.SetInterpolator(new OvershootInterpolator(1.6f))?.Start();
                };

                rowLayout.AddView(cell, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            }

            var rowLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            if (r < rows - 1) rowLp.BottomMargin = Dp(10);
            _editorBody.AddView(rowLayout, rowLp);
        }

        SyncColorEditorDots(dotRadius);
    }

    private void SyncColorEditorDots(float? radiusDp = null)
    {
        if (_colorEditorDots.Count == 0) return;
        var r = radiusDp ?? (_isLandscapeLayout && !UiHelpers.IsTabletLayout(this) ? 17f : 19f);
        for (var i = 0; i < _colorEditorDots.Count; i++)
        {
            var selected = i == _colorIndex;
            var isCustomFirst = i == 0 && _backgroundMode == ShareBackgroundMode.CustomImage;
            var color = isCustomFirst ? _customBgThemeColor : _presets[i].BaseColor;
            _colorEditorDots[i].Background = UiHelpers.RoundedRect(
                this,
                color,
                r,
                selected ? Color.White : Color.Argb(86, 255, 255, 255),
                selected ? 2.6f : 1f);
            if (i < _colorEditorLabels.Count)
            {
                _colorEditorLabels[i].Text = isCustomFirst ? "图片取色" : _presets[i].Label;
                _colorEditorLabels[i].SetTextColor(selected ? Color.White : Color.Argb(185, 255, 255, 255));
            }
        }
    }

    private void PopulateBackgroundEditor()
    {
        // 1. 四合一背景来源分段条：渐变 / 纯色 / 封面 / 自选
        var sourceRow = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        var sourceButtons = new List<(ShareBackgroundMode Mode, TextView View)>(4);
        var sourceModes = new[]
        {
            ShareBackgroundMode.Gradient,
            ShareBackgroundMode.Solid,
            ShareBackgroundMode.AlbumCover,
            ShareBackgroundMode.CustomImage,
        };
        for (var i = 0; i < sourceModes.Length; i++)
        {
            var mode = sourceModes[i];
            var btn = MakeEditorPillButton(BackgroundLabel(mode));
            btn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
            btn.Enabled = mode != ShareBackgroundMode.AlbumCover || _payload.CoverBitmap is not null;
            AttachPressSpring(btn);
            sourceButtons.Add((mode, btn));
            sourceRow.AddView(btn, new LinearLayout.LayoutParams(0, Dp(34), 1f)
            {
                RightMargin = i < sourceModes.Length - 1 ? Dp(6) : 0,
            });
        }
        _editorBody.AddView(sourceRow);

        // 2. 自选图片子操作行（仅在 CustomImage 时显示，避免占空间）
        var customSubRow = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        var replaceBtn = MakeEditorPillButton("更换图片");
        replaceBtn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        replaceBtn.Click += (_, _) => OpenCustomBackgroundPicker();
        AttachPressSpring(replaceBtn);

        var clearBtn = MakeEditorPillButton("清除图片");
        clearBtn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        AttachPressSpring(clearBtn);

        customSubRow.AddView(replaceBtn, new LinearLayout.LayoutParams(0, Dp(32), 1f) { RightMargin = Dp(8) });
        customSubRow.AddView(clearBtn, new LinearLayout.LayoutParams(0, Dp(32), 1f));
        _editorBody.AddView(customSubRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(8),
        });

        // 3. 图片效果区（仅在 封面 / 自选 时显示）：单行横向滤镜条 + 参数芯片 + 全宽精度滑块
        var effects = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        effects.SetPadding(0, Dp(10), 0, 0);
        _editorBody.AddView(effects);

        AddSectionCaption(effects, "图片效果");
        var presetScroll = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
        var presetTrack = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        presetScroll.AddView(presetTrack, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        var presetChips = new List<TextView>(EffectLabels.Length);
        for (var i = 0; i < EffectLabels.Length; i++)
        {
            var chip = MakeEditorPillButton(EffectLabels[i]);
            chip.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
            chip.SetPadding(Dp(12), 0, Dp(12), 0);
            chip.Background = UiHelpers.RoundedRect(this, Color.Argb(32, 255, 255, 255), 16f);
            AttachPressSpring(chip);
            presetChips.Add(chip);
            presetTrack.AddView(chip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(32))
            {
                RightMargin = i < EffectLabels.Length - 1 ? Dp(6) : 0,
            });
        }
        effects.AddView(presetScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // 4 个调节参数切换标签（模糊 / 暗度 / 融色 / 染印）+ 单条全宽滑块
        var paramRow = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        var paramHolders = new List<(LinearLayout Host, TextView Name, TextView Value)>(4);
        for (var i = 0; i < EffectParams.Length; i++)
        {
            var host = new LinearLayout(this)
            {
                Orientation = global::Android.Widget.Orientation.Vertical,
                Clickable = true,
                Focusable = true,
            };
            host.SetGravity(GravityFlags.Center);
            host.SetPadding(Dp(4), Dp(5), Dp(4), Dp(5));
            AttachPressSpring(host);

            var nameTv = new TextView(this)
            {
                Text = EffectParams[i].Label,
                Gravity = GravityFlags.Center,
                TextAlignment = TextAlignment.Center,
            };
            nameTv.SetIncludeFontPadding(false);
            nameTv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10.5f);
            UiHelpers.SetFontWeight(nameTv, UiHelpers.FontWeightMedium);

            var valTv = new TextView(this)
            {
                Gravity = GravityFlags.Center,
                TextAlignment = TextAlignment.Center,
            };
            valTv.SetIncludeFontPadding(false);
            valTv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
            UiHelpers.SetFontWeight(valTv, UiHelpers.FontWeightBold);
            UiHelpers.SetTabularNumerals(valTv);

            host.AddView(nameTv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            host.AddView(valTv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = Dp(1),
            });
            paramHolders.Add((host, nameTv, valTv));
            paramRow.AddView(host, new LinearLayout.LayoutParams(0, Dp(42), 1f)
            {
                RightMargin = i < EffectParams.Length - 1 ? Dp(6) : 0,
            });
        }
        effects.AddView(paramRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(10),
        });

        var nonImageCard = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Background = UiHelpers.RoundedRect(this, Color.Argb(22, 255, 255, 255), 14f, Color.Argb(20, 255, 255, 255), 1f),
        };
        nonImageCard.SetGravity(GravityFlags.CenterHorizontal);
        nonImageCard.SetPadding(Dp(14), Dp(18), Dp(14), Dp(16));
        var nonImageText = new TextView(this)
        {
            Text = "当前使用渐变或纯色主题背景\n选择「封面 / 自选」可调整模糊与光影参数",
            Gravity = GravityFlags.Center,
            TextAlignment = TextAlignment.Center,
        };
        nonImageText.SetIncludeFontPadding(false);
        nonImageText.SetLineSpacing(Dp(3), 1f);
        nonImageText.SetTextColor(Color.Argb(182, 255, 255, 255));
        nonImageText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        UiHelpers.SetFontWeight(nonImageText, UiHelpers.FontWeightRegular);
        nonImageCard.AddView(nonImageText, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var gotoColorBtn = MakeEditorPillButton("前往调整主题配色");
        gotoColorBtn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
        gotoColorBtn.SetPadding(Dp(16), 0, Dp(16), 0);
        gotoColorBtn.Background = UiHelpers.RoundedRect(this, Color.Argb(40, 255, 255, 255), 16f);
        gotoColorBtn.Click += (_, _) => OpenEditor(ShareEditorTab.Color);
        AttachPressSpring(gotoColorBtn);
        nonImageCard.AddView(gotoColorBtn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, Dp(32))
        {
            TopMargin = Dp(12),
        });
        _editorBody.AddView(nonImageCard, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(16),
        });

        var sliderHeader = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        sliderHeader.SetGravity(GravityFlags.CenterVertical);
        var activeParamName = new TextView(this);
        activeParamName.SetIncludeFontPadding(false);
        activeParamName.SetTextColor(Color.Argb(220, 255, 255, 255));
        activeParamName.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        UiHelpers.SetFontWeight(activeParamName, UiHelpers.FontWeightMedium);

        var activeParamValue = new TextView(this);
        activeParamValue.SetIncludeFontPadding(false);
        activeParamValue.SetTextColor(Color.White);
        activeParamValue.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12.5f);
        UiHelpers.SetFontWeight(activeParamValue, UiHelpers.FontWeightBold);
        UiHelpers.SetTabularNumerals(activeParamValue);

        var resetPresetBtn = new TextView(this)
        {
            Text = "重置预设",
            Gravity = GravityFlags.Center,
            Clickable = true,
            Focusable = true,
            Background = UiHelpers.RoundedRect(this, Color.Argb(34, 255, 255, 255), 11f),
        };
        resetPresetBtn.SetIncludeFontPadding(false);
        resetPresetBtn.SetTextColor(Color.Argb(215, 255, 255, 255));
        resetPresetBtn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10.5f);
        UiHelpers.SetFontWeight(resetPresetBtn, UiHelpers.FontWeightMedium);
        resetPresetBtn.SetPadding(Dp(9), 0, Dp(9), 0);
        AttachPressSpring(resetPresetBtn);

        sliderHeader.AddView(activeParamName, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            RightMargin = Dp(6),
        });
        sliderHeader.AddView(activeParamValue, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        sliderHeader.AddView(resetPresetBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(22)));
        effects.AddView(sliderHeader, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(9),
        });

        var selectedIdx = Math.Clamp(_selectedEffectParam, 0, EffectParams.Length - 1);
        var slider = new SpotifySliderView(
            this,
            EffectParams[selectedIdx].Min,
            EffectParams[selectedIdx].Max,
            GetEffectParamValue(selectedIdx));
        slider.ValueChanged += v =>
        {
            SetEffectParamValue(_selectedEffectParam, v);
            SyncParamControls();
        };
        effects.AddView(slider, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(34))
        {
            TopMargin = Dp(2),
        });

        for (var i = 0; i < presetChips.Count; i++)
        {
            var captured = i;
            presetChips[i].Click += (_, _) =>
            {
                var preset = (ShareCoverEffect)captured;
                UpdateCoverEffect(ShareCoverEffectSettings.FromPreset(preset), immediateCrossfade: true);
                SyncEffectChips();
                SyncParamControls();
                ScrollChipIntoView(presetScroll, presetChips[captured]);
            };
        }

        for (var i = 0; i < paramHolders.Count; i++)
        {
            var captured = i;
            paramHolders[i].Host.Click += (_, _) =>
            {
                if (_selectedEffectParam == captured) return;
                _selectedEffectParam = captured;
                SyncParamControls();
            };
        }

        resetPresetBtn.Click += (_, _) =>
        {
            var defaults = ShareCoverEffectSettings.FromPreset(_coverEffect.Preset);
            if (_coverEffect == defaults) return;
            UpdateCoverEffect(defaults, immediateCrossfade: true);
            SyncParamControls();
        };

        foreach (var (mode, view) in sourceButtons)
        {
            view.Click += (_, _) =>
            {
                if (mode == ShareBackgroundMode.CustomImage)
                {
                    OpenCustomBackgroundPicker();
                    return;
                }
                SwitchBackgroundMode(mode);
                SyncBackgroundControls();
                UpdateControlsHostBounds();
            };
        }

        clearBtn.Click += (_, _) =>
        {
            SwitchBackgroundMode(_nonCustomBackgroundMode);
            SyncBackgroundControls();
            UpdateControlsHostBounds();
        };

        SyncEffectChips();
        SyncParamControls();
        SyncBackgroundControls();
        presetScroll.Post(() => ScrollChipIntoView(presetScroll, presetChips[(int)_coverEffect.Preset], smooth: false));

        void SyncEffectChips()
        {
            var currentPresetIdx = (int)_coverEffect.Preset;
            for (var i = 0; i < presetChips.Count; i++)
                StyleCapsuleChip(presetChips[i], i == currentPresetIdx);
        }

        void SyncParamControls()
        {
            var activeParam = Math.Clamp(_selectedEffectParam, 0, EffectParams.Length - 1);
            _selectedEffectParam = activeParam;
            for (var i = 0; i < paramHolders.Count; i++)
            {
                var isSelected = i == activeParam;
                var val = GetEffectParamValue(i);
                var (host, nameTv, valTv) = paramHolders[i];
                valTv.Text = $"{val}%";
                host.Background = UiHelpers.RoundedRect(
                    this,
                    isSelected ? Color.Argb(44, 30, 215, 96) : Color.Argb(28, 255, 255, 255),
                    12f,
                    isSelected ? LyricsPalette.AccentGreen : Color.Argb(24, 255, 255, 255),
                    isSelected ? 1.2f : 1f);
                nameTv.SetTextColor(isSelected ? Color.White : Color.Argb(175, 255, 255, 255));
                valTv.SetTextColor(isSelected ? LyricsPalette.AccentGreen : Color.White);
            }

            var meta = EffectParams[activeParam];
            var curVal = GetEffectParamValue(activeParam);
            activeParamName.Text = meta.Label;
            activeParamValue.Text = $"{curVal}%";
            slider.SetRange(meta.Min, meta.Max, curVal);

            var isDefaultPreset = _coverEffect == ShareCoverEffectSettings.FromPreset(_coverEffect.Preset);
            resetPresetBtn.Visibility = isDefaultPreset ? ViewStates.Invisible : ViewStates.Visible;
            resetPresetBtn.Enabled = !isDefaultPreset;
        }

        void SyncBackgroundControls()
        {
            foreach (var (mode, view) in sourceButtons)
            {
                var selected = mode == _backgroundMode;
                view.Selected = selected;
                view.Alpha = view.Enabled ? 1f : 0.36f;
                view.SetTextColor(selected ? Color.Rgb(18, 18, 18) : Color.White);
                UiHelpers.SetFontWeight(view, selected ? UiHelpers.FontWeightBold : UiHelpers.FontWeightMedium);
                view.Background = UiHelpers.RoundedRect(
                    this,
                    selected ? Color.White : Color.Argb(32, 255, 255, 255),
                    12f);
            }
            var custom = _backgroundMode == ShareBackgroundMode.CustomImage;
            var hasEffects = custom || _backgroundMode == ShareBackgroundMode.AlbumCover;
            customSubRow.Visibility = custom ? ViewStates.Visible : ViewStates.Gone;
            effects.Visibility = hasEffects ? ViewStates.Visible : ViewStates.Gone;
            nonImageCard.Visibility = hasEffects ? ViewStates.Gone : ViewStates.Visible;
        }
    }

    private void PopulateTypographyEditor()
    {
        AddSectionCaption(_editorBody, "歌词字体");

        var fontScroll = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
        var fontTrack = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        fontScroll.AddView(fontTrack, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var fontChips = new List<TextView>(FontLabels.Length);
        for (var i = 0; i < FontLabels.Length; i++)
        {
            var chip = MakeEditorPillButton(FontLabels[i]);
            chip.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
            chip.SetPadding(Dp(13), 0, Dp(13), 0);
            ApplyChipFontPreview(chip, (ShareLyricsFont)i);
            AttachPressSpring(chip);
            fontChips.Add(chip);
            fontTrack.AddView(chip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(34))
            {
                RightMargin = i < FontLabels.Length - 1 ? Dp(6) : 0,
            });

            var captured = i;
            chip.Click += (_, _) =>
            {
                var next = (ShareLyricsFont)captured;
                if (_lyricsFont == next) return;
                _lyricsFont = next;
                SyncFontChips();
                ScrollChipIntoView(fontScroll, chip);
                RefreshAllVisuals(crossfade: true);
            };
        }
        _editorBody.AddView(fontScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        void SyncFontChips()
        {
            var selectedIdx = (int)_lyricsFont;
            for (var i = 0; i < fontChips.Count; i++)
                StyleCapsuleChip(fontChips[i], i == selectedIdx);
        }
        SyncFontChips();
        fontScroll.Post(() => ScrollChipIntoView(fontScroll, fontChips[(int)_lyricsFont], smooth: false));

        var sizeHeader = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        sizeHeader.SetGravity(GravityFlags.CenterVertical);
        var sizeLabel = new TextView(this) { Text = "字号" };
        sizeLabel.SetIncludeFontPadding(false);
        sizeLabel.SetTextColor(Color.Argb(215, 255, 255, 255));
        sizeLabel.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        UiHelpers.SetFontWeight(sizeLabel, UiHelpers.FontWeightMedium);

        var sizeValue = new TextView(this) { Text = $"{_fontPercent}%" };
        sizeValue.SetIncludeFontPadding(false);
        sizeValue.SetTextColor(Color.White);
        sizeValue.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12.5f);
        UiHelpers.SetFontWeight(sizeValue, UiHelpers.FontWeightBold);
        UiHelpers.SetTabularNumerals(sizeValue);

        var resetBtn = new TextView(this)
        {
            Text = "恢复自动",
            Gravity = GravityFlags.Center,
            Clickable = true,
            Focusable = true,
            Background = UiHelpers.RoundedRect(this, Color.Argb(34, 255, 255, 255), 11f),
        };
        resetBtn.SetIncludeFontPadding(false);
        resetBtn.SetTextColor(Color.Argb(220, 255, 255, 255));
        resetBtn.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11f);
        UiHelpers.SetFontWeight(resetBtn, UiHelpers.FontWeightMedium);
        resetBtn.SetPadding(Dp(10), 0, Dp(10), 0);
        AttachPressSpring(resetBtn);

        sizeHeader.AddView(sizeLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            RightMargin = Dp(6),
        });
        sizeHeader.AddView(sizeValue, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        sizeHeader.AddView(resetBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(24)));
        _editorBody.AddView(sizeHeader, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(20),
        });

        var slider = new SpotifySliderView(
            this,
            LyricShareCardRenderer.MinFontPercent,
            LyricShareCardRenderer.MaxFontPercent,
            _fontPercent);

        void SyncSizeReadout()
        {
            sizeValue.Text = $"{_fontPercent}%";
            var isAuto = _fontPercent == 100;
            resetBtn.Enabled = !isAuto;
            resetBtn.Alpha = isAuto ? 0.35f : 1f;
        }
        SyncSizeReadout();

        slider.ValueChanged += v =>
        {
            SetFontPercent(v);
            SyncSizeReadout();
        };
        resetBtn.Click += (_, _) =>
        {
            if (_fontPercent == 100) return;
            SetFontPercent(100, immediateCrossfade: true);
            slider.SetValue(100);
            SyncSizeReadout();
        };
        _editorBody.AddView(slider, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(34))
        {
            TopMargin = Dp(3),
        });
    }

    private int GetEffectParamValue(int index) => index switch
    {
        1 => _coverEffect.Dim,
        2 => _coverEffect.Fade,
        3 => _coverEffect.Tint,
        _ => _coverEffect.Blur,
    };

    private void SetEffectParamValue(int index, int value)
    {
        var next = index switch
        {
            1 => _coverEffect with { Dim = value },
            2 => _coverEffect with { Fade = value },
            3 => _coverEffect with { Tint = value },
            _ => _coverEffect with { Blur = value },
        };
        UpdateCoverEffect(next);
    }

    private static void ScrollChipIntoView(HorizontalScrollView scroll, View chip, bool smooth = true)
    {
        if (scroll.Width <= 0 || chip.Width <= 0) return;
        var targetX = Math.Max(0, chip.Left - (scroll.Width - chip.Width) / 2);
        if (smooth) scroll.SmoothScrollTo(targetX, 0);
        else scroll.ScrollTo(targetX, 0);
    }

    private void ApplyChipFontPreview(TextView chip, ShareLyricsFont font)
    {
        try
        {
            chip.Typeface = font switch
            {
                ShareLyricsFont.SansSerif => Typeface.SansSerif,
                ShareLyricsFont.Serif => Typeface.Serif,
                ShareLyricsFont.Monospace => Typeface.Monospace,
                ShareLyricsFont.Spotify => UiHelpers.ResolveFontWeight(this, UiHelpers.FontWeightBold, useTitleFont: true),
                _ => UiHelpers.ResolveSystemFontWeight(UiHelpers.FontWeightMedium),
            };
        }
        catch { }
    }

    private void AddSectionCaption(LinearLayout parent, string text)
    {
        var label = new TextView(this) { Text = text };
        label.SetIncludeFontPadding(false);
        label.SetTextColor(Color.Argb(182, 255, 255, 255));
        label.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 11.5f);
        UiHelpers.SetFontWeight(label, UiHelpers.FontWeightSemiBold);
        parent.AddView(label, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(6),
        });
    }

    private void StyleCapsuleChip(TextView chip, bool selected)
    {
        chip.Selected = selected;
        chip.SetTextColor(selected ? Color.Rgb(18, 18, 18) : Color.Argb(232, 255, 255, 255));
        chip.Background = UiHelpers.RoundedRect(
            this,
            selected ? Color.White : Color.Argb(32, 255, 255, 255),
            16f,
            selected ? null : Color.Argb(24, 255, 255, 255),
            1f);
        UiHelpers.SetFontWeight(chip, selected ? UiHelpers.FontWeightBold : UiHelpers.FontWeightMedium);
    }

    private TextView MakeEditorPillButton(string title)
    {
        var button = new TextView(this)
        {
            Text = title,
            Gravity = GravityFlags.Center,
            Clickable = true,
            Focusable = true,
            Background = UiHelpers.RoundedRect(this, Color.Argb(34, 255, 255, 255), 12f, Color.Argb(30, 255, 255, 255), 1f),
        };
        button.SetIncludeFontPadding(false);
        button.SetTextColor(Color.White);
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12.5f);
        button.SetPadding(Dp(8), 0, Dp(8), 0);
        UiHelpers.SetFontWeight(button, UiHelpers.FontWeightMedium);
        return button;
    }

    private void UpdateCoverEffect(ShareCoverEffectSettings effect, bool immediateCrossfade = false)
    {
        _coverEffect = effect;
        _editorPreferencesDirty = true;
        if (immediateCrossfade) RefreshAllVisuals(crossfade: true);
        else SchedulePreviewRefresh();
    }

    private void SetFontPercent(int percent, bool immediateCrossfade = false)
    {
        _fontPercent = percent;
        _editorPreferencesDirty = true;
        if (immediateCrossfade) RefreshAllVisuals(crossfade: true);
        else SchedulePreviewRefresh();
    }

    private void SaveEditorPreferences()
    {
        if (!_editorPreferencesDirty) return;
        _editorPreferencesDirty = false;
        var settings = LyrxApp.Instance.Settings;
        settings.LyricShareFontPercent = _fontPercent;
        settings.LyricShareCoverEffectPreset = _coverEffect.Preset.ToString();
        settings.LyricShareCoverBlur = _coverEffect.Blur;
        settings.LyricShareCoverDim = _coverEffect.Dim;
        settings.LyricShareCoverFade = _coverEffect.Fade;
        settings.LyricShareCoverTint = _coverEffect.Tint;
        settings.Save();
    }

    private void SchedulePreviewRefresh()
    {
        if (_pendingPreviewRefresh is not null) return;
        _pendingPreviewRefresh = () =>
        {
            _pendingPreviewRefresh = null;
            if (!_disposed) RefreshAllVisuals();
        };
        _previewHandler.PostDelayed(_pendingPreviewRefresh, 65);
    }

    private void CancelPreviewRefresh()
    {
        if (_pendingPreviewRefresh is not { } pending) return;
        _previewHandler.RemoveCallbacks(pending);
        _pendingPreviewRefresh = null;
    }

    private void RegisterEditorBackCallback()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        RegisterEditorBackCallbackApi33();
    }

    private void UnregisterEditorBackCallback()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        UnregisterEditorBackCallbackApi33();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("android33.0")]
    private void RegisterEditorBackCallbackApi33()
    {
        if (_editorBackCallback is not null) return;
        var cb = new EditorBackCallback(this);
        _editorBackCallback = cb;
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(
            global::Android.Window.IOnBackInvokedDispatcher.PriorityDefault,
            cb);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("android33.0")]
    private void UnregisterEditorBackCallbackApi33()
    {
        if (_editorBackCallback is not EditorBackCallback cb) return;
        _editorBackCallback = null;
        try { OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(cb); }
        catch { }
    }
}
