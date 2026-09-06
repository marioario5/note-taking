using System.Windows;
using System.Windows.Controls;

namespace NoteTaker.App.Views;

/// <summary>Single-field prompt, used for names and small numeric inputs.</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _input;

    private PromptWindow(string title, string label, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        if (TryFindResource("ElleryWindowStyle") is Style windowStyle)
        {
            Style = windowStyle;
        }

        _input = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 12) };
        _input.SelectAll();

        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };

        if (TryFindResource("PrimaryButton") is Style primary)
        {
            ok.Style = primary;
            ok.Width = double.NaN;
            ok.MinWidth = 80;
        }

        ok.Click += (_, _) =>
        {
            DialogResult = !string.IsNullOrWhiteSpace(_input.Text);
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(_input);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _input.Focus();
    }

    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var window = new PromptWindow(title, label, initial) { Owner = owner };
        return window.ShowDialog() == true ? window._input.Text.Trim() : null;
    }
}
