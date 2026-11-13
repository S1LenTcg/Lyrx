using System.Text;
using System.Windows;
using System.Windows.Controls;
using Musebase.Windows.Services;
using Musebase.Core;
using Musebase.Core.Search;
using Musebase.Engine;

namespace Musebase.Windows;

/// <summary>
/// 수동 가사 검색 창. 자동 매칭이 틀렸을 때 직접 검색해 교체한다.
/// 열면 현재 곡을 바로 검색하고, 목록에서 항목을 고르면 우측에 가사를 미리 보여준다.
/// 적용 시 coordinator.UseLyricsAsync로 오버레이·캐시에 반영.
/// </summary>
public sealed class SearchWindow : Window
{
    private readonly LyricsCoordinator _coordinator;
    private readonly TextBox _titleBox;
    private readonly TextBox _artistBox;
    private readonly ListView _resultList;
    private readonly TextBox _preview;
    private readonly Button _searchButton;
    private readonly Button _applyButton;
    private readonly TextBlock _statusText;
    private CancellationTokenSource? _cts;

    private sealed record ResultRow(Lyrics Lyrics, string Service, string Title, string Artist,
        int LineCount, string Translated, string Quality);

    public SearchWindow(LyricsCoordinator coordinator)
    {
        _coordinator = coordinator;

        Title = Loc.T("search.title");
        Width = 900;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _titleBox = new TextBox { Text = coordinator.CurrentTrack?.Title ?? "", MinWidth = 200, Margin = new Thickness(4, 0, 12, 0) };
        _artistBox = new TextBox { Text = coordinator.CurrentTrack?.Artist ?? "", MinWidth = 150, Margin = new Thickness(4, 0, 12, 0) };
        _searchButton = new Button { Content = Loc.T("search.button"), Width = 80, IsDefault = true };
        _searchButton.Click += async (_, _) => await RunSearchAsync();

        var inputPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
        inputPanel.Children.Add(new TextBlock { Text = Loc.T("search.label.title"), VerticalAlignment = VerticalAlignment.Center });
        inputPanel.Children.Add(_titleBox);
        inputPanel.Children.Add(new TextBlock { Text = Loc.T("search.label.artist"), VerticalAlignment = VerticalAlignment.Center });
        inputPanel.Children.Add(_artistBox);
        inputPanel.Children.Add(_searchButton);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = Loc.T("search.col.source"), Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding("Service") });
        grid.Columns.Add(new GridViewColumn { Header = Loc.T("search.col.title"), Width = 170, DisplayMemberBinding = new System.Windows.Data.Binding("Title") });
        grid.Columns.Add(new GridViewColumn { Header = Loc.T("search.col.artist"), Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("Artist") });
        grid.Columns.Add(new GridViewColumn { Header = Loc.T("search.col.lines"), Width = 45, DisplayMemberBinding = new System.Windows.Data.Binding("LineCount") });
        grid.Columns.Add(new GridViewColumn { Header = Loc.T("search.col.translated"), Width = 45, DisplayMemberBinding = new System.Windows.Data.Binding("Translated") });
        grid.Columns.Add(new GridViewColumn { Header = Loc.T("search.col.quality"), Width = 55, DisplayMemberBinding = new System.Windows.Data.Binding("Quality") });

        _resultList = new ListView { View = grid };
        _resultList.MouseDoubleClick += async (_, _) => await ApplySelectedAsync();
        _resultList.SelectionChanged += (_, _) =>
        {
            _applyButton!.IsEnabled = _resultList.SelectedItem is not null;
            UpdatePreview();
        };

        _preview = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _statusText = new TextBlock { Margin = new Thickness(12, 6, 12, 0), Opacity = 0.7 };

        _applyButton = new Button
        {
            Content = Loc.T("search.apply"),
            Width = 140,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
        };
        _applyButton.Click += async (_, _) => await ApplySelectedAsync();

        // 결과 목록 | 미리보기 (2:1.2, 가운데 스플리터)
        var centerGrid = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        Grid.SetColumn(_resultList, 0);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(_preview, 2);
        centerGrid.Children.Add(_resultList);
        centerGrid.Children.Add(splitter);
        centerGrid.Children.Add(_preview);

        var root = new DockPanel();
        DockPanel.SetDock(inputPanel, Dock.Top);
        DockPanel.SetDock(_statusText, Dock.Top);
        DockPanel.SetDock(_applyButton, Dock.Bottom);
        root.Children.Add(inputPanel);
        root.Children.Add(_statusText);
        root.Children.Add(_applyButton);
        root.Children.Add(centerGrid);
        Content = root;

        Closed += (_, _) => _cts?.Cancel();
        // 열면 현재 곡을 바로 검색
        Loaded += async (_, _) =>
        {
            if (_titleBox.Text.Trim().Length > 0) await RunSearchAsync();
        };
    }

    private async Task RunSearchAsync()
    {
        var title = _titleBox.Text.Trim();
        var artist = _artistBox.Text.Trim();
        if (title.Length == 0) return;

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        _searchButton.IsEnabled = false;
        _statusText.Text = Loc.T("search.status.searching");
        _resultList.ItemsSource = null;
        _preview.Text = "";

        try
        {
            var request = LyricsSearchRequest.ByInfo(
                title, artist, _coordinator.CurrentTrack?.Duration?.TotalSeconds ?? 0, limit: 6);
            var service = new LyricsSearchService();
            var results = await service.SearchAllAsync(request, cts.Token);

            _resultList.ItemsSource = results.Select(l => new ResultRow(
                l,
                l.Metadata.ServiceName ?? "?",
                l.IdTags.GetValueOrDefault(Lyrics.TagTitle) ?? "?",
                l.IdTags.GetValueOrDefault(Lyrics.TagArtist) ?? "?",
                l.Lines.Count,
                l.HasTranslation() ? "O" : "-",
                l.Quality().ToString("0.00"))).ToList();
            _statusText.Text = results.Count == 0
                ? Loc.T("search.status.none")
                : Loc.T("search.status.count", ("count", results.Count));

            if (_resultList.Items.Count > 0)
                _resultList.SelectedIndex = 0; // 최상위(최고 품질) 자동 선택 → 미리보기 표시
        }
        catch (OperationCanceledException)
        {
            // 창 닫힘/재검색
        }
        catch (Exception e)
        {
            _statusText.Text = Loc.T("search.status.fail", ("error", e.Message));
        }
        finally
        {
            _searchButton.IsEnabled = true;
        }
    }

    private void UpdatePreview()
    {
        if (_resultList.SelectedItem is not ResultRow row)
        {
            _preview.Text = "";
            return;
        }

        var lang = _coordinator.TargetLanguage?.ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var line in row.Lyrics.Lines)
        {
            sb.AppendLine(line.Content);
            var tr = string.IsNullOrEmpty(lang) ? line.Attachments.Translation() : line.Attachments.Translation(lang, null);
            if (!string.IsNullOrEmpty(tr)) sb.AppendLine("    " + tr);
        }
        _preview.Text = sb.ToString().TrimEnd();
    }

    private async Task ApplySelectedAsync()
    {
        if (_resultList.SelectedItem is not ResultRow row) return;
        await _coordinator.UseLyricsAsync(row.Lyrics);
        Close();
    }
}
