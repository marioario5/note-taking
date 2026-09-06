using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NoteTaker.App.Views;

/// <summary>
/// Phase 1 gate instrument. It measures the software half of pen latency: how long a
/// stylus sample waits before the next composed frame, plus the pen's sample rate.
/// True pen-to-photon latency also includes display and digitizer time, which no
/// software can observe, so the side-by-side feel test against OneNote still matters.
/// </summary>
public sealed class BenchmarkWindow : Window
{
    private readonly InkCanvas _canvas;
    private readonly TextBlock _results;
    private readonly List<double> _inputToFrame = [];
    private readonly List<double> _sampleIntervals = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<double> _pendingSamples = new();

    private double _lastSampleAt = -1;
    private int _frames;
    private double _firstFrameAt = -1;

    public BenchmarkWindow()
    {
        Title = "Pen latency benchmark";
        Width = 900;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Plain Window default (light gray) never gets the dark Ellery chrome, but the
        // app-wide implicit Button style still applies its light TextMuted foreground to
        // every button — including these — regardless. On the unstyled default background
        // that leaves near-white text on a near-white window: the buttons were rendering,
        // just invisible. Same fix NoticeWindow.cs already uses.
        if (TryFindResource("ElleryWindowStyle") is Style windowStyle)
        {
            Style = windowStyle;
        }

        _canvas = new InkCanvas
        {
            Background = Brushes.White,
            DefaultDrawingAttributes = new System.Windows.Ink.DrawingAttributes
            {
                Color = Colors.Black,
                Width = 3,
                Height = 3,
                FitToCurve = true,
            },
        };

        _canvas.StylusMove += (_, _) => RecordSample();
        _canvas.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                RecordSample();
            }
        };

        _results = new TextBlock
        {
            Margin = new Thickness(12),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Text = "Scribble continuously for a few seconds, then press Stop.",
        };

        var stop = new Button { Content = "Stop and report", Width = 130, Margin = new Thickness(0, 0, 8, 0) };
        if (TryFindResource("PrimaryButton") is Style primary)
        {
            stop.Style = primary;
            stop.Width = double.NaN;
            stop.MinWidth = 130;
        }

        stop.Click += (_, _) => Report();

        var reset = new Button { Content = "Reset", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            _inputToFrame.Clear();
            _sampleIntervals.Clear();
            _pendingSamples.Clear();
            _canvas.Strokes.Clear();
            _frames = 0;
            _firstFrameAt = -1;
            _lastSampleAt = -1;
            _results.Text = "Cleared. Scribble again.";
        };

        var close = new Button { Content = "Close", Width = 90, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(stop);
        buttons.Children.Add(reset);
        buttons.Children.Add(close);

        var footer = new Border { Child = buttons };
        if (TryFindResource("DialogFooter") is Style footerStyle)
        {
            footer.Style = footerStyle;
        }
        else
        {
            footer.Padding = new Thickness(12);
        }

        var root = new DockPanel();
        DockPanel.SetDock(_results, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(_results);
        root.Children.Add(footer);
        root.Children.Add(_canvas);

        Content = root;

        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    private void RecordSample()
    {
        var now = _clock.Elapsed.TotalMilliseconds;

        if (_lastSampleAt >= 0)
        {
            _sampleIntervals.Add(now - _lastSampleAt);
        }

        _lastSampleAt = now;
        _pendingSamples.Enqueue(now);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalMilliseconds;

        _frames++;
        if (_firstFrameAt < 0)
        {
            _firstFrameAt = now;
        }

        while (_pendingSamples.Count > 0)
        {
            _inputToFrame.Add(now - _pendingSamples.Dequeue());
        }
    }

    private void Report()
    {
        if (_inputToFrame.Count < 20)
        {
            _results.Text = "Not enough samples yet. Scribble continuously for a few seconds first.";
            return;
        }

        var latency = _inputToFrame.OrderBy(v => v).ToList();
        var intervals = _sampleIntervals.Where(v => v is > 0 and < 100).OrderBy(v => v).ToList();

        var median = Percentile(latency, 0.50);
        var p95 = Percentile(latency, 0.95);
        var sampleMedian = intervals.Count > 0 ? Percentile(intervals, 0.50) : 0;
        var rate = sampleMedian > 0 ? 1000.0 / sampleMedian : 0;
        var elapsedSeconds = (_clock.Elapsed.TotalMilliseconds - _firstFrameAt) / 1000.0;
        var fps = elapsedSeconds > 0 ? _frames / elapsedSeconds : 0;

        var verdict = median <= 12
            ? "PASS — input reaches the compositor within one frame."
            : median <= 20
                ? "BORDERLINE — acceptable, but compare side by side with OneNote."
                : "FAIL — investigate before building on this stack.";

        _results.Text =
            $"""
             Samples: {latency.Count}   Strokes: {_canvas.Strokes.Count}

             Input to next frame   median {median:0.0} ms   p95 {p95:0.0} ms
             Pen sample rate       {rate:0} Hz  (interval {sampleMedian:0.0} ms)
             Render rate           {fps:0} fps

             {verdict}

             Note: this measures the software pipeline only. Display and digitizer
             latency are not observable from code, so also compare the feel directly
             against OneNote on this device before signing off Phase 1.
             """;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Clamp(Math.Round(percentile * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }
}
