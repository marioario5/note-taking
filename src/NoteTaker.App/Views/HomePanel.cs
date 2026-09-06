using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.App.Views;

/// <summary>
/// The landing view: a rail of places to go, and a pane showing subjects and their lessons.
/// </summary>
/// <remarks>
/// Review measures a topic, and a topic is a section — so the whole feature rests on work landing
/// in the right section. Left to a tree of "My Notes / First Section" it does not: weeks of Calc 3
/// accumulated in one undifferentiated bucket, which is what made the meters meaningless.
/// Choosing the lesson before writing is the cheapest fix available, provided choosing is one
/// click. That is this view's job.
///
/// Laid out after OneNote's File tab, and taking the parts of it that carry weight: a rail down
/// the left, a back arrow at the top of it, and a titled pane. The rail's entries are this app's
/// own — every one opens something that exists, rather than a Print or a Send that would sit
/// there greyed out.
///
/// Units are read from the lesson's own number rather than stored: "5.6 Center of Mass" belongs
/// to Unit 5 because it says so. A subject whose lessons carry no numbering lists them flat,
/// which is what an SAT syllabus wants anyway.
/// </remarks>
public sealed class HomePanel : Grid
{
    private readonly AppHost _host;
    private readonly StackPanel _rail = new();
    private readonly StackPanel _subjectList = new();
    private readonly StackPanel _lessons = new();
    private readonly TextBlock _paneTitle = new();
    private readonly TextBlock _paneSubtitle = new();

    private IReadOnlyList<Notebook> _notebooks = [];

    /// <summary>
    /// What the pane is showing. Swapped in place rather than opened in a window: the landing
    /// screen is the one place in the app that changes what it shows instead of stacking another
    /// frame on top, and usage is a thing you read and leave, which is exactly that shape.
    /// </summary>
    private readonly Decorator _body = new();

    private UIElement? _subjectsBody;
    private long? _subjectId;

    /// <summary>Raised with the section id when a lesson is chosen.</summary>
    public event Action<long>? LessonChosen;

    /// <summary>Raised when the student leaves the landing view without choosing.</summary>
    public event Action? Dismissed;

    /// <summary>Raised for a rail entry that the window itself owns.</summary>
    public event Action<HomeAction>? ActionRequested;

    public HomePanel(AppHost host)
    {
        _host = host;

        // Darker outside, lighter middle. The pane is the page you are choosing from; the rail
        // and the header framing it are chrome, and colouring them the same flattened the screen
        // into one dark rectangle with words floating in it.
        Background = (Brush)Application.Current.FindResource("SurfacePane");
        // Attached, not a property: a Grid is not a Control, and this is how the font reaches
        // every descendant text element in one go.
        TextElement.SetFontFamily(this, (FontFamily)Application.Current.FindResource("FontShell"));

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Children.Add(BuildRail());

        var pane = BuildPane();
        SetColumn(pane, 1);
        Children.Add(pane);
    }

    // ── The rail ─────────────────────────────────────────────────────────────────────────
    //
    // Four things carry OneNote's File page, and all four are structural rather than
    // decorative: a rail that runs the full height flush to the edge, every entry an icon
    // beside a label, the current entry marked, and a second group pinned to the bottom so the
    // rail has a floor. Without them a list of centred words floats in a dark rectangle, which
    // is what the first attempt at this screen looked like.

    private readonly List<(Button Button, Border Accent, HomeSection Section)> _railRows = [];
    private HomeSection _current = HomeSection.Subjects;

    private UIElement BuildRail()
    {
        var back = RailRow("Back to your notes", "IconChevronRight", () => Dismissed?.Invoke());
        back.Margin = new Thickness(0, 10, 0, 6);

        _rail.Children.Add(RailEntry("Subjects", "IconBookOpen", HomeSection.Subjects,
            () => _ = RefreshAsync(_subjectId)));
        _rail.Children.Add(RailEntry("New subject", "IconPlus", HomeSection.NewSubject,
            () => Run(AddSubjectAsync)));
        _rail.Children.Add(RailEntry("New lesson", "IconFeather", HomeSection.NewLesson,
            () => Run(AddLessonAsync)));
        _rail.Children.Add(RailEntry("Import syllabus", "IconFileText", HomeSection.Import,
            () => Run(ImportSyllabusAsync)));
        _rail.Children.Add(RailDivider());
        _rail.Children.Add(RailEntry("Search notes", "IconSearch", HomeSection.Search,
            () => ActionRequested?.Invoke(HomeAction.Search)));
        _rail.Children.Add(RailEntry("Practice", "IconPlay", HomeSection.Practice,
            () => ActionRequested?.Invoke(HomeAction.Practice)));
        _rail.Children.Add(RailEntry("Usage and budget", "IconGauge", HomeSection.Usage,
            () => Run(ShowUsageAsync)));

        // Pinned to the bottom, the way Account and Options are: it gives the rail a floor and
        // keeps the settings out of the reading order of the things you came here to do.
        var settings = RailEntry("Settings", "IconSettings", HomeSection.Settings,
            () => ActionRequested?.Invoke(HomeAction.Settings));
        settings.Margin = new Thickness(0, 0, 0, 12);

        var rail = new DockPanel { Background = (Brush)FindResource("SurfaceShell") };
        DockPanel.SetDock(back, Dock.Top);
        DockPanel.SetDock(settings, Dock.Bottom);
        DockPanel.SetDock(_railDivider, Dock.Bottom);
        rail.Children.Add(back);
        rail.Children.Add(settings);
        rail.Children.Add(_railDivider);
        rail.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _rail,
        });

        MarkCurrent(HomeSection.Subjects);
        return rail;
    }

    private readonly Border _railDivider = RailDivider();

    /// <summary>An icon beside a label, filling the rail's width.</summary>
    private Button RailRow(string text, string iconKey, Action run)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Controls.Icon
        {
            Geometry = (Geometry)FindResource(iconKey),
            Width = 15,
            Height = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });

        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Content = row,
            Style = (Style)Application.Current.FindResource("RowButton"),
            Padding = new Thickness(16, 8, 12, 8),
            MinHeight = 36,
            FontSize = 13.5,
        };

        button.Click += (_, _) => run();
        return button;
    }

    /// <summary>A rail row plus the accent bar that marks it as the one you are on.</summary>
    private Grid RailEntry(string text, string iconKey, HomeSection section, Action run)
    {
        var button = RailRow(text, iconKey, () =>
        {
            MarkCurrent(section);
            run();
        });

        var accent = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 3, 0, 3),
            CornerRadius = new CornerRadius(0, 2, 2, 0),
            Background = (Brush)FindResource("Accent"),
            Visibility = Visibility.Collapsed,
        };

        var host = new Grid();
        host.Children.Add(button);
        host.Children.Add(accent);

        _railRows.Add((button, accent, section));
        return host;
    }

    private void MarkCurrent(HomeSection section)
    {
        // The one-shot actions light up while their dialog is open and then hand the mark back,
        // because you are still looking at the subjects underneath them.
        _current = section is HomeSection.Subjects ? HomeSection.Subjects : _current;

        foreach (var (button, accent, owned) in _railRows)
        {
            var on = owned == _current;
            accent.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            button.Background = on ? (Brush)FindResource("SurfaceHover") : Brushes.Transparent;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private static Border RailDivider() => new()
    {
        Height = 1,
        Margin = new Thickness(16, 9, 16, 9),
        Background = new SolidColorBrush(Color.FromArgb(0x30, 0xA9, 0xB7, 0xDE)),
    };

    /// <summary>
    /// Runs a rail action, showing anything it throws.
    /// </summary>
    /// <remarks>
    /// A rail entry is a click handler, so an async action started from one is unobserved: it
    /// fails, the exception goes nowhere, and the button reads as simply not working. Surfacing
    /// it is the difference between a bug you can describe and a button that does nothing.
    /// </remarks>
    private async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            NoticeWindow.Tell(Owner, "That didn't work", ex.Message);
        }
    }

    // ── The pane ─────────────────────────────────────────────────────────────────────────

    private UIElement BuildPane()
    {
        _paneTitle.FontSize = 30;
        _paneTitle.FontWeight = FontWeights.SemiBold;
        _paneTitle.Margin = new Thickness(0, 0, 0, 2);
        _paneTitle.Foreground = (Brush)FindResource("TextTitle");
        _paneTitle.Text = "Your subjects";

        _paneSubtitle.Margin = new Thickness(0, 0, 0, 18);
        if (Application.Current.TryFindResource("Marginalia") is Style marginalia)
        {
            _paneSubtitle.Style = marginalia;
        }

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(_paneTitle);
        header.Children.Add(_paneSubtitle);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Add buttons sit under the list they add to. They exist on the rail as well, but the
        // rail is where you go to change what the screen is showing — looking at a list of
        // subjects and wanting one more, the eye goes to the end of the list, not sideways.
        var subjects = new DockPanel();
        var subjectsLabel = Header("SUBJECTS");
        var addSubject = AddButton("New subject", "IconPlus", () => Run(AddSubjectAsync));
        DockPanel.SetDock(subjectsLabel, Dock.Top);
        DockPanel.SetDock(addSubject, Dock.Bottom);
        subjects.Children.Add(subjectsLabel);
        subjects.Children.Add(addSubject);
        subjects.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _subjectList,
        });

        var lessons = new DockPanel { Margin = new Thickness(26, 0, 0, 0) };
        var lessonsLabel = Header("LESSONS");
        var addLesson = AddButton("New lesson", "IconPlus", () => Run(AddLessonAsync));
        DockPanel.SetDock(lessonsLabel, Dock.Top);
        DockPanel.SetDock(addLesson, Dock.Bottom);
        lessons.Children.Add(lessonsLabel);
        lessons.Children.Add(addLesson);
        lessons.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _lessons,
        });

        SetColumn(lessons, 1);
        columns.Children.Add(subjects);
        columns.Children.Add(lessons);

        _subjectsBody = columns;
        _body.Child = columns;

        var pane = new DockPanel { Margin = new Thickness(34, 26, 30, 24) };
        DockPanel.SetDock(header, Dock.Top);
        pane.Children.Add(header);
        pane.Children.Add(_body);
        return pane;
    }

    /// <summary>
    /// Swaps the pane to the week's spending and the month's distribution.
    /// </summary>
    /// <remarks>
    /// Reads rows already on disk, so opening it spends nothing — which is the point of a screen
    /// whose whole subject is what things cost.
    /// </remarks>
    private async Task ShowUsageAsync()
    {
        var now = DateTimeOffset.UtcNow;

        // From the start of last month: each day's allowance is replayed from what its own month
        // had spent before it, so a strip reaching back past the first needs that month's history
        // too, not just the days on screen.
        var daily = await _host.Usage.GetDailyCostsAsync(
            BudgetDay.StartOfMonth(BudgetDay.StartOfMonth(now).AddDays(-1)));

        // What a question actually costs, measured on this month's own turns rather than
        // assumed: it moves with the page image, the thread's length and the model.
        var breakdown = await _host.Usage.GetBreakdownAsync(BudgetDay.StartOfMonth(now));
        var chat = breakdown.Where(row => row.CallType == TutorCallType.SocraticChat).ToList();
        var chatTurnCost = chat.Sum(row => row.Calls) is var calls && calls > 0
            ? chat.Sum(row => row.Cost) / calls
            : 0m;

        var outlook = UsageOutlook.Build(
            _host.Settings.MonthlyCostCapUsd, daily, now, chatTurnCost);

        _paneTitle.Text = "Usage and budget";
        _paneSubtitle.Text = $"${outlook.SpentThisMonth:0.00} of ${outlook.MonthlyCap:0.00} this month.";
        _body.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new UsageBoard(outlook),
        };
    }

    /// <summary>A quiet "add one more" beneath a list.</summary>
    private Button AddButton(string text, string iconKey, Action run)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Controls.Icon
        {
            Geometry = (Geometry)FindResource(iconKey),
            Width = 13,
            Height = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Content = row,
            Style = (Style)Application.Current.FindResource("RowButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(11, 7, 13, 7),
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 13,
        };

        button.Click += (_, _) => run();
        return button;
    }

    private static TextBlock Header(string text)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(2, 0, 0, 6) };
        if (Application.Current.TryFindResource("MicroLabel") is Style style)
        {
            block.Style = style;
        }

        return block;
    }

    private static TextBlock Note(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            Margin = new Thickness(2, 12, 0, 0),
        };

        if (Application.Current.TryFindResource("Marginalia") is Style style)
        {
            block.Style = style;
        }

        return block;
    }

    // ── Content ──────────────────────────────────────────────────────────────────────────

    /// <summary>Reloads subjects and lessons. Called each time the view is shown.</summary>
    public async Task RefreshAsync(long? selectSubjectId = null)
    {
        _notebooks = await _host.Notebooks.GetNotebooksAsync();
        _subjectId = _notebooks.Any(n => n.Id == selectSubjectId)
            ? selectSubjectId
            : _notebooks.FirstOrDefault()?.Id;

        _subjectList.Children.Clear();
        foreach (var notebook in _notebooks)
        {
            var id = notebook.Id;
            var chosen = id == _subjectId;
            var lessonCount = (await _host.Notebooks.GetSectionsAsync(id)).Count;

            // A card rather than a row. Choosing a subject decides where a term's work will be
            // filed, and everything the Review page later says about a topic follows from it —
            // the weight is there to make that read as a decision rather than a list item.
            var name = new TextBlock
            {
                Text = notebook.Name,
                FontSize = 15,
                FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = (Brush)FindResource("TextTitle"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var count = new TextBlock
            {
                Text = lessonCount == 1 ? "1 lesson" : $"{lessonCount} lessons",
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (Brush)FindResource("TextFaint"),
            };

            var stack = new StackPanel();
            stack.Children.Add(name);
            stack.Children.Add(count);

            var card = new Button
            {
                Content = stack,
                Style = (Style)Application.Current.FindResource("CardButton"),
                Padding = new Thickness(15, 12, 14, 13),
                Margin = new Thickness(0, 0, 0, 7),
                BorderBrush = chosen
                    ? (Brush)FindResource("Accent")
                    : (Brush)FindResource("BorderNightSoft"),
                Background = chosen
                    ? (Brush)FindResource("SurfaceHover")
                    : (Brush)FindResource("SurfaceRaised"),
            };

            card.Click += async (_, _) => await RefreshAsync(id);
            _subjectList.Children.Add(card);
        }

        if (_notebooks.Count == 0)
        {
            _subjectList.Children.Add(Note("Nothing yet — start with New subject."));
        }

        await ShowLessonsAsync();
    }

    private Notebook? CurrentSubject => _notebooks.FirstOrDefault(n => n.Id == _subjectId);

    private async Task ShowLessonsAsync()
    {
        // Any route back to the subjects takes the pane with it, so leaving usage is just
        // clicking Subjects rather than closing something.
        if (_subjectsBody is not null)
        {
            _body.Child = _subjectsBody;
        }

        _lessons.Children.Clear();

        if (CurrentSubject is not { } subject)
        {
            _paneTitle.Text = "Your subjects";
            _paneSubtitle.Text = "Calculus 3, SAT, anything you study — each keeps its own progress.";
            _lessons.Children.Add(Note("Add a subject on the left to begin."));
            return;
        }

        _paneTitle.Text = subject.Name;

        var sections = await _host.Notebooks.GetSectionsAsync(subject.Id);
        _paneSubtitle.Text = sections.Count == 1
            ? "1 lesson · pick one to write in it"
            : $"{sections.Count} lessons · pick one to write in it";

        if (sections.Count == 0)
        {
            _paneSubtitle.Text = "No lessons yet";
            _lessons.Children.Add(Note(
                "Import a syllabus and every numbered lesson in it becomes a place to write. "
                + "Nothing is sent anywhere — the file is read on this machine."));
            return;
        }

        // Grouped by the unit the lesson number implies, in numeric order, with anything
        // unnumbered gathered at the end rather than dropped.
        foreach (var group in sections
                     .GroupBy(s => SyllabusParser.UnitOf(s.Name))
                     .OrderBy(g => g.Key ?? int.MaxValue))
        {
            _lessons.Children.Add(new TextBlock
            {
                Text = group.Key is { } unit ? $"UNIT {unit}" : "OTHER",
                Margin = new Thickness(2, 14, 0, 5),
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 11,
            });

            // Textbook order, from the number in the name — not creation order, which drops a
            // lesson added later at the bottom of its unit.
            foreach (var section in group
                         .OrderBy(sec => SyllabusParser.NumberOf(sec.Name)?.Lesson ?? int.MaxValue)
                         .ThenBy(sec => sec.Name, StringComparer.OrdinalIgnoreCase))
            {
                var open = new Button
                {
                    Content = section.Name,
                    Style = (Style)Application.Current.FindResource("RowButton"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(13, 7, 13, 7),
                    Margin = new Thickness(0, 2, 0, 2),
                };

                var id = section.Id;
                open.Click += (_, _) => LessonChosen?.Invoke(id);
                _lessons.Children.Add(open);
            }
        }
    }

    // ── Rail actions this panel owns ─────────────────────────────────────────────────────

    private Window Owner => Window.GetWindow(this)!;

    private async Task AddSubjectAsync()
    {
        var name = PromptWindow.Ask(Owner, "New subject", "What are you studying?", "Calculus 3");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var created = await _host.Notebooks.CreateNotebookAsync(name.Trim());
        await RefreshAsync(created.Id);
    }

    private async Task AddLessonAsync()
    {
        if (CurrentSubject is not { } subject)
        {
            NoticeWindow.Tell(Owner, "Pick a subject first", "A lesson belongs to one of them.");
            return;
        }

        var name = PromptWindow.Ask(Owner, "New lesson", "Name it as the textbook does.", "5.6 Center of Mass");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await _host.Notebooks.CreateSectionAsync(subject.Id, name.Trim());
        await ShowLessonsAsync();
    }

    private async Task ImportSyllabusAsync()
    {
        if (CurrentSubject is not { } subject)
        {
            NoticeWindow.Tell(Owner, "Pick a subject first", "The lessons need somewhere to land.");
            return;
        }

        var dialog = new SyllabusWindow(subject.Name) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Entries.Count == 0)
        {
            return;
        }

        var existing = (await _host.Notebooks.GetSectionsAsync(subject.Id))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var entry in dialog.Entries.Where(e => !existing.Contains(e.DisplayName)))
        {
            await _host.Notebooks.CreateSectionAsync(subject.Id, entry.DisplayName);
            added++;
        }

        // A syllabus states the course it belongs to, so a subject still carrying a placeholder
        // name can take the real one here rather than being renamed by hand afterwards.
        if (dialog.CourseTitle is { Length: > 0 } course
            && !string.Equals(course, subject.Name, StringComparison.OrdinalIgnoreCase)
            && NoticeWindow.Confirm(
                Owner,
                $"Rename this subject to \"{course}\"?",
                $"The syllabus says it is for {course}. The subject is currently called {subject.Name}.",
                "Rename"))
        {
            await _host.Notebooks.RenameNotebookAsync(subject.Id, course);
            await RefreshAsync(subject.Id);
        }

        await ShowLessonsAsync();

        NoticeWindow.Tell(
            Owner,
            added == 1 ? "1 lesson added" : $"{added} lessons added",
            added == dialog.Entries.Count
                ? "Pick one to start writing in it."
                : $"{dialog.Entries.Count - added} were already there.");
    }
}

/// <summary>Which rail entry is currently marked.</summary>
public enum HomeSection
{
    Subjects,
    NewSubject,
    NewLesson,
    Import,
    Search,
    Practice,
    Usage,
    Settings,
}

/// <summary>A rail entry the main window carries out, because it owns the window it opens.</summary>
public enum HomeAction
{
    Search,
    Practice,
    Usage,
    Settings,
}
