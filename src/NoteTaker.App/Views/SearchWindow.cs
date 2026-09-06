using System.Windows;
using System.Windows.Controls;
using NoteTaker.Core.Models;

namespace NoteTaker.App.Views;

/// <summary>
/// Two honest search modes: keyword over recognized ink and titles, and visual
/// similarity over whole pages. Handwritten formulas are not keyword-searchable.
/// </summary>
public sealed class SearchWindow : Window
{
    private readonly AppHost _host;
    private readonly long? _currentPageId;
    private readonly TextBox _query;
    private readonly ListBox _results;
    private readonly TextBlock _hint;

    public SearchWindow(AppHost host, long? currentPageId, bool showRelated)
    {
        _host = host;
        _currentPageId = currentPageId;

        Title = "Search notes";
        Width = 560;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (TryFindResource("ElleryWindowStyle") is Style windowStyle)
        {
            Style = windowStyle;
        }

        _query = new TextBox { Margin = new Thickness(0, 0, 8, 0), MinWidth = 300 };
        var searchButton = new Button { Content = "Search", Width = 90, IsDefault = true };
        var relatedButton = new Button { Content = "Related to this page", Width = 150, Margin = new Thickness(8, 0, 0, 0) };

        searchButton.Click += async (_, _) => await RunKeywordAsync();
        relatedButton.Click += async (_, _) => await RunRelatedAsync();

        var top = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(searchButton, Dock.Right);
        DockPanel.SetDock(relatedButton, Dock.Right);
        top.Children.Add(relatedButton);
        top.Children.Add(searchButton);
        top.Children.Add(_query);

        _results = new ListBox { Margin = new Thickness(12, 0, 12, 0) };
        _results.MouseDoubleClick += (_, _) => Accept();

        _results.ItemTemplate = BuildTemplate();

        _hint = new TextBlock
        {
            Margin = new Thickness(12, 8, 12, 0),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Text = "Keyword search covers page titles and any handwriting Windows could read. "
                 + "Use Related for pages that look like this one.",
        };

        var open = new Button { Content = "Open", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Close", Width = 90, IsCancel = true };

        if (TryFindResource("PrimaryButton") is Style primary)
        {
            open.Style = primary;
            open.Width = double.NaN;
            open.MinWidth = 90;
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(open);
        buttons.Children.Add(cancel);

        var footer = new Border { Child = buttons };
        if (TryFindResource("DialogFooter") is Style footerStyle)
        {
            footer.Style = footerStyle;
        }

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_hint, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_hint);
        root.Children.Add(footer);
        root.Children.Add(_results);

        Content = root;

        if (showRelated)
        {
            Loaded += async (_, _) => await RunRelatedAsync();
        }
    }

    public long? SelectedPageId { get; private set; }

    private static DataTemplate BuildTemplate()
    {
        var xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Margin="2,4">
                <TextBlock Text="{Binding PageTitle}" FontWeight="SemiBold" />
                <TextBlock Opacity="0.65" FontSize="11">
                  <Run Text="{Binding NotebookName, Mode=OneWay}" />
                  <Run Text="/" />
                  <Run Text="{Binding SectionName, Mode=OneWay}" />
                  <Run Text=" · " />
                  <Run Text="{Binding Kind, Mode=OneWay}" />
                </TextBlock>
                <TextBlock Text="{Binding Snippet}" Opacity="0.75" FontSize="11" TextWrapping="Wrap" />
              </StackPanel>
            </DataTemplate>
            """;

        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private async Task RunKeywordAsync()
    {
        var results = await _host.Search.SearchKeywordAsync(_query.Text);
        Show(results, results.Count == 0 ? "No matches. Try the page title, or use Related." : null);
    }

    private async Task RunRelatedAsync()
    {
        if (_currentPageId is null)
        {
            _hint.Text = "Open a page first to find related pages.";
            return;
        }

        var results = await _host.Search.FindSimilarPagesAsync(_currentPageId.Value);
        Show(results, results.Count == 0 ? "No similar pages yet. Write more, or rebuild the index." : null);
    }

    private void Show(IReadOnlyList<SearchResult> results, string? emptyHint)
    {
        _results.ItemsSource = results;
        if (emptyHint is not null)
        {
            _hint.Text = emptyHint;
        }
    }

    private void Accept()
    {
        if (_results.SelectedItem is SearchResult result)
        {
            SelectedPageId = result.PageId;
            DialogResult = true;
        }
    }
}
