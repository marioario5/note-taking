using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using NoteTaker.App.Services;
using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.App.Views;

/// <summary>
/// Generates a practice set from the pages in a section. One API call per generation,
/// so this stays a rounding error against the monthly budget.
/// </summary>
public sealed class PracticeWindow : Window
{
    private readonly AppHost _host;
    private readonly long _sectionId;

    private readonly TextBox _count = new() { Text = "5" };
    private readonly TextBox _instructions = new();
    private readonly TextBox _output;
    private readonly Button _generate;

    public PracticeWindow(AppHost host, TutorCoordinator tutor, long sectionId)
    {
        _host = host;
        _sectionId = sectionId;
        _ = tutor;

        Title = "Generate practice set";
        Width = 640;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (TryFindResource("ElleryWindowStyle") is Style windowStyle)
        {
            Style = windowStyle;
        }

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(16, 0, 16, 0),
        };

        _generate = new Button { Content = "Generate", Width = 110, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        _generate.Click += async (_, _) => await GenerateAsync();

        if (TryFindResource("PrimaryButton") is Style primary)
        {
            _generate.Style = primary;
            _generate.Width = double.NaN;
            _generate.MinWidth = 110;
        }

        var top = new StackPanel { Margin = new Thickness(16) };
        top.Children.Add(new TextBlock { Text = "How many questions?" });
        top.Children.Add(_count);
        top.Children.Add(new TextBlock { Text = "Extra instructions (optional)", Margin = new Thickness(0, 8, 0, 0) });
        top.Children.Add(_instructions);

        var copy = new Button { Content = "Copy", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Clipboard.SetText(_output.Text);
        var close = new Button { Content = "Close", Width = 90, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(_generate);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var footer = new Border { Child = buttons };
        if (TryFindResource("DialogFooter") is Style footerStyle)
        {
            footer.Style = footerStyle;
        }

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(_output);

        Content = root;
    }

    private async Task GenerateAsync()
    {
        if (_host.TutorApi is not { } client)
        {
            _output.Text = "The tutor is not configured yet. Add an API key in Settings.";
            return;
        }

        _generate.IsEnabled = false;
        _output.Text = "Reading your notes…";

        try
        {
            var pages = await _host.Pages.GetPagesAsync(_sectionId);
            var images = new List<byte[]>();

            // A handful of pages is plenty of style signal, and keeps image tokens down.
            foreach (var page in pages.Take(4))
            {
                var ink = await _host.Ink.LoadAsync(page.Id);
                if (ink is null || ink.IsfBlob.Length == 0)
                {
                    continue;
                }

                var strokes = new StrokeCollection(new MemoryStream(ink.IsfBlob));
                var background = page.PdfPath is { Length: > 0 } path && File.Exists(path)
                    ? PdfBackgroundService.Render(path, page.PdfPageIndex ?? 0)
                    : null;

                // Worksheet only. This path reads OTHER pages straight from the database, so
                // it cannot go through the ActiveBackground/ActiveChatBackground split that
                // protects the live page — say it explicitly instead. Practice questions are
                // generated from the student's own work; a screenshot they pasted to ask a
                // question about is not that, and does not belong in the request.
                images.Add(PageRenderer.RenderPagePng(
                    strokes, background, 900, ocrContrast: false, drawBackground: true));
            }

            if (images.Count == 0)
            {
                _output.Text = "This section has no ink yet, so there is nothing to base questions on.";
                return;
            }

            var count = int.TryParse(_count.Text, out var parsed) ? Math.Clamp(parsed, 1, 20) : 5;
            _output.Text = "Generating…";

            var result = await client.GeneratePracticeAsync(
                new PracticeGenerationRequest(
                    "this section",
                    images,
                    count,
                    string.IsNullOrWhiteSpace(_instructions.Text) ? null : _instructions.Text.Trim()));

            _output.Text = result.Content;

            await _host.Usage.LogAsync(new ApiUsageLog
            {
                CallType = TutorCallType.PracticeGeneration,
                Model = result.Model,
                TokensIn = result.Usage.TokensIn,
                TokensOut = result.Usage.TokensOut,
                CostEstimate = PricingTable.Estimate(result.Model, result.Usage),
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _output.Text = $"Could not generate a practice set.\n\n{ex.Message}";
        }
        finally
        {
            _generate.IsEnabled = true;
        }
    }
}
