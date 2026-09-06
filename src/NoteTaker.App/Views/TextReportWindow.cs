using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NoteTaker.App.Views;

/// <summary>Read-only report surface for session summaries, usage and generated practice.</summary>
public sealed class TextReportWindow : Window
{
    public TextReportWindow(string title, string body)
    {
        Title = title;
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (TryFindResource("ElleryWindowStyle") is Style windowStyle)
        {
            Style = windowStyle;
        }

        var text = new TextBox
        {
            Text = body,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            FontSize = 13,
            Padding = new Thickness(12),
        };

        var copy = new Button { Content = "Copy", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Clipboard.SetText(body);

        var close = new Button { Content = "Close", Width = 90, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var footer = new Border { Child = buttons };
        if (TryFindResource("DialogFooter") is Style footerStyle)
        {
            footer.Style = footerStyle;
        }

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(text);

        Content = root;
    }
}
