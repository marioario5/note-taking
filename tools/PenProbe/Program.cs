using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// Bare .NET 8 WPF InkCanvas with EnablePointerSupport on, and none of NoteTaker's code.
// Answers one question: on THIS runtime, does the pointer stack deliver stylus events here?
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        int stylusDown = 0, stylusMove = 0, stylusUp = 0, mouseDown = 0, mouseMove = 0, strokes = 0;

        var app = new Application();
        var ink = new InkCanvas { Background = System.Windows.Media.Brushes.White };

        ink.StylusDown += (_, _) => stylusDown++;
        ink.StylusMove += (_, _) => stylusMove++;
        ink.StylusUp += (_, _) => stylusUp++;
        ink.MouseDown += (_, _) => mouseDown++;
        ink.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) mouseMove++; };
        ink.StrokeCollected += (_, _) => strokes++;

        var window = new Window
        {
            Title = "Pen probe (.NET 8, pointer support ON) - draw, then close",
            Width = 900,
            Height = 650,
            Content = ink,
        };

        window.Closed += (_, _) =>
        {
            var report = new StringBuilder();

            report.AppendLine("EnablePointerSupport: " + (
                AppContext.TryGetSwitch("Switch.System.Windows.Input.Stylus.EnablePointerSupport", out var on)
                    ? on.ToString()
                    : "not set"));

            // Expected to be EMPTY under pointer support — that alone is not a fault.
            report.AppendLine($"tablet devices: {Tablet.TabletDevices.Count}");
            foreach (TabletDevice tablet in Tablet.TabletDevices)
            {
                report.AppendLine($"  {tablet.Name} type={tablet.Type} styluses={tablet.StylusDevices.Count}");
            }

            report.AppendLine();
            report.AppendLine($"StylusDown : {stylusDown}");
            report.AppendLine($"StylusMove : {stylusMove}");
            report.AppendLine($"StylusUp   : {stylusUp}");
            report.AppendLine($"MouseDown  : {mouseDown}");
            report.AppendLine($"MouseMove  : {mouseMove}");
            report.AppendLine($"Strokes    : {strokes}");

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NoteTaker",
                "pen-probe8.txt");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report.ToString());
        };

        app.Run(window);
    }
}
