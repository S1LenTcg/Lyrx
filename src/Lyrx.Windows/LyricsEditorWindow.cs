using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Musebase.Windows.Services;
using Musebase.Core;

namespace Musebase.Windows;

/// <summary>
/// 当前歌词编辑窗口。
/// - 完整视图：扩展 LRC（无损）。tt 与所有语言的译文都原样保留。
/// - 简易视图：只显示所选语言，格式为 [时间]原文【译文】（提供语言下拉框）。保存时合并回原文。
/// 保存时先做解析校验，再通过 onSaved 回调交出结果。
/// </summary>
public sealed class LyricsEditorWindow : Window
{
    private sealed record LangItem(string Tag, string Display);

    private readonly TextBox _editor;
    private readonly TextBlock _status;
    private readonly CheckBox _simpleToggle;
    private readonly ComboBox _langCombo;
    private readonly Action<Lyrics> _onSaved;

    private Lyrics _working;         // 编辑目标模型
    private string _selectedTag = LineAttachments.TagTranslationPrefix;
    private bool _suppress;          // 程序性改动时忽略事件

    public LyricsEditorWindow(string trackLabel, Lyrics lyrics, Action<Lyrics> onSaved)
    {
        _working = lyrics;
        _onSaved = onSaved;

        Title = Loc.T("editor.title", ("track", trackLabel));
        Width = 560;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _simpleToggle = new CheckBox { Content = Loc.T("editor.simpleToggle"), VerticalAlignment = VerticalAlignment.Center };
        _langCombo = new ComboBox
        {
            Width = 120,
            Margin = new Thickness(12, 0, 0, 0),
            DisplayMemberPath = nameof(LangItem.Display),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var langLabel = new TextBlock
        {
            Text = Loc.T("editor.lang.label"), Margin = new Thickness(12, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };

        var hint = new TextBlock
        {
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 6),
        };
        void UpdateHint() => hint.Text = _simpleToggle.IsChecked == true
            ? Loc.T("editor.hint.simple")
            : Loc.T("editor.hint.full");

        _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
        };

        _status = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

        var save = new Button { Content = Loc.T("common.save"), Width = 90, IsDefault = true, Margin = new Thickness(0, 8, 8, 0) };
        var cancel = new Button { Content = Loc.T("common.cancel"), Width = 90, IsCancel = true, Margin = new Thickness(0, 8, 0, 0) };

        // ---- 事件 ----
        _simpleToggle.Checked += (_, _) => SwitchView(simple: true, langLabel);
        _simpleToggle.Unchecked += (_, _) => SwitchView(simple: false, langLabel);
        _langCombo.SelectionChanged += (_, _) =>
        {
            if (_suppress || _langCombo.SelectedItem is not LangItem item) return;
            if (!CaptureEdits()) { RevertLangSelection(); return; }
            _selectedTag = item.Tag;
            RenderSimple();
        };
        save.Click += (_, _) =>
        {
            if (!CaptureEdits()) return;
            _onSaved(_working);
            Close();
        };
        cancel.Click += (_, _) => Close();
        _simpleToggle.Checked += (_, _) => UpdateHint();
        _simpleToggle.Unchecked += (_, _) => UpdateHint();

        // 初始：完整视图
        _editor.Text = _working.ToString();
        UpdateHint();

        // ---- 布局 ----
        var topBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        topBar.Children.Add(_simpleToggle);
        topBar.Children.Add(langLabel);
        topBar.Children.Add(_langCombo);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(topBar, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(topBar);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        root.Children.Add(_editor);
        Content = root;
    }

    /// <summary>把当前编辑器文本写回模型（_working）。失败时在状态栏提示并返回 false。</summary>
    private bool CaptureEdits()
    {
        _status.Text = "";
        if (_simpleToggle.IsChecked == true)
        {
            var merged = LyricsEditing.ApplySimpleEdit(_working, _editor.Text, _selectedTag);
            if (merged is null)
            {
                _status.Text = Loc.T("editor.invalid");
                return false;
            }
            _working = merged;
        }
        else
        {
            var parsed = Lyrics.Parse(_editor.Text);
            if (parsed is null || parsed.Lines.Count == 0)
            {
                _status.Text = Loc.T("editor.invalid");
                return false;
            }
            _working = parsed;
        }
        return true;
    }

    private void SwitchView(bool simple, TextBlock langLabel)
    {
        if (_suppress) return;

        // 切换前先把当前的编辑内容写回模型
        if (!CaptureEdits())
        {
            // 失败则把开关拨回去
            _suppress = true;
            _simpleToggle.IsChecked = !simple;
            _suppress = false;
            return;
        }

        langLabel.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
        _langCombo.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;

        if (simple)
        {
            PopulateLangCombo();
            RenderSimple();
        }
        else
        {
            _editor.Text = _working.ToString();
        }
    }

    private void PopulateLangCombo()
    {
        var tags = LyricsEditing.TranslationTags(_working);
        var items = tags.Select(t => new LangItem(t, TagToDisplay(t))).ToList();

        _suppress = true;
        _langCombo.ItemsSource = items;
        var selected = items.FirstOrDefault(i => i.Tag == _selectedTag) ?? items[0];
        _selectedTag = selected.Tag;
        _langCombo.SelectedItem = selected;
        _suppress = false;
    }

    private void RenderSimple() => _editor.Text = LyricsEditing.ToSimpleText(_working, _selectedTag);

    private void RevertLangSelection()
    {
        _suppress = true;
        _langCombo.SelectedItem = (_langCombo.ItemsSource as IEnumerable<LangItem>)?.FirstOrDefault(i => i.Tag == _selectedTag);
        _suppress = false;
    }

    private static string TagToDisplay(string tag) =>
        tag == LineAttachments.TagTranslationPrefix ? Loc.T("editor.lang.default")
        : tag.StartsWith("tr:", StringComparison.Ordinal) ? tag[3..].ToUpperInvariant()
        : tag;
}
