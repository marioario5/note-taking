using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NoteTaker.App.Services;

namespace NoteTaker.App.Views;

/// <summary>
/// Runs a short Python snippet in a local sidecar process and stamps the output into the
/// page. The process is killed on timeout so a runaway loop cannot hang the app.
/// </summary>
public sealed class PythonWindow : Window
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly AppSettings _settings;
    private readonly TextBox _code;
    private readonly TextBox _output;
    private readonly ComboBox _placement = new();
    private readonly Button _run;

    public PythonWindow(AppSettings settings)
    {
        _settings = settings;

        Title = "Python cell";
        Width = 760;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (TryFindResource("ElleryWindowStyle") is Style windowStyle)
        {
            Style = windowStyle;
        }

        _code = new TextBox
        {
            Text = "import math\nfor n in range(1, 6):\n    print(n, math.sqrt(n))",
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 220,
            Margin = new Thickness(12, 4, 12, 8),
        };

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(12, 4, 12, 8),
        };

        foreach (var value in Enum.GetValues<InsertPlacement>())
        {
            _placement.Items.Add(value);
        }

        _placement.SelectedIndex = 1;
        _placement.Width = 100;

        _run = new Button { Content = "Run", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        _run.Click += async (_, _) => await RunAsync();

        var insert = new Button { Content = "Insert output", Width = 110, Margin = new Thickness(0, 0, 8, 0) };
        insert.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_output.Text))
            {
                return;
            }

            Result = RenderOutput(_code.Text, _output.Text);
            Placement = (InsertPlacement)_placement.SelectedItem;
            DialogResult = true;
        };

        if (TryFindResource("PrimaryButton") is Style primary)
        {
            insert.Style = primary;
            insert.Width = double.NaN;
            insert.MinWidth = 110;
        }

        var cancel = new Button { Content = "Close", Width = 90, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(_placement);
        buttons.Children.Add(new TextBlock { Width = 8 });
        buttons.Children.Add(_run);
        buttons.Children.Add(insert);
        buttons.Children.Add(cancel);

        var footer = new Border { Child = buttons };
        if (TryFindResource("DialogFooter") is Style footerStyle)
        {
            footer.Style = footerStyle;
        }

        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Code", Margin = new Thickness(12, 8, 12, 0) });
        top.Children.Add(_code);
        top.Children.Add(new TextBlock { Text = "Output", Margin = new Thickness(12, 0, 12, 0) });

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(_output);

        Content = root;
    }

    public ImageSource? Result { get; private set; }

    public InsertPlacement Placement { get; private set; } = InsertPlacement.Middle;

    private async Task RunAsync()
    {
        _run.IsEnabled = false;
        _output.Text = "Running…";

        var scriptPath = Path.Combine(Path.GetTempPath(), $"notetaker_{Guid.NewGuid():N}.py");

        try
        {
            await File.WriteAllTextAsync(scriptPath, _code.Text);

            var startInfo = new ProcessStartInfo
            {
                FileName = _settings.PythonPath ?? "python",
                Arguments = $"\"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _output.Text = "Could not start Python. Check the path in Settings.";
                return;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                _output.Text = $"Timed out after {Timeout.TotalSeconds:0} seconds.";
                return;
            }

            var text = new StringBuilder(await stdout);
            var errors = await stderr;
            if (!string.IsNullOrWhiteSpace(errors))
            {
                text.AppendLine().Append(errors);
            }

            _output.Text = text.Length == 0 ? "(no output)" : text.ToString();
        }
        catch (Exception ex)
        {
            _output.Text = $"Could not run Python.\n\n{ex.Message}";
        }
        finally
        {
            _run.IsEnabled = true;
            try
            {
                File.Delete(scriptPath);
            }
            catch (IOException)
            {
                // Temp file cleanup is best-effort.
            }
        }
    }

    private static ImageSource RenderOutput(string code, string output)
    {
        const int width = 1000;
        var typeface = new Typeface("Cascadia Mono, Consolas");

        var codeText = new FormattedText(
            code.Trim(),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            14,
            Brushes.Black,
            1.0)
        { MaxTextWidth = width - 48 };

        var outputText = new FormattedText(
            output.Trim(),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            14,
            new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20)),
            1.0)
        { MaxTextWidth = width - 48 };

        var height = (int)(codeText.Height + outputText.Height + 96);
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFC)),
                new Pen(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xE4)), 1),
                new Rect(0.5, 0.5, width - 1, height - 1));

            context.DrawText(codeText, new Point(24, 20));
            context.DrawLine(
                new Pen(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xE4)), 1),
                new Point(24, codeText.Height + 36),
                new Point(width - 24, codeText.Height + 36));
            context.DrawText(outputText, new Point(24, codeText.Height + 48));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
