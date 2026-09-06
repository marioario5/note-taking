using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NoteTaker.App.Views;

/// <summary>
/// Replaces the system MessageBox so the night chrome is never interrupted by a bright
/// default dialog. One idea per string, matching the Ellery voice.
/// </summary>
public sealed class NoticeWindow : Window
{
    private NoticeWindow(string title, string? detail, bool confirm, string? confirmLabel)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        if (TryFindResource("ElleryWindowStyle") is Style style)
        {
            Style = style;
        }

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            // Assigned only when the resource resolves. FontFamily rejects null, so
            // `TryFindResource(...) as FontFamily` throws whenever the key is missing — and
            // "FontDisplay" has never existed, which meant EVERY notice threw instead of
            // showing. It surfaced as "Save ink trace now" appearing to do nothing.
            FontFamily = TryFindResource("FontUi") as FontFamily ?? new TextBlock().FontFamily,
            FontSize = 20,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, detail is null ? 0 : 10),
        });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78,
                LineHeight = 22,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        var footer = new Border
        {
            Margin = new Thickness(-28, 22, -28, 0),
            Padding = new Thickness(28, 14, 28, 16),
            Background = TryFindResource("SurfaceSunk") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x0C, 0x11, 0x1A)),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (confirm)
        {
            var neverMind = new Button
            {
                Content = "Never mind",
                IsCancel = true,
                Margin = new Thickness(0, 0, 10, 0),
                MinWidth = 110,
            };
            neverMind.Click += (_, _) => DialogResult = false;

            var yes = new Button
            {
                Content = string.IsNullOrWhiteSpace(confirmLabel) ? "Do it" : confirmLabel,
                IsDefault = true,
                MinWidth = 90,
            };

            if (TryFindResource("PrimaryButton") is Style primary)
            {
                yes.Style = primary;
            }

            yes.Click += (_, _) => DialogResult = true;

            buttons.Children.Add(neverMind);
            buttons.Children.Add(yes);
        }
        else
        {
            var ok = new Button
            {
                Content = "OK",
                IsDefault = true,
                IsCancel = true,
                MinWidth = 90,
            };

            if (TryFindResource("PrimaryButton") is Style primary)
            {
                ok.Style = primary;
            }

            ok.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(ok);
        }

        footer.Child = buttons;
        panel.Children.Add(footer);
        Content = panel;
    }

    public static void Tell(Window? owner, string title, string? detail = null)
    {
        var window = new NoticeWindow(title, detail, confirm: false, confirmLabel: null)
        {
            Owner = owner,
        };
        window.ShowDialog();
    }

    public static bool Confirm(
        Window? owner,
        string title,
        string? detail = null,
        string? confirmLabel = null)
    {
        var window = new NoticeWindow(title, detail, confirm: true, confirmLabel)
        {
            Owner = owner,
        };
        return window.ShowDialog() == true;
    }
}
