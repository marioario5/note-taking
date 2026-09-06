using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NoteTaker.App.Services;
using NoteTaker.Core.Expressions;

namespace NoteTaker.App.Views;

/// <summary>Plots y = f(x) and stamps the result into the page.</summary>
public sealed class GraphWindow : Window
{
    private const int RenderWidth = 1000;
    private const int RenderHeight = 640;

    /// <summary>
    /// One line per function, so several can be plotted together and compared — which is
    /// most of the reason to graph anything by hand in a maths notebook.
    /// </summary>
    private readonly TextBox _expression = new()
    {
        Text = "sin(x)/x",
        AcceptsReturn = true,
        MinLines = 3,
        MaxLines = 6,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private readonly TextBox _xMin = new() { Text = "-10" };
    private readonly TextBox _xMax = new() { Text = "10" };

    /// <summary>Blank means "fit to the data", which is the right default most of the time.</summary>
    private readonly TextBox _yMin = new() { Text = string.Empty };
    private readonly TextBox _yMax = new() { Text = string.Empty };
    private readonly ComboBox _placement = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    public GraphWindow()
    {
        Title = "Insert graph";
        Width = 760;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (TryFindResource("ElleryWindowStyle") is Style windowStyle)
        {
            Style = windowStyle;
        }

        foreach (var value in Enum.GetValues<InsertPlacement>())
        {
            _placement.Items.Add(value);
        }

        _placement.SelectedIndex = 1;

        var plot = new Button { Content = "Plot", Width = 90, IsDefault = true };
        plot.Click += (_, _) => Render();

        var insert = new Button { Content = "Insert", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        insert.Click += (_, _) =>
        {
            Render();
            if (Result is not null)
            {
                Placement = (InsertPlacement)_placement.SelectedItem;
                DialogResult = true;
            }
        };

        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };

        if (TryFindResource("PrimaryButton") is Style primary)
        {
            insert.Style = primary;
            insert.Width = double.NaN;
            insert.MinWidth = 90;
        }

        var inputs = new StackPanel { Margin = new Thickness(12) };
        inputs.Children.Add(new TextBlock
        {
            Text = "y = f(x) — one per line, e.g. sin(x)/x, x^2-3*x, exp(-x^2)",
        });
        inputs.Children.Add(_expression);

        foreach (var box in new[] { _xMin, _xMax, _yMin, _yMax })
        {
            box.Width = 70;
        }

        var range = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        range.Children.Add(Label("x from"));
        range.Children.Add(_xMin);
        range.Children.Add(Label("to", 6));
        range.Children.Add(_xMax);
        range.Children.Add(Label("y from", 16));
        range.Children.Add(_yMin);
        range.Children.Add(Label("to", 6));
        range.Children.Add(_yMax);
        range.Children.Add(Label("(blank = fit)", 8));
        inputs.Children.Add(range);

        var second = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        second.Children.Add(Label("Place"));
        _placement.Width = 100;
        second.Children.Add(_placement);
        second.Children.Add(plot);
        inputs.Children.Add(second);
        inputs.Children.Add(_error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(insert);
        buttons.Children.Add(cancel);

        var footer = new Border { Child = buttons };
        if (TryFindResource("DialogFooter") is Style footerStyle)
        {
            footer.Style = footerStyle;
        }

        var root = new DockPanel();
        DockPanel.SetDock(inputs, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(inputs);
        root.Children.Add(footer);
        root.Children.Add(_preview);

        Content = root;
        Loaded += (_, _) => Render();
    }

    public ImageSource? Result { get; private set; }

    public InsertPlacement Placement { get; private set; } = InsertPlacement.Middle;

    private void Render()
    {
        _error.Text = string.Empty;

        try
        {
            var sources = _expression.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (sources.Count == 0)
            {
                throw new FormatException("Write at least one function.");
            }

            var functions = sources.Select(ExpressionEvaluator.Compile).ToList();
            var min = double.Parse(_xMin.Text, CultureInfo.InvariantCulture);
            var max = double.Parse(_xMax.Text, CultureInfo.InvariantCulture);

            if (max <= min)
            {
                throw new FormatException("The upper bound must be greater than the lower bound.");
            }

            var lowerY = ParseOptional(_yMin.Text);
            var upperY = ParseOptional(_yMax.Text);
            if (lowerY is { } l && upperY is { } u && u <= l)
            {
                throw new FormatException("The y upper bound must be greater than the y lower bound.");
            }

            Result = Plot(functions, sources, min, max, lowerY, upperY);
            _preview.Source = Result;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            Result = null;
            _preview.Source = null;
            _error.Text = ex.Message;
        }
    }

    private static double? ParseOptional(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static TextBlock Label(string text, double left = 0) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 6, 0),
    };

    private static ImageSource Plot(
        IReadOnlyList<Func<double, double>> functions,
        IReadOnlyList<string> names,
        double xMin,
        double xMax,
        double? fixedYMin,
        double? fixedYMax)
    {
        const int samples = 1400;
        var curves = new List<List<Point?>>();
        var yMin = double.MaxValue;
        var yMax = double.MinValue;

        foreach (var function in functions)
        {
            var points = new List<Point?>(samples);
            for (var i = 0; i < samples; i++)
            {
                var x = xMin + ((xMax - xMin) * i / (samples - 1));
                double y;

                try
                {
                    y = function(x);
                }
                catch (Exception)
                {
                    points.Add(null);
                    continue;
                }

                if (double.IsNaN(y) || double.IsInfinity(y))
                {
                    points.Add(null);
                    continue;
                }

                points.Add(new Point(x, y));
                yMin = Math.Min(yMin, y);
                yMax = Math.Max(yMax, y);
            }

            curves.Add(points);
        }

        if (yMin > yMax)
        {
            yMin = -1;
            yMax = 1;
        }

        // Clamp pathological ranges (asymptotes) so the interesting part stays visible.
        var span = yMax - yMin;
        if (span > 1e6)
        {
            yMin = Math.Max(yMin, -100);
            yMax = Math.Min(yMax, 100);
            span = yMax - yMin;
        }

        if (span <= double.Epsilon)
        {
            yMin -= 1;
            yMax += 1;
            span = 2;
        }

        var padding = span * 0.08;
        yMin -= padding;
        yMax += padding;

        // An explicit bound wins over the fitted one; either can be given on its own.
        yMin = fixedYMin ?? yMin;
        yMax = fixedYMax ?? yMax;

        const double margin = 56;
        var plotWidth = RenderWidth - (margin * 2);
        var plotHeight = RenderHeight - (margin * 2);

        double MapX(double x) => margin + ((x - xMin) / (xMax - xMin) * plotWidth);
        double MapY(double y) => margin + (plotHeight - ((y - yMin) / (yMax - yMin) * plotHeight));

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // Dark to match the paper it gets stamped onto — a white card punched a hole in
            // the page every time a graph was inserted.
            context.DrawRectangle(PaperFill, null, new Rect(0, 0, RenderWidth, RenderHeight));

            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0xA9, 0xB7, 0xDE)), 1);
            var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(0x88, 0xA9, 0xB7, 0xDE)), 1.4);

            for (var i = 0; i <= 10; i++)
            {
                var gx = margin + (plotWidth * i / 10);
                var gy = margin + (plotHeight * i / 10);
                context.DrawLine(gridPen, new Point(gx, margin), new Point(gx, margin + plotHeight));
                context.DrawLine(gridPen, new Point(margin, gy), new Point(margin + plotWidth, gy));
            }

            if (yMin < 0 && yMax > 0)
            {
                var zero = MapY(0);
                context.DrawLine(axisPen, new Point(margin, zero), new Point(margin + plotWidth, zero));
            }

            if (xMin < 0 && xMax > 0)
            {
                var zero = MapX(0);
                context.DrawLine(axisPen, new Point(zero, margin), new Point(zero, margin + plotHeight));
            }

            context.PushClip(new RectangleGeometry(new Rect(margin, margin, plotWidth, plotHeight)));

            for (var c = 0; c < curves.Count; c++)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    var started = false;
                    foreach (var point in curves[c])
                    {
                        if (point is null)
                        {
                            started = false;
                            continue;
                        }

                        var mapped = new Point(MapX(point.Value.X), MapY(point.Value.Y));
                        if (mapped.Y < margin - 200 || mapped.Y > margin + plotHeight + 200)
                        {
                            started = false;
                            continue;
                        }

                        if (!started)
                        {
                            ctx.BeginFigure(mapped, false, false);
                            started = true;
                        }
                        else
                        {
                            ctx.LineTo(mapped, true, false);
                        }
                    }
                }

                geometry.Freeze();
                context.DrawGeometry(null, new Pen(CurveBrush(c), 2.2), geometry);
            }

            context.Pop();

            // Legend, but only when there is something to tell apart.
            if (curves.Count > 1)
            {
                for (var c = 0; c < curves.Count && c < names.Count; c++)
                {
                    var y = margin + 6 + (c * 22);
                    context.DrawRectangle(CurveBrush(c), null, new Rect(margin + 12, y, 18, 3));
                    DrawLabel(context, names[c], margin + 38, y - 9, CurveBrush(c));
                }
            }

            DrawLabel(context, xMin.ToString("0.##", CultureInfo.InvariantCulture), margin, margin + plotHeight + 6);
            DrawLabel(context, xMax.ToString("0.##", CultureInfo.InvariantCulture), margin + plotWidth - 24, margin + plotHeight + 6);
            DrawLabel(context, yMax.ToString("0.##", CultureInfo.InvariantCulture), 8, margin - 8);
            DrawLabel(context, yMin.ToString("0.##", CultureInfo.InvariantCulture), 8, margin + plotHeight - 8);
        }

        var bitmap = new RenderTargetBitmap(RenderWidth, RenderHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Page-dark, so a stamped graph sits on the paper instead of punching a hole in it.</summary>
    private static readonly Brush PaperFill = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x17, 0x22)));

    private static readonly Brush AxisText = Freeze(new SolidColorBrush(Color.FromRgb(0xA9, 0xB7, 0xDE)));

    /// <summary>
    /// Distinguishable at a glance and on the app's dark paper: peri, amber, sage, rose,
    /// then repeating. Ordered so the first two — the common "compare these" case — are the
    /// furthest apart.
    /// </summary>
    private static readonly Brush[] CurveBrushes =
    [
        Freeze(new SolidColorBrush(Color.FromRgb(0xA9, 0xB7, 0xDE))),
        Freeze(new SolidColorBrush(Color.FromRgb(0xFE, 0xBB, 0x55))),
        Freeze(new SolidColorBrush(Color.FromRgb(0x6F, 0x9B, 0x7D))),
        Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x8F, 0x74))),
        Freeze(new SolidColorBrush(Color.FromRgb(0xC6, 0xCF, 0xEB))),
    ];

    private static Brush CurveBrush(int index) => CurveBrushes[index % CurveBrushes.Length];

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static void DrawLabel(DrawingContext context, string text, double x, double y, Brush? brush = null)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            brush ?? AxisText,
            1.0);

        context.DrawText(formatted, new Point(x, y));
    }
}
