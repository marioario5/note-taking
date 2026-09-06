using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NoteTaker.Core.Tutor;

namespace NoteTaker.App.Views;

/// <summary>
/// Turns a syllabus PDF into lessons: choose the file, see what was found, confirm.
/// </summary>
/// <remarks>
/// A file picker rather than a paste box. The syllabus already exists as a file, and asking the
/// student to open it, select the right span of it and paste it in was three chores standing in
/// front of the one thing they wanted.
///
/// It costs nothing. <see cref="SyllabusPdfReader"/> reads the text layer on this machine and
/// <see cref="SyllabusParser"/> picks the lessons out of it — no model call, no upload. The
/// preview list exists because that parse should be visible before it is committed: fifty
/// lessons appearing in a subject is a big change to make on trust, and a syllabus whose text
/// will not read is something the student can see and work around rather than a button that
/// silently does nothing.
/// </remarks>
public sealed class SyllabusWindow : Window
{
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _fileName = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _preview = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _import = new() { Content = "Add lessons", IsEnabled = false };

    /// <summary>What the syllabus parsed to, once the student accepted it.</summary>
    public IReadOnlyList<SyllabusEntry> Entries { get; private set; } = [];

    /// <summary>The course the syllabus names, so the subject can take its own name.</summary>
    public string? CourseTitle { get; private set; }

    public SyllabusWindow(string subjectName)
    {
        Title = "Import syllabus";
        Width = 560;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (TryFindResource("ElleryWindowStyle") is Style windowStyle)
        {
            Style = windowStyle;
        }

        var heading = new TextBlock
        {
            Text = $"Add lessons to {subjectName}",
            FontSize = 19,
            Margin = new Thickness(0, 0, 0, 6),
        };

        var instructions = new TextBlock
        {
            Text = "Choose the syllabus or course outline as a PDF. Every numbered lesson in it "
                 + "becomes a lesson here, and the unit is read from the number. It is read on "
                 + "this machine — nothing is uploaded.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };

        if (Application.Current.TryFindResource("Body") is Style body)
        {
            instructions.Style = body;
            _summary.Style = body;
            _fileName.Style = body;
        }

        var choose = new Button
        {
            Content = "Choose PDF…",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 8, 16, 8),
        };
        choose.Click += (_, _) => Load();

        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        _import.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_import);

        var top = new StackPanel();
        top.Children.Add(heading);
        top.Children.Add(instructions);
        top.Children.Add(choose);
        top.Children.Add(_fileName);
        top.Children.Add(_summary);

        var layout = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(buttons);
        layout.Children.Add(_preview);

        Content = layout;
    }

    private void Load()
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose your syllabus",
            Filter = "Syllabus (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        _fileName.Text = Path.GetFileName(picker.FileName);
        _fileName.Margin = new Thickness(0, 12, 0, 0);

        string text;
        try
        {
            text = SyllabusPdfReader.ReadText(picker.FileName);
        }
        catch (Exception ex)
        {
            Entries = [];
            _preview.ItemsSource = null;
            _import.IsEnabled = false;
            _summary.Text = $"That file could not be read: {ex.Message}";
            return;
        }

        CourseTitle = SyllabusParser.CourseTitle(text);
        Entries = SyllabusParser.Parse(text);
        _preview.ItemsSource = Entries.Select(e => e.DisplayName).ToList();
        _import.IsEnabled = Entries.Count > 0;

        if (Entries.Count == 0)
        {
            // Almost always a scan: a photographed page carries no text layer to read.
            _summary.Text = text.Trim().Length == 0
                ? "No text in that PDF — it looks like a scan. A syllabus saved as text, or "
                + "adding the lessons by hand, will work."
                : "No numbered lessons found. This reader looks for lines like "
                + "\"5.6 Center of Mass\".";
            return;
        }

        var units = Entries.Select(e => e.Unit).Where(u => u is not null).Distinct().Count();
        var found = Entries.Count == 1
            ? "1 lesson found"
            : $"{Entries.Count} lessons found"
              + (units > 0 ? $" across {units} unit{(units == 1 ? "" : "s")}" : "");

        _summary.Text = CourseTitle is null
            ? found + "."
            : $"{found}, for {CourseTitle}.";
    }
}
