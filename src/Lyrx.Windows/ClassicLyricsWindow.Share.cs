using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Musebase.Windows.Services;

namespace Musebase.Windows;

internal sealed partial class ClassicLyricsWindow
{
    private (Border Header, Button CancelBtn, TextBlock CountText, TextBlock LimitHint) BuildShareHeaderBar()
    {
        var cancelBtn = new Button
        {
            Content = "取消",
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 14, 0),
            Background = CreateOffsetAlphaBrush(28, 255, 255, 255),
            Foreground = Brushes.White,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "退出分享选择 (Esc)",
            VerticalAlignment = VerticalAlignment.Center,
            Template = TransportTemplate(cornerRadius: 15),
        };
        AutomationProperties.SetName(cancelBtn, "取消分享选择");
        cancelBtn.Click += (_, _) => ExitShareSelectionMode(resumeAutoScroll: true);

        var titleText = new TextBlock
        {
            Text = "选择歌词",
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
        };

        var countText = new TextBlock
        {
            Text = "已选择 0 / 6 行歌词",
            FontSize = 11.5,
            Foreground = CreateOffsetAlphaBrush(215, 255, 255, 255),
            Margin = new Thickness(0, 1, 0, 0),
        };

        var limitHint = new TextBlock
        {
            Text = $"最多可选择 {MaxShareSelectionCount} 行歌词",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateOffsetBrush(255, 214, 102),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(titleText);
        textStack.Children.Add(countText);

        var leftRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftRow.Children.Add(cancelBtn);
        leftRow.Children.Add(textStack);
        leftRow.Children.Add(limitHint);

        var bar = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            Background = CreateOffsetAlphaBrush(228, 18, 18, 18),
            BorderBrush = CreateOffsetAlphaBrush(36, 255, 255, 255),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 10, 148, 10),
            Visibility = Visibility.Collapsed,
            Child = leftRow,
        };
        Panel.SetZIndex(bar, 15);
        return (bar, cancelBtn, countText, limitHint);
    }

    private (Border Host, Button ContinueBtn) BuildShareActionBar()
    {
        var continueBtn = new Button
        {
            Content = "继续",
            MinWidth = 136,
            Height = 44,
            Padding = new Thickness(28, 0, 28, 0),
            Background = Brushes.White,
            Foreground = Chrome,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = "生成并预览歌词卡片",
            Template = TransportTemplate(cornerRadius: 22),
        };
        AutomationProperties.SetName(continueBtn, "继续生成歌词分享卡片");
        continueBtn.Click += (_, _) => OpenLyricSharePreview();

        var host = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 26),
            Visibility = Visibility.Collapsed,
            Child = continueBtn,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.48,
            },
        };
        Panel.SetZIndex(host, 15);
        return (host, continueBtn);
    }

    private ContextMenu CreateRowContextMenu(int lineIndex)
    {
        var menu = new ContextMenu
        {
            FontFamily = ClassicLyricsFonts.Body,
            Background = OffsetPopupBg,
            BorderBrush = OffsetPopupBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Template = DarkContextMenuTemplate(),
        };

        var itemTemplate = DarkMenuItemTemplate();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            if (!_isShareSelecting)
            {
                var shareItem = new MenuItem
                {
                    Header = "分享歌词…",
                    Foreground = Brushes.White,
                    Template = itemTemplate,
                };
                shareItem.Click += (_, _) => EnterShareSelectionMode(lineIndex);
                menu.Items.Add(shareItem);
                return;
            }

            var isSelected = _selectedShareLines.Contains(lineIndex);
            var toggleItem = new MenuItem
            {
                Header = isSelected ? "取消选中此行" : "选中此行",
                Foreground = Brushes.White,
                Template = itemTemplate,
            };
            toggleItem.Click += (_, _) => ToggleShareRowSelection(lineIndex);
            menu.Items.Add(toggleItem);

            var previewItem = new MenuItem
            {
                Header = "继续生成卡片…",
                IsEnabled = _selectedShareLines.Count > 0,
                Foreground = Brushes.White,
                Template = itemTemplate,
            };
            previewItem.Click += (_, _) => OpenLyricSharePreview();
            menu.Items.Add(previewItem);

            var cancelItem = new MenuItem
            {
                Header = "取消分享选择",
                Foreground = Brushes.White,
                Template = itemTemplate,
            };
            cancelItem.Click += (_, _) => ExitShareSelectionMode(resumeAutoScroll: true);
            menu.Items.Add(cancelItem);
        };
        return menu;
    }

    private static ControlTemplate DarkContextMenuTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, OffsetPopupBg);
        border.SetValue(Border.BorderBrushProperty, OffsetPopupBorder);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new Thickness(4));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(ContextMenu)) { VisualTree = border };
    }

    private static ControlTemplate DarkMenuItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bg");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new Thickness(12, 7, 14, 7));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = border };
        var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, OffsetBtnBg, "Bg"));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.42));
        template.Triggers.Add(disabled);
        return template;
    }

    private void OnLyricRowMouseEnter(int lineIndex)
    {
        if (!_isShareSelecting || !_rows.TryGetValue(lineIndex, out var row)) return;
        _hoveredShareLineIndex = lineIndex;
        SetRowVisual(row, lineIndex, animate: false);
    }

    private void OnLyricRowMouseLeave(int lineIndex)
    {
        if (!_isShareSelecting) return;
        if (_hoveredShareLineIndex == lineIndex)
            _hoveredShareLineIndex = null;
        if (_rows.TryGetValue(lineIndex, out var row))
            SetRowVisual(row, lineIndex, animate: false);
    }

    private void ToggleShareSelectionFromButton()
    {
        if (_isShareSelecting)
        {
            ExitShareSelectionMode(resumeAutoScroll: true);
            return;
        }
        if (_rows.Count == 0) return;
        EnterShareSelectionMode(initialLineIndex: null);
    }

    private void UpdateShareButton()
    {
        if (_shareBtn is null) return;
        var hasLyrics = _rows.Count > 0;
        _shareBtn.IsEnabled = hasLyrics || _isShareSelecting;
        ((ClassicTransportGlyph)_shareBtn.Content).SetState(ClassicTransportIcon.Share, active: _isShareSelecting);
        var label = !hasLyrics && !_isShareSelecting
            ? "暂无歌词可分享"
            : _isShareSelecting
                ? "退出歌词选择 (Esc)"
                : "分享歌词…";
        SetButtonLabel(_shareBtn, label);
    }

    private void EnterShareSelectionMode(int? initialLineIndex)
    {
        if (_lyrics is null || _rows.Count == 0) return;
        if (initialLineIndex is { } initial && !_rows.ContainsKey(initial)) return;

        _offsetPopup.IsOpen = false;
        CancelScrollAnimation();
        var keepManualOffset = initialLineIndex is null && _manualScroll;
        _manualScroll = false;
        _resume.Visibility = Visibility.Collapsed;
        _resumeScrollPending = false;

        _isShareSelecting = true;
        HideCaptionBar(animate: false);
        _shareSelectionLyrics = _lyrics;
        _hoveredShareLineIndex = null;
        _selectedShareLines.Clear();
        if (initialLineIndex is { } preselected)
            _selectedShareLines.Add(preselected);
        HideShareLimitHint();

        foreach (var row in _rows.Values)
            row.SetShareMode(true);

        ResizeLyrics();
        UpdateShareSelectionChrome();
        UpdateShareButton();
        foreach (var (idx, row) in _rows)
            SetRowVisual(row, idx, animate: true);

        var focusLine = initialLineIndex ?? (!keepManualOffset && _activeIndex >= 0 && _rows.ContainsKey(_activeIndex) ? _activeIndex : (int?)null);
        if (focusLine is { } targetLineIndex)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!_isShareSelecting || !_rows.TryGetValue(targetLineIndex, out var targetRow)) return;
                if (_scroll.ViewportHeight <= 0) return;

                var y = targetRow.Main.TranslatePoint(new Point(0, 0), _scroll).Y;
                var h = targetRow.ActualHeight > 0 ? targetRow.ActualHeight : targetRow.Main.ActualHeight;
                const double topPad = 86;
                const double bottomPad = 92;
                if (y < topPad)
                {
                    var next = Math.Max(0, _scroll.VerticalOffset + y - topPad);
                    _scroll.ScrollToVerticalOffset(next);
                }
                else if (y + h > _scroll.ViewportHeight - bottomPad)
                {
                    var next = Math.Clamp(_scroll.VerticalOffset + (y + h - (_scroll.ViewportHeight - bottomPad)), 0, _scroll.ScrollableHeight);
                    _scroll.ScrollToVerticalOffset(next);
                }
            });
        }
    }

    private void ToggleShareRowSelection(int lineIndex)
    {
        if (!_isShareSelecting || !_rows.TryGetValue(lineIndex, out var row)) return;

        if (_selectedShareLines.Contains(lineIndex))
        {
            _selectedShareLines.Remove(lineIndex);
            HideShareLimitHint();
        }
        else
        {
            if (_selectedShareLines.Count >= MaxShareSelectionCount)
            {
                ShowShareLimitHint();
                return;
            }
            _selectedShareLines.Add(lineIndex);
            if (_selectedShareLines.Count < MaxShareSelectionCount)
                HideShareLimitHint();
        }

        UpdateShareSelectionChrome();
        SetRowVisual(row, lineIndex, animate: true);
    }

    private void ExitShareSelectionMode(bool resumeAutoScroll)
    {
        if (!_isShareSelecting && _selectedShareLines.Count == 0) return;

        _isShareSelecting = false;
        _shareSelectionLyrics = null;
        _hoveredShareLineIndex = null;
        _selectedShareLines.Clear();
        HideShareLimitHint();

        _shareHeaderBar.Visibility = Visibility.Collapsed;
        _shareActionBar.Visibility = Visibility.Collapsed;

        foreach (var row in _rows.Values)
            row.SetShareMode(false);

        ResizeLyrics();
        UpdateShareButton();
        foreach (var (idx, row) in _rows)
            SetRowVisual(row, idx, animate: resumeAutoScroll);

        if (resumeAutoScroll)
        {
            _manualScroll = false;
            _resume.Visibility = Visibility.Collapsed;
            _resumeScrollPending = false;
            FollowActiveLine(animate: true);
            Refresh();
        }
    }

    private void UpdateShareSelectionChrome()
    {
        _shareHeaderBar.Visibility = _isShareSelecting ? Visibility.Visible : Visibility.Collapsed;
        _shareActionBar.Visibility = _isShareSelecting ? Visibility.Visible : Visibility.Collapsed;
        var count = _selectedShareLines.Count;
        _shareCountText.Text = $"已选择 {count} / {MaxShareSelectionCount} 行歌词";
        _shareContinueBtn.IsEnabled = count > 0;
        if (_isShareSelecting)
        {
            _shareContinueBtn.Background = count > 0
                ? Brushes.White
                : new SolidColorBrush(BlendColor(Colors.White, _subdued, 0.25));
            _shareHeaderBar.Background = new SolidColorBrush(Color.FromArgb(
                232,
                (byte)Math.Clamp((int)Math.Round(_background.R * 0.55), 14, 72),
                (byte)Math.Clamp((int)Math.Round(_background.G * 0.55), 14, 72),
                (byte)Math.Clamp((int)Math.Round(_background.B * 0.55), 14, 72)));
        }
    }

    private void ShowShareLimitHint()
    {
        _shareLimitHintText.Text = $"最多可选择 {MaxShareSelectionCount} 行歌词";
        _shareLimitHintText.Visibility = Visibility.Visible;
        _shareLimitHintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _shareLimitHintTimer.Stop();
        _shareLimitHintTimer.Tick -= OnShareLimitHintTick;
        _shareLimitHintTimer.Tick += OnShareLimitHintTick;
        _shareLimitHintTimer.Start();
    }

    private void OnShareLimitHintTick(object? sender, EventArgs e) => HideShareLimitHint();

    private void HideShareLimitHint()
    {
        _shareLimitHintTimer?.Stop();
        _shareLimitHintText.Visibility = Visibility.Collapsed;
    }

    private LyricSharePayload? BuildSharePayloadSnapshot()
    {
        if (!_isShareSelecting || _lyrics is null || _selectedShareLines.Count == 0)
            return null;

        var selectedLines = _selectedShareLines
            .Where(idx => idx >= 0 && idx < _lyrics.Lines.Count && _rows.ContainsKey(idx))
            .OrderBy(idx => idx)
            .Select(idx =>
            {
                var line = _lyrics.Lines[idx];
                var text = string.IsNullOrWhiteSpace(line.Content) ? "♪" : line.Content.Trim();
                return new ShareLyricLine(text, ExtractTranslation(line));
            })
            .ToList();

        if (selectedLines.Count == 0) return null;

        BitmapSource? coverSnapshot = null;
        if (_coverBytes is { Length: > 0 } bytes)
        {
            try { coverSnapshot = WindowsShareService.DecodeCoverBytes(bytes, maxDimension: 1600); }
            catch { coverSnapshot = null; }
        }
        coverSnapshot ??= _cover.Source as BitmapSource;
        var brightCoverColor = ClassicLyricsPalette.ShareCardColorFromCover(coverSnapshot, _coverBackground);

        return new LyricSharePayload(
            _track?.Title,
            _track?.Artist,
            coverSnapshot,
            brightCoverColor,
            selectedLines,
            _settings.ClassicShowTranslation,
            TextElement.GetFontFamily(_lines),
            _settings.ClassicLyricsFont);
    }

    private void OpenLyricSharePreview()
    {
        var payload = BuildSharePayloadSnapshot();
        if (payload is null) return;

        if (_shareWindow is { IsLoaded: true })
        {
            try { _shareWindow.Close(); } catch { }
        }

        // 进入二级分享卡片编辑窗时，主歌词界面立即恢复正常播放跟随状态
        ExitShareSelectionMode(resumeAutoScroll: true);

        _shareWindow = new LyricShareWindow(
            payload,
            _settings,
            onClosed: _ =>
            {
                _shareWindow = null;
            })
        {
            Owner = this,
        };
        _shareWindow.Show();
    }
}
