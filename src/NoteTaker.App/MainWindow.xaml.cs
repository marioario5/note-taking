using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using NoteTaker.AI;
using NoteTaker.App.Controls;
using NoteTaker.App.Services;
using NoteTaker.App.ViewModels;
using NoteTaker.App.Views;
using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.App;

public partial class MainWindow : Window, IActivePageSource
{
    /// <summary>Ink for the night ground: cream, periwinkle and the one amber.</summary>
    private static readonly (string Name, Color Colour)[] NightPalette =
    [
        ("Cream", Color.FromRgb(0xFB, 0xF6, 0xEC)),
        ("Periwinkle", Color.FromRgb(0xA9, 0xB7, 0xDE)),
        ("Star", Color.FromRgb(0xFE, 0xBB, 0x55)),
        ("Sage", Color.FromRgb(0x6F, 0x9B, 0x7D)),
        ("Clay", Color.FromRgb(0xC4, 0x61, 0x4B)),
    ];

    /// <summary>
    /// Ink for a page with a PDF behind it. Cream on a printed worksheet would be invisible,
    /// so the palette flips to the dark end of the same six families.
    /// </summary>
    private static readonly (string Name, Color Colour)[] PaperPalette =
    [
        ("Ink", Color.FromRgb(0x16, 0x1E, 0x2F)),
        ("Night", Color.FromRgb(0x47, 0x6A, 0x92)),
        ("Clay", Color.FromRgb(0xC4, 0x61, 0x4B)),
        ("Sage", Color.FromRgb(0x51, 0x7A, 0x60)),
        ("Star", Color.FromRgb(0xC9, 0x82, 0x2A)),
    ];

    private readonly AppHost _host;
    private readonly PageSnapshotProvider _snapshots;
    private readonly TutorCoordinator _tutor;
    private readonly ObservableCollection<NotebookNode> _notebooks = [];
    private readonly ObservableCollection<FeedbackItemViewModel> _feedback = [];

    private readonly ObservableCollection<SkillScoreViewModel> _skills = [];

    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _periodicSaveTimer;

    private PageNode? _currentPage;
    /// <summary>Worksheet plus placed pictures, flattened. For export and chat captures.</summary>
    private ImageSource? _currentBackground;

    /// <summary>The PDF worksheet, re-rendered from PdfPath on open. Visible to the tutor.</summary>
    private ImageSource? _pdfBackground;

    /// <summary>Picture ids currently in the database for this page; see SyncImagesAsync.</summary>
    private readonly HashSet<long> _persistedImageIds = [];
    private int _currentRevision;
    private long _lastInkAt;
    private int _swatchIndex;
    private bool _dirty;
    private bool _loadingPage;
    private bool _overBudget;
    private long _activeThreadId;
    private TutorFeedback? _activeAnchor;
    /// <summary>1-based hint-ladder turn for the currently open chat panel.</summary>
    private int _socraticSessionTurn;

    /// <summary>The skill the ladder is currently climbing, so a change of subject can reset it.</summary>
    private string _ladderSkill = string.Empty;
    private DateTimeOffset _sessionStart = DateTimeOffset.UtcNow;
    /// <summary>
    /// Awaited by every chat-render call before touching <see cref="ChatWebView"/>'s
    /// CoreWebView2 — kicked off once in the constructor rather than gated behind a bool
    /// flag, so a message that arrives before startup finishes still queues correctly
    /// instead of racing it.
    /// </summary>
    private readonly Task _chatWebViewReady;

    /// <summary>
    /// Bumped by every ink change. Lets an in-flight save notice that the student started
    /// writing again while it was awaiting, so it neither clears the dirty flag on top of
    /// unsaved ink nor spends a page render competing with a live stroke.
    /// </summary>
    private int _inkEpoch;

    /// <summary>Fires after a real lull in writing; see the constructor for why it is separate from saving.</summary>
    private readonly DispatcherTimer _indexTimer;

    /// <summary>Last ISF written to the database, awaiting its (expensive) indexing pass.</summary>
    private byte[]? _pendingIndexIsf;
    private long _pendingIndexPageId;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        // Custom WindowChrome overhangs the screen when maximized unless we answer
        // WM_GETMINMAXINFO ourselves — see MaximizeGuard.
        MaximizeGuard.Attach(this);

        // Applied here rather than with the rest of the workspace state, which is restored
        // from Loaded: by then the window is on screen, so maximizing would be visible as a
        // jump from the designed size to full screen.
        if (_host.Settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        _snapshots = new PageSnapshotProvider(Dispatcher, this);
        _tutor = _host.AttachTutor(_snapshots);
        _tutor.FeedbackProduced += OnFeedbackProduced;
        _tutor.StatusChanged += OnTutorStatusChanged;

        // Debounced write-behind: ink is never held hostage to disk I/O. Background priority
        // (not the default Normal, which in WPF's dispatcher actually outranks Input and
        // Render) so a tick landing in the pause between two strokes — e.g. the two bars of
        // "=", or any multi-stroke glyph — can't jump the queue ahead of pending stylus input
        // or a wet-ink composition pass and stall the pen mid-write. Save/index work (ISF
        // serialize, page render, ink recognition) still runs, just after input and render
        // have had their turn, not before.
        _autoSaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _autoSaveTimer.Tick += async (_, _) => await SaveCurrentPageAsync();

        _periodicSaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _periodicSaveTimer.Tick += async (_, _) => await SaveCurrentPageAsync();
        _periodicSaveTimer.Start();

        // Indexing is deliberately NOT part of saving. A trace on the target device measured
        // one save at 928 ms, of which 924 ms was indexing — a stroke deserialize, a 512px
        // page render and handwriting recognition, none of which can be interrupted once
        // started, so no dispatcher priority can keep them out of the pen's way. Saving
        // itself took 4 ms. Splitting them lets ink hit the database on the 1.5 s debounce
        // as before, while the expensive pass waits for a real lull.
        _indexTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };
        _indexTimer.Tick += async (_, _) => await FlushIndexAsync();

        NavTree.ItemsSource = _notebooks;
        FeedbackList.ItemsSource = _feedback;
        SkillList.ItemsSource = _skills;
        _chatWebViewReady = InitializeChatWebViewAsync();

        BuildSwatches();

        WireEvents();
        PageEditor.AttachPointerGuard(this);
        Loaded += async (_, _) => await InitializeAsync();
        Closing += async (_, _) =>
        {
            await SaveCurrentPageAsync();
            await FlushIndexAsync();
            SaveWorkspaceState();
        };
    }

    public long ActivePageId => _currentPage?.Id ?? 0;

    public StrokeCollection ActiveStrokes => PageEditor.Strokes;

    /// <summary>
    /// What a vision model is allowed to see behind the ink: the PDF worksheet only.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the composite. This is the property <see cref="PageSnapshotProvider"/>
    /// reads for every tutor scan and chat crop, so it is the choke point where "pasted
    /// images are never sent to a model" is actually enforced. The stamp layer is excluded
    /// here by construction rather than by relying on callers passing ocrContrast:true —
    /// that flag skips the background as a side effect of switching to OCR colours, which
    /// is a coincidence one refactor away from silently uploading the student's pictures.
    /// A PDF the student is working ON is different: that is the problem being solved, and
    /// the tutor needs it.
    /// </remarks>
    public ImageSource? ActiveBackground => _pdfBackground;

    /// <summary>
    /// What the chat tutor sees: the worksheet plus whatever the student stamped on the
    /// page. Pasting a picture is how you hand the tutor a question, so chat has to read it
    /// — but <see cref="ActiveBackground"/> stays worksheet-only so the flagging scan never
    /// marks up a screenshot as though the student had written it.
    /// </summary>
    public ImageSource? ActiveChatBackground => _currentBackground;

    public IReadOnlyList<PageImage> ActiveImages => PageEditor.PageImages;

    public Rect ActiveViewBounds => PageEditor.ViewBounds;

    public int ActiveStrokeRevision => _currentRevision;

    public long ActiveLastInkAt => _lastInkAt;

    private (string Name, Color Colour)[] ActivePalette =>
        PageEditor.Ground == PageGround.Paper ? PaperPalette : NightPalette;

    private void WireEvents()
    {
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        // Through MaximizeGuard, not WindowState directly: it has to know a restore was asked
        // for, so it does not treat it the way it treats the on-screen keyboard un-maximizing
        // us and immediately put the window back.
        MaximizeButton.Click += (_, _) => MaximizeGuard.ToggleMaximize(this);
        CloseButton.Click += (_, _) => Close();

        PageEditor.InkChanged += OnInkChanged;

        // A picture finished being dragged or resized: keep its new rectangle, and refresh
        // the flattened copy that export and the chat capture read from.
        PageEditor.ImageBoundsCommitted += async (_, image) => await _host.Ink.UpdateImageBoundsAsync(image);
        PageEditor.ImagesChanged += async (_, _) =>
        {
            RefreshBackgroundComposite();
            await SyncImagesAsync();
        };

        PageEditor.ImageDeleteRequested += (_, image) =>
        {
            // Removal goes through the page control so it lands on the undo stack; the
            // database catches up via SyncImagesAsync above.
            PageEditor.RemoveImage(image);
            TutorStatus.Text = "Removed the picture. Ctrl+Z brings it back.";
        };
        PageEditor.HighlightTapped += async (_, feedback) => await OpenThreadAsync(feedback);
        PageEditor.ZoomChanged += (_, _) => UpdateZoomLabel();
        PageEditor.GroundChanged += (_, _) =>
        {
            BuildSwatches();
            ApplyTool();
        };

        NavTree.SelectedItemChanged += async (_, e) =>
        {
            if (e.NewValue is PageNode page)
            {
                await OpenPageAsync(page);
            }
        };

        foreach (var button in new[] { ToolPen, ToolHighlighter, ToolEraser, ToolSelect })
        {
            button.Checked += (_, _) => ApplyTool();
        }

        WidthSlider.ValueChanged += (_, _) => ApplyTool();

        UndoButton.Click += (_, _) => PageEditor.Undo();
        RedoButton.Click += (_, _) => PageEditor.Redo();
        MenuUndo.Click += (_, _) => PageEditor.Undo();
        MenuRedo.Click += (_, _) => PageEditor.Redo();
        MenuPasteImage.Click += async (_, _) => await PasteImageAsync();

        ZoomInButton.Click += (_, _) => StepZoom(1.25);
        ZoomOutButton.Click += (_, _) => StepZoom(1 / 1.25);
        ZoomFitButton.Click += (_, _) => FitZoom();
        MenuZoomIn.Click += (_, _) => StepZoom(1.25);
        MenuZoomOut.Click += (_, _) => StepZoom(1 / 1.25);
        MenuZoomFit.Click += (_, _) => FitZoom();

        MenuRuled.Click += (_, _) =>
        {
            PageEditor.IsRuled = MenuRuled.IsChecked;
            SecondPage.IsRuled = MenuRuled.IsChecked;
        };

        // Recording starts with the app and never stops (see App.OnStartup), so this only
        // writes out what has been captured so far. Closing the app dumps it too — this is for
        // grabbing a file mid-session, without having to remember to arm anything first.
        MenuInkTrace.Click += (_, _) =>
            NoticeWindow.Tell(this, "Ink trace saved", InkTrace.Dump());

        MenuInkProbes.Click += (_, _) =>
        {
            if (!MenuInkProbes.IsChecked)
            {
                InkTrace.StopProbes();
                TutorStatus.Text = string.Empty;
                return;
            }

            InkTrace.StartProbes(Dispatcher);
            TutorStatus.Text = "Recording input timing…";
        };

        AsideToggle.Click += (_, _) => SetSidebarVisible(AsideToggle.IsChecked == true);
        MenuShowAside.Click += (_, _) => SetSidebarVisible(MenuShowAside.IsChecked);
        SetSidebarVisible(false);

        NavToggle.Click += (_, _) => SetNavVisible(NavToggle.IsChecked == true);
        PageCrumb.Click += (_, _) => SetNavVisible(NavColumn.Width.Value <= 0);
        SetNavVisible(false);

        ModeLive.Checked += async (_, _) => await SetModeAsync(TutorMode.Live);
        ModePractice.Checked += async (_, _) => await SetModeAsync(TutorMode.Practice);
        ModeReview.Checked += async (_, _) => await SetModeAsync(TutorMode.Review);
        MenuPracticeMode.Click += async (_, _) => await SetModeAsync(
            MenuPracticeMode.IsChecked ? TutorMode.Practice : TutorMode.Live);
        EndPracticeButton.Click += async (_, _) =>
        {
            SetModeRadio(TutorMode.Review);
            await SetModeAsync(TutorMode.Review);
        };

        MenuReviewPage.Click += async (_, _) => await ReviewPageAsync();
        MenuSessionReport.Click += async (_, _) => await ShowSessionReportAsync();
        MenuFlushQueue.Click += async (_, _) => await FlushQueueAsync();
        MenuUsage.Click += async (_, _) => await ShowUsageAsync();

        AddNotebookButton.Click += async (_, _) => await AddNotebookAsync();
        AddSectionButton.Click += async (_, _) => await AddSectionAsync();
        AddPageButton.Click += async (_, _) => await AddPageAsync();
        SearchButton.Click += (_, _) => OpenSearch();
        MenuHome.Click += async (_, _) => await ShowHomeAsync();
        HeaderResume.Click += async (_, _) =>
        {
            HideHome();
            var pages = await _host.Pages.GetAllPagesAsync();
            var recent = pages.OrderByDescending(p => p.UpdatedAt).FirstOrDefault();
            if (recent is not null)
            {
                await ReloadNavigationAsync(recent.Id);
            }
        };
        MenuNewNotebook.Click += async (_, _) => await AddNotebookAsync();
        MenuNewSection.Click += async (_, _) => await AddSectionAsync();
        MenuNewPage.Click += async (_, _) => await AddPageAsync();
        MenuRenamePage.Click += async (_, _) => await RenamePageAsync();
        MenuDeletePage.Click += async (_, _) => await DeletePageAsync();

        MenuImportPdf.Click += async (_, _) => await AttachPdfAsync();
        MenuExportPng.Click += (_, _) => Export("png");
        MenuExportPdf.Click += (_, _) => Export("pdf");
        MenuExportSvg.Click += (_, _) => Export("svg");
        MenuExit.Click += (_, _) => Close();

        MenuSearch.Click += (_, _) => OpenSearch();
        MenuRelated.Click += (_, _) => OpenSearch(showRelated: true);
        MenuPractice.Click += (_, _) => OpenPracticeGenerator();
        MenuReindex.Click += async (_, _) => await ReindexAsync();

        MenuGraph.Click += (_, _) => InsertGraph();
        MenuCode.Click += (_, _) => RunPythonCell();
        GraphButton.Click += (_, _) => InsertGraph();
        CodeButton.Click += (_, _) => RunPythonCell();
        MenuSplitView.Click += async (_, _) => await ToggleSplitViewAsync();
        MenuBenchmark.Click += (_, _) => new BenchmarkWindow { Owner = this }.ShowDialog();
        MenuSettings.Click += (_, _) => OpenSettings();

        AddKeyButton.Click += (_, _) => OpenSettings();
        RaiseLimitButton.Click += (_, _) => OpenSettings();

        SendButton.Click += async (_, _) => await SendChatAsync();

        // Enter-to-send and the placeholder both live in the composer page now — a math field
        // has to see its own keystrokes to build a fraction, so WPF cannot intercept them
        // first. The page posts back when the student presses Enter instead.
        ChatInputView.WebMessageReceived += OnComposerMessage;

        FeedbackList.SelectionChanged += async (_, _) =>
        {
            if (FeedbackList.SelectedItem is FeedbackItemViewModel item)
            {
                PageEditor.ScrollToRegion(item.Feedback.Region);
                await OpenThreadAsync(item.Feedback);
            }
        };

        _host.Connectivity.ConnectivityChanged += (_, online) =>
            Dispatcher.Invoke(() => UpdateConnectivity(online));

        PreviewKeyDown += OnShortcut;
        ReportButton.Click += (_, _) => _ = WriteReportAsync();
    }

    /// <summary>
    /// Remembers where the student was working and what they were writing with, so reopening
    /// resumes rather than dumping them back at the top of the sheet with a different nib.
    /// </summary>
    private void SaveWorkspaceState()
    {
        var settings = _host.Settings;
        settings.PenWidth = WidthSlider.Value;
        settings.ViewZoom = PageEditor.ZoomLevel;
        settings.ViewPanX = PageEditor.PanOffset.X;
        settings.ViewPanY = PageEditor.PanOffset.Y;

        // Minimized is not a state worth reopening in, so it counts as whatever the window
        // would return to.
        settings.WindowMaximized = WindowState == WindowState.Maximized
            || (WindowState == WindowState.Minimized && settings.WindowMaximized);

        settings.Save();
    }

    private void RestoreWorkspaceState()
    {
        var settings = _host.Settings;
        WidthSlider.Value = Math.Clamp(settings.PenWidth, WidthSlider.Minimum, WidthSlider.Maximum);

        // Only when all three were stored together — a half-restored camera is worse than a
        // predictable default, and the nulls also mark a first run.
        if (settings is { ViewZoom: { } zoom, ViewPanX: { } panX, ViewPanY: { } panY })
        {
            PageEditor.ZoomLevel = zoom;
            PageEditor.PanOffset = new Point(panX, panY);
        }
    }

    private async Task InitializeAsync()
    {
        RestoreWorkspaceState();
        ApplyTool();
        UpdateZoomLabel();
        UpdateConnectivity(_host.Connectivity.IsOnline);
        ApplyVisionAvailability();

        // The chat model, not the vision one: with page checking off, vision is the model
        // that never runs, and naming it in the status bar was already misleading before that.
        ModelStatus.Text = _host.Settings.ChatModel;

        await ReloadNavigationAsync();
        await UpdateUsageAsync();

        // Last, and only after the notebook is loaded behind it: the landing screen is a way of
        // choosing where to write, not a gate. Dismissing it leaves the student on the page they
        // had open rather than nowhere.
        await ShowHomeAsync();
    }

    /// <summary>Rebuilds the swatch strip for whichever ground the page is standing on.</summary>
    private void BuildSwatches()
    {
        var palette = ActivePalette;
        _swatchIndex = Math.Clamp(_swatchIndex, 0, palette.Length - 1);

        SwatchStrip.Children.Clear();

        for (var index = 0; index < palette.Length; index++)
        {
            var (name, colour) = palette[index];

            var swatch = new RadioButton
            {
                Style = (Style)FindResource("SwatchToggle"),
                GroupName = "Swatch",
                Background = new SolidColorBrush(colour),
                ToolTip = name,
                IsChecked = index == _swatchIndex,
                Tag = index,
            };

            swatch.Checked += (sender, _) =>
            {
                _swatchIndex = (int)((RadioButton)sender).Tag;
                ApplyTool();
            };

            SwatchStrip.Children.Add(swatch);
        }
    }

    private async Task ReloadNavigationAsync(long? selectPageId = null)
    {
        _notebooks.Clear();

        foreach (var notebook in await _host.Notebooks.GetNotebooksAsync())
        {
            var notebookNode = new NotebookNode(notebook);

            // Ordered the same way the landing screen orders them: by the lesson number in
            // the name, so the tree and the launcher never disagree about where 5.6 sits.
            var ordered = (await _host.Notebooks.GetSectionsAsync(notebook.Id))
                .OrderBy(sec => SyllabusParser.NumberOf(sec.Name)?.Unit ?? int.MaxValue)
                .ThenBy(sec => SyllabusParser.NumberOf(sec.Name)?.Lesson ?? int.MaxValue)
                .ThenBy(sec => sec.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var pagesBySection = new Dictionary<long, IReadOnlyList<NoteTaker.Core.Models.Page>>();
            foreach (var section in ordered)
            {
                pagesBySection[section.Id] = await _host.Pages.GetPagesAsync(section.Id);
            }

            // Grouped into units, so an imported syllabus is six chapters rather than
            // thirty-six lessons in one unscrollable run. Only the unit holding the page being
            // opened starts expanded; the rest stay shut.
            foreach (var group in ordered.GroupBy(sec => SyllabusParser.NumberOf(sec.Name)?.Unit))
            {
                var holdsTarget = selectPageId is not null
                    && group.Any(sec => pagesBySection.TryGetValue(sec.Id, out var inSection)
                        && inSection.Any(page => page.Id == selectPageId));

                var unitNode = new UnitNode(
                    group.Key is { } unit ? $"Unit {unit}" : "Other",
                    expanded: holdsTarget || notebookNode.Units.Count == 0);

                foreach (var section in group)
                {
                    var sectionNode = new SectionNode(section);

                    foreach (var page in pagesBySection[section.Id])
                    {
                        sectionNode.Pages.Add(new PageNode(page));
                    }

                    sectionNode.IsExpanded = sectionNode.Pages.Any(p => p.Id == selectPageId);
                    unitNode.Sections.Add(sectionNode);
                }

                notebookNode.Units.Add(unitNode);
            }

            _notebooks.Add(notebookNode);
        }

        var target = selectPageId is null
            ? _notebooks.SelectMany(n => n.Sections).SelectMany(s => s.Pages).FirstOrDefault()
            : _notebooks.SelectMany(n => n.Sections).SelectMany(s => s.Pages)
                .FirstOrDefault(p => p.Id == selectPageId);

        if (target is not null)
        {
            await OpenPageAsync(target);
            target.IsSelected = true;
        }
        else
        {
            _currentPage = null;
            PageEditor.IsPageOpen = false;
            UpdatePageStatus();
        }
    }

    private HomePanel? _home;

    /// <summary>
    /// Swaps the window over to the landing view, building it the first time it is asked for.
    /// </summary>
    /// <remarks>
    /// The panel is kept rather than rebuilt so returning to it is instant and the subject you
    /// were looking at is still selected. Its contents are refreshed on every show, because a
    /// lesson may have been renamed or added since.
    /// </remarks>
    private async Task ShowHomeAsync()
    {
        if (_home is null)
        {
            _home = new HomePanel(_host);
            _home.LessonChosen += async sectionId =>
            {
                HideHome();
                await OpenLessonAsync(sectionId);
            };

            _home.Dismissed += HideHome;

            // The rail's lower half opens windows the main window owns, so it carries them out
            // rather than handing the panel a pile of dependencies it would only pass along.
            _home.ActionRequested += action =>
            {
                switch (action)
                {
                    case HomeAction.Search:
                        OpenSearch();
                        break;
                    case HomeAction.Practice:
                        HideHome();
                        OpenPracticeGenerator();
                        break;
                    case HomeAction.Usage:
                        _ = ShowUsageAsync();
                        break;
                    case HomeAction.Settings:
                        OpenSettings();
                        break;
                }
            };

            HomeHost.Children.Add(_home);
        }

        ShowChrome(false);
        HomeHost.Visibility = Visibility.Visible;
        await _home.RefreshAsync(CurrentNotebookId());
        await FillLandingHeaderAsync();
    }

    /// <summary>
    /// Fills the landing screen's header strip: where you are, and one click back to the page
    /// you were last on.
    /// </summary>
    /// <remarks>
    /// The strip is otherwise empty here — the toolbar that normally fills it is addressed to a
    /// page, and there is no page yet. Resume earns the space rather than decorating it: the
    /// commonest reason to open this screen is to carry on with the thing already in progress,
    /// and picking it out of a list of thirty-five lessons is the long way round.
    /// </remarks>
    private async Task FillLandingHeaderAsync()
    {
        var notebook = _notebooks.FirstOrDefault(n => n.Id == CurrentNotebookId())
            ?? _notebooks.FirstOrDefault();
        HeaderSubject.Text = notebook is null ? string.Empty : $"· {notebook.Name}";

        var pages = await _host.Pages.GetAllPagesAsync();
        var recent = pages.OrderByDescending(p => p.UpdatedAt).FirstOrDefault();
        if (recent is null)
        {
            HeaderResume.Visibility = Visibility.Collapsed;
            return;
        }

        var lesson = _notebooks.SelectMany(n => n.Sections)
            .FirstOrDefault(sec => sec.Id == recent.SectionId)?.Name;

        HeaderResume.Content = string.IsNullOrWhiteSpace(lesson)
            ? $"Continue — {recent.Title}"
            : $"Continue — {lesson}";
        HeaderResume.Visibility = Visibility.Visible;
    }

    /// <summary>Returns the window to the notebook it was already showing.</summary>
    private void HideHome()
    {
        HomeHost.Visibility = Visibility.Collapsed;
        ShowChrome(true);
    }

    /// <summary>
    /// Shows or hides the parts of the title bar that only mean something with a page open.
    /// </summary>
    /// <remarks>
    /// Pens, colours, zoom and the tutor mode are all addressed to a page, and the landing view
    /// has none — leaving them up made the screen read as the notebook with a panel over it
    /// rather than as a place of its own. The window buttons stay, because the window still has
    /// to be closable from here.
    /// </remarks>
    private void ShowChrome(bool visible)
    {
        var state = visible ? Visibility.Visible : Visibility.Collapsed;
        ToolbarMain.Visibility = state;
        ModeSegments.Visibility = state;
        StreakStrip.Visibility = state;

        // The landing screen wants the outside darker than the middle, so the header drops its
        // gradient for a flat near-black and hands the starfield over to a geometric motif.
        HeaderStars.Visibility = state;
        HeaderOrnament.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        HeaderLanding.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        HeaderBar.Background = visible
            ? (Brush)FindResource("HeaderWash")
            : (Brush)FindResource("SurfaceShell");
    }

    /// <summary>
    /// Opens a lesson's most recent page, starting one when the lesson is empty.
    /// </summary>
    /// <remarks>
    /// Creating the page here is what makes "pick a lesson and start writing" true. Without it a
    /// freshly imported syllabus is fifty lessons that all open onto nothing, and the student is
    /// back to making pages by hand and filing them themselves — the exact chore the landing
    /// screen exists to remove.
    /// </remarks>
    private async Task OpenLessonAsync(long sectionId)
    {
        var pages = await _host.Pages.GetPagesAsync(sectionId);
        var page = pages.Count > 0
            ? pages[^1]
            : await _host.Pages.CreatePageAsync(sectionId, $"Page {DateTime.Now:MMM d}");

        await ReloadNavigationAsync(page.Id);
    }

    private async Task OpenPageAsync(PageNode page)
    {
        if (_currentPage?.Id == page.Id)
        {
            return;
        }

        await SaveCurrentPageAsync();

        // Leaving the page: index it now while nothing is being written, rather than
        // stranding the pass on a timer that will never fire for a page we have left.
        await FlushIndexAsync();

        _loadingPage = true;
        try
        {
            _currentPage = page;
            _activeThreadId = 0;
            _activeAnchor = null;
            _socraticSessionTurn = 0;
            _ladderSkill = string.Empty;
            await ClearChatAsync();

            var ink = await _host.Ink.LoadAsync(page.Id);
            var strokes = new StrokeCollection();

            if (ink is { IsfBlob.Length: > 0 })
            {
                try
                {
                    using var stream = new MemoryStream(ink.IsfBlob);
                    strokes = new StrokeCollection(stream);
                }
                catch (ArgumentException)
                {
                    NoticeWindow.Tell(
                        this,
                        "That page's ink wouldn't open.",
                        "It has been left blank rather than overwritten, so nothing is lost.");
                    _loadingPage = true;
                }
            }

            _currentRevision = ink?.Revision ?? 0;
            PageEditor.LoadStrokes(strokes);
            // A genuinely blank page gets the mascot too, not just "no page selected at
            PageEditor.IsPageOpen = true;

            _pdfBackground = page.Model.PdfPath is { Length: > 0 } path && File.Exists(path)
                ? PdfBackgroundService.Render(path, page.Model.PdfPageIndex ?? 0)
                : null;

            // Placed pictures live in their own table precisely so they survive this: the
            // PDF layer above is rebuilt from scratch every open, which is why anything
            // composited into it (graphs, Python output) used to vanish on leaving the page.
            var pageImages = await RepairStrayImagesAsync(await _host.Ink.GetImagesAsync(page.Id));
            _persistedImageIds.Clear();
            foreach (var image in pageImages)
            {
                _persistedImageIds.Add(image.Id);
            }

            PageEditor.LoadImages(pageImages);
            RefreshBackgroundComposite();

            SetModeRadio(page.TutorMode);
            await RefreshFeedbackAsync();
            await RefreshReviewAsync();
            UpdateSidebarState();

            Title = $"NoteTaker — {page.Title}";
            SaveStatus.Text = "Saved";
            _dirty = false;
            UpdatePageStatus();
            UpdateNotices();
        }
        finally
        {
            _loadingPage = false;
        }
    }

    private void OnInkChanged(object? sender, InkChangedEventArgs e)
    {
        // Stamped before the guard below: a chat capture wants to know the hand is still
        // moving even when there is no page to mark dirty.
        _lastInkAt = Environment.TickCount64;

        if (_loadingPage || _currentPage is null)
        {
            return;
        }

        _dirty = true;
        _inkEpoch++;
        SaveStatus.Text = "Unsaved";

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();

        // The 30s backstop was never rescheduled by writing, so it could land in the middle
        // of a stroke no matter how recently the debounced save above had run. Push it out
        // too: while ink keeps arriving the debounced save is the one that should fire.
        _periodicSaveTimer.Stop();
        _periodicSaveTimer.Start();

        // Practice mode is enforced inside the coordinator too; this just avoids the call.
        if (_currentPage.TutorMode == TutorMode.Live)
        {
            _tutor.NotifyInkChanged(_currentPage.Id, TutorMode.Live, e.ChangedRegion);
        }
    }

    private async Task SaveCurrentPageAsync()
    {
        _autoSaveTimer.Stop();

        if (!_dirty || _currentPage is null)
        {
            return;
        }

        var page = _currentPage;
        var epoch = _inkEpoch;

        try
        {
            using var save = InkTrace.Measure(InkEvent.SaveBegin, InkEvent.SaveEnd);
            using var stream = new MemoryStream();
            PageEditor.Strokes.Save(stream);
            var isf = stream.ToArray();

            _currentRevision = await _host.Ink.SaveAsync(page.Id, isf);
            await _host.Pages.UpdatePageAsync(page.Model);

            // Only claim "saved" if no stroke arrived while those awaits were in flight —
            // otherwise this clears the dirty flag on top of ink that was never serialized,
            // and the next debounce sees a clean page and skips it. That ink then survives
            // only by luck, until some later save happens to pick it up.
            if (_inkEpoch == epoch)
            {
                _dirty = false;
                SaveStatus.Text = "Saved";
            }

            // Queue the indexing pass rather than running it here, and restart its idle
            // timer — while the student keeps writing it keeps being pushed out.
            _pendingIndexIsf = isf;
            _pendingIndexPageId = page.Id;
            _indexTimer.Stop();
            _indexTimer.Start();
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "Not saved";
            NoticeWindow.Tell(this, "That didn't save. Trying again.", ex.Message);
        }
    }

    /// <summary>
    /// Rebuilds the search index for a page. Deliberately the lowest-priority thing the app
    /// does: it deserializes the whole stroke collection, rasterizes a 512px page, and runs
    /// handwriting recognition, all on the UI thread.
    /// </summary>
    /// <remarks>
    /// The autosave timer asks for <see cref="DispatcherPriority.Background"/>, but that only
    /// governs when its handler STARTS — WPF resumes an await continuation at Normal (9),
    /// which outranks stylus Input (5). So every await upstream of here silently promoted
    /// this work back above the pen, and a dispatcher operation cannot be preempted once it
    /// begins. Since the debounce lands ~1.5s after the last stroke — almost exactly the
    /// pause before reaching up to write a superscript — it would routinely start just as the
    /// next stroke did, hold the thread for its duration, and let the queued packets coalesce.
    /// The stroke then arrived late and all at once, with its middle collapsed away: a "3"
    /// drawn as a single arc, not fluid.
    ///
    /// So: drop back to Background explicitly before starting, and again between each stage,
    /// so pending pen input is always served first. And if the student resumed writing while
    /// we waited, abandon the pass entirely — the next save re-indexes anyway, and stale
    /// index data for a few more seconds costs nothing next to a broken stroke.
    /// </remarks>
    /// <summary>
    /// Runs the queued indexing pass, if any. Called from the idle timer, and directly when
    /// leaving a page or closing — moments where a long pass costs nothing because nobody is
    /// mid-stroke.
    /// </summary>
    private async Task FlushIndexAsync()
    {
        _indexTimer.Stop();

        if (_pendingIndexIsf is not { } isf)
        {
            return;
        }

        var pageId = _pendingIndexPageId;
        _pendingIndexIsf = null;

        await IndexPageAsync(pageId, isf, _inkEpoch);
    }

    private async Task IndexPageAsync(long pageId, byte[] isf, int epoch)
    {
        try
        {
            using var index = InkTrace.Measure(InkEvent.IndexBegin, InkEvent.IndexEnd);

            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_inkEpoch != epoch)
            {
                InkTrace.Log(InkEvent.IndexAbandoned, 0, 1);
                return;
            }

            var strokes = new StrokeCollection(new MemoryStream(isf));

            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_inkEpoch != epoch)
            {
                return;
            }

            var png = PageRenderer.RenderPagePng(strokes, _currentBackground, 512);

            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_inkEpoch != epoch)
            {
                InkTrace.Log(InkEvent.IndexAbandoned, 0, 2);
                return;
            }

            var text = await InkRecognitionService.RecognizeAsync(strokes);
            await _host.Indexing.IndexPageAsync(pageId, png, text, _currentRevision);
        }
        catch (Exception)
        {
            // Indexing is best-effort; never let it interrupt writing.
        }
    }

    private async Task SetModeAsync(TutorMode mode)
    {
        if (_currentPage is null || _currentPage.TutorMode == mode)
        {
            return;
        }

        _currentPage.TutorMode = mode;
        await _host.Pages.UpdatePageAsync(_currentPage.Model);

        if (mode != TutorMode.Live)
        {
            _tutor.CancelPending();
        }

        if (mode == TutorMode.Practice)
        {
            // Silence means silence: hide anything already on screen.
            PageEditor.ClearHighlights();
            _feedback.Clear();
            _sessionStart = DateTimeOffset.UtcNow;
        }
        else
        {
            await RefreshFeedbackAsync();
            if (mode == TutorMode.Live)
            {
                SidebarSummary.Text = "He reads the page a moment after you pause.";
            }
        }

        await RefreshReviewAsync();
        UpdateSidebarState();
        UpdateNotices();
    }

    private void SetModeRadio(TutorMode mode)
    {
        ModeLive.IsChecked = mode is TutorMode.Live or TutorMode.Practice;
        ModePractice.IsChecked = mode == TutorMode.Practice;
        ModeReview.IsChecked = mode == TutorMode.Review;
        MenuPracticeMode.IsChecked = mode == TutorMode.Practice;
    }

    /// <summary>
    /// How far back Review looks. Long enough to span a semester's worth of a topic, bounded
    /// because a judgement from a year ago is history rather than evidence about today.
    /// </summary>
    private static readonly TimeSpan ReviewWindow = TimeSpan.FromDays(180);

    private TopicConfidence? _topic;

    /// <summary>
    /// Scores the current topic's skill history. Reads rows already on disk and does arithmetic
    /// over them — no model call, so opening Review is free and can be refreshed on every reply.
    /// </summary>
    private async Task RefreshReviewAsync()
    {
        _skills.Clear();
        _topic = null;

        // Cleared before the mode check, not after: leaving Review is one of the ways these get
        // stale, and a progress bar left over from a topic you are no longer looking at is worse
        // than no bar at all.
        UnlockBlock.Visibility = Visibility.Collapsed;
        SidebarHeading.Visibility = Visibility.Visible;
        SidebarSummary.Visibility = Visibility.Visible;
        ReportCard.Visibility = Visibility.Collapsed;

        if (_currentPage is null || _currentPage.TutorMode != TutorMode.Review)
        {
            return;
        }

        // Review puts nothing on the page — everything it has to say lives in the panel, so
        // entering the mode with the panel shut shows a page indistinguishable from Live.
        SetSidebarVisible(true);

        var now = DateTimeOffset.UtcNow;
        var events = await _host.Tutor.GetSkillEventsAsync(_currentPage.SectionId, now - ReviewWindow);
        var topic = SkillConfidence.Score(events, now);
        _topic = topic;

        // Every meter here is arithmetic over rows already on disk, so showing them early costs
        // nothing. They used to be withheld below the threshold on the grounds that a bar built
        // from one answer reads as a measurement — but the honest fix for that is to say how
        // much is behind it, which the caption above them now does, not to show nothing at all.
        foreach (var score in topic.Skills)
        {
            _skills.Add(new SkillScoreViewModel(score));
        }

        UpdateUnlockBar(topic);

        // The confidence meters are arithmetic over rows already on disk and are always shown.
        // The report is the only part that spends, so it is never written by arriving here —
        // opening the panel to see where you stand should not be able to cost anything.
        await ShowStoredReportAsync(topic);
    }

    /// <summary>
    /// Shows how close the topic is to earning a written report, and disappears once it has.
    /// </summary>
    /// <remarks>
    /// Progress is the WORSE of the two conditions, not their average: the gate needs problems
    /// AND skills, so ten problems on two skills is not five-sixths of the way there — it is
    /// two-thirds, held back by the skills it has not touched. Averaging would show a bar close
    /// to full that never unlocks, which is the one thing a progress bar must not do.
    /// </remarks>
    private void UpdateUnlockBar(TopicConfidence topic)
    {
        if (topic.HasEnoughData)
        {
            UnlockBlock.Visibility = Visibility.Collapsed;
            SidebarHeading.Visibility = Visibility.Visible;
            SidebarSummary.Visibility = Visibility.Visible;
            return;
        }

        // Each half fills independently and stops at its own end. Clamping is the point: pass
        // the problem count and that bar is full, but the report still will not unlock, and a
        // bar that kept growing would say the opposite.
        Fill(ProblemsFill, ProblemsGap, topic.TotalAttempts, SkillConfidence.MinimumAttempts);
        Fill(SkillsFill, SkillsGap, topic.Skills.Count, SkillConfidence.MinimumJudgedSkills);

        Count(ProblemsDone, topic.TotalAttempts, SkillConfidence.MinimumAttempts);
        ProblemsOf.Text = $" of {SkillConfidence.MinimumAttempts} problems · ";
        Count(SkillsDone, topic.Skills.Count, SkillConfidence.MinimumJudgedSkills);
        SkillsOf.Text = $" of {SkillConfidence.MinimumJudgedSkills} skills";

        // The bar takes the header's space, so the line it replaces goes: the topic's name is
        // already in the crumb at the top left and saying it twice bought nothing.
        UnlockBlock.Visibility = Visibility.Visible;
        SidebarHeading.Visibility = Visibility.Collapsed;
        SidebarSummary.Visibility = Visibility.Collapsed;
    }

    /// <summary>Sizes one half of the unlock bar, clamped so it cannot cross the middle.</summary>
    private static void Fill(ColumnDefinition done, ColumnDefinition rest, int have, int need)
    {
        var fraction = need <= 0 ? 1 : Math.Clamp((double)have / need, 0, 1);
        done.Width = new GridLength(fraction, GridUnitType.Star);
        rest.Width = new GridLength(1 - fraction, GridUnitType.Star);
    }

    /// <summary>
    /// Writes a count, accented once it is past what the gate asks for.
    /// </summary>
    /// <remarks>
    /// The bar stops at full, so beyond the target it can no longer show anything — six of five
    /// problems and five of five draw the same. The number says what the bar cannot.
    /// </remarks>
    private void Count(System.Windows.Documents.Run run, int have, int need)
    {
        run.Text = have.ToString(System.Globalization.CultureInfo.InvariantCulture);
        run.SetResourceReference(
            System.Windows.Documents.TextElement.ForegroundProperty,
            have >= need ? "Accent" : "TextBody");
    }

    /// <summary>
    /// Shows whatever report is already on disk, and offers the button that writes a new one.
    /// </summary>
    /// <remarks>
    /// Reads only. Opening Review used to write a report the moment the topic crossed the
    /// threshold, which made the most expensive call in the app a side effect of looking at a
    /// panel. Now the meters answer "where do I stand" for free, and spending is a press.
    /// </remarks>
    private async Task ShowStoredReportAsync(TopicConfidence topic)
    {
        ReportCard.Visibility = Visibility.Collapsed;
        ReportButton.Visibility = Visibility.Collapsed;

        if (_currentPage is null)
        {
            return;
        }

        var report = await _host.Tutor.GetSkillReportAsync(_currentPage.SectionId);

        // The report supersedes the meters rather than joining them. It is written from the
        // same numbers and now names each skill in prose, so keeping the bars underneath left
        // the panel saying everything twice — once as a row of bars nobody can act on, once as
        // the sentences that say what to do about them.
        var written = report is not null && !string.IsNullOrWhiteSpace(report.Content);
        SkillList.Visibility = written ? Visibility.Collapsed : Visibility.Visible;

        if (written && report is not null)
        {
            ReportText.Text = report.Content;
            ReportStamp.Text = report.AttemptsAtGeneration == topic.TotalAttempts
                ? $"written from {report.AttemptsAtGeneration} problems"
                : $"written from {report.AttemptsAtGeneration} problems; {topic.TotalAttempts - report.AttemptsAtGeneration} more since";
            ReportCard.Visibility = Visibility.Visible;
        }

        if (!topic.HasEnoughData)
        {
            return;
        }

        // Named for what pressing it does now: a first report, or a re-read of numbers that
        // have moved. A report with nothing new behind it would say the same thing again.
        var newAttempts = topic.TotalAttempts - (report?.AttemptsAtGeneration ?? 0);
        ReportButton.Content = report is null
            ? "Write the report"
            : $"Rewrite it — {newAttempts} more problem(s) since";
        ReportButton.IsEnabled = report is null || newAttempts > 0;
        ReportButton.Visibility = Visibility.Visible;
    }

    /// <summary>Writes a fresh report because the student asked for one.</summary>
    private async Task WriteReportAsync()
    {
        if (_currentPage is null || _topic is not { } topic || _reportInFlight)
        {
            return;
        }

        _reportInFlight = true;
        ReportButton.IsEnabled = false;
        ReportButton.Content = "Writing…";
        try
        {
            var name = _notebooks.SelectMany(n => n.Sections)
                .FirstOrDefault(s => s.Id == _currentPage.SectionId)?.Name;

            await _tutor.WriteTopicReportAsync(
                _currentPage.SectionId,
                string.IsNullOrWhiteSpace(name) ? "this topic" : name,
                topic);

            await ShowStoredReportAsync(topic);
            await UpdateUsageAsync();
        }
        finally
        {
            _reportInFlight = false;
        }
    }

    private async Task RefreshFeedbackAsync()
    {
        _feedback.Clear();

        // Page checking off is the same situation as Practice: nothing may mark the page.
        // Rows already in the database do not stop existing when the scanner is switched off,
        // and hiding only the sidebar list left them still drawn over the ink — a highlight
        // pointing at an error, with no list entry to open and nothing able to clear it. The
        // student cannot tell that from a live finding.
        if (_currentPage is null
            || _currentPage.TutorMode == TutorMode.Practice
            || !_host.Settings.VisionEnabled)
        {
            PageEditor.ClearHighlights();
            UpdateSidebarState();
            return;
        }

        // Drop stale blank marks left over from a previous session before drawing.
        await _tutor.SyncFeedbackWithInkAsync(_currentPage.Id);

        var items = await _host.Tutor.GetFeedbackAsync(_currentPage.Id);

        for (var index = 0; index < items.Count; index++)
        {
            _feedback.Add(new FeedbackItemViewModel(items[index], index + 1));
        }

        PageEditor.ShowHighlights(items);

        // If the active anchor just vanished (fixed in place, or dismissed some other way),
        // clear it here and stop — do NOT eagerly reassign it to whatever is now first.
        // ResolveAnchorForQuestion already falls back to the first still-open finding for a
        // vanished anchor, and it does so at the moment the student actually sends the next
        // message. Reassigning it here instead runs BEFORE that message exists, so a "next"
        // typed right after a fix would resolve one past this eager reassignment — landing on
        // the finding *after* the one the student actually meant to move to, silently
        // skipping it. Leaving this null until the next message is what lets a vanished
        // anchor and an explicit "next" compose correctly instead of double-advancing.
        if (_activeAnchor is not null
            && items.All(item => item.Id != _activeAnchor.Id))
        {
            _activeAnchor = null;
            _activeThreadId = 0;
            _socraticSessionTurn = 0;
            _ladderSkill = string.Empty;
        }

        UpdateSidebarState();
    }

    /// <summary>Keeps the findings list, the counts and the sealed card consistent.</summary>
    private void UpdateSidebarState()
    {
        var practice = _currentPage?.TutorMode == TutorMode.Practice;
        var review = _currentPage?.TutorMode == TutorMode.Review;

        SealedCard.Visibility = practice ? Visibility.Visible : Visibility.Collapsed;

        // The two list panels share one slot and are mutually exclusive. Findings answer
        // "what is wrong on this page"; Review answers "how is this topic going" — and the
        // findings side has nothing to say at all while page checking is off, which is why
        // its visibility is decided here rather than once at startup.
        ReviewPanel.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        ApplySidebarLayout();
        FindingsPanel.Visibility = review || !_host.Settings.VisionEnabled
            ? Visibility.Collapsed
            : Visibility.Visible;

        ReviewEmptyHint.Visibility = review && _skills.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        NoFeedbackHint.Visibility = !practice && !review && _feedback.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var major = _feedback.Count(f => f.Feedback.Severity == FeedbackSeverity.Major);
        var minor = _feedback.Count - major;

        SeverityPills.Visibility = !practice && !review && _feedback.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        MajorCount.Text = major == 1 ? "1 major" : $"{major} major";
        MinorCount.Text = minor == 1 ? "1 minor" : $"{minor} minor";

        if (practice)
        {
            SidebarSummary.Text = "Practice. Nothing is read and nothing is sent.";
        }
        else if (review)
        {
            UpdateReviewSummary();
        }
        else if (_feedback.Count == 0)
        {
            SidebarSummary.Text = _host.Settings.VisionEnabled
                ? "Write something and he'll take a look."
                : "Ask him about anything on the page.";
        }
    }

    /// <summary>
    /// Names the topic being reviewed, because the section IS the topic and getting that
    /// wrong — reviewing a section called "First Section" holding four unrelated units — is
    /// the one mistake the student cannot see from the meters themselves.
    /// </summary>
    private void UpdateReviewSummary()
    {
        var name = _notebooks.SelectMany(n => n.Sections)
            .FirstOrDefault(s => s.Id == _currentPage?.SectionId)?.Name;
        var topicName = string.IsNullOrWhiteSpace(name) ? "This topic" : name;

        if (_topic is not { } topic || topic.Skills.Count == 0)
        {
            SidebarSummary.Text = $"{topicName} — nothing judged here yet.";
            ReviewEmptyText.Text = "ask him about your working and this fills in";
            return;
        }

        var skills = topic.Skills.Count == 1 ? "1 skill" : $"{topic.Skills.Count} skills";
        var problems = topic.TotalAttempts == 1 ? "1 problem" : $"{topic.TotalAttempts} problems";

        // Below the threshold the meters are a preview, and saying so is what keeps them honest:
        // the bars are real arithmetic on real work, there is just not enough of it yet for the
        // written report to be worth paying for.
        SidebarSummary.Text = topic.HasEnoughData
            ? $"{topicName} — {skills} from {problems}, weakest first."
            : $"{topicName} — early days: {skills} from {problems}. "
              + $"The written report starts at {SkillConfidence.MinimumAttempts} problems "
              + $"across {SkillConfidence.MinimumJudgedSkills} skills.";
    }

    /// <summary>
    /// Keeps the empty-findings panel honest about whether anything is actually watching.
    /// </summary>
    private void ApplyVisionAvailability()
    {
        if (_host.Settings.VisionEnabled)
        {
            return;
        }

        // FindingsPanel visibility is decided in UpdateSidebarState, which also has to weigh
        // Review's claim on the same slot; setting it once here would win the startup race and
        // then never be revisited.
        SeverityPills.Visibility = Visibility.Collapsed;

        // Whatever the scanner last said is now frozen there forever: that line is only ever
        // written by a scan, and no scan will run again to replace it. So a "budget spent" or
        // "check failed" warning from before page checking was turned off would sit in the
        // status bar for the rest of the app's life, describing a subsystem that no longer
        // runs. Clear it once here, at the point the decision is applied.
        TutorStatus.Text = string.Empty;
    }

    private void OnFeedbackProduced(object? sender, TutorFeedbackBatch batch) =>
        Dispatcher.Invoke(async () =>
        {
            if (_currentPage?.Id != batch.PageId || _currentPage.TutorMode == TutorMode.Practice)
            {
                return;
            }

            await RefreshFeedbackAsync();

            if (!string.IsNullOrWhiteSpace(batch.Summary))
            {
                SidebarSummary.Text = batch.Summary;
            }

            await UpdateUsageAsync();
        });

    // Live-check progress used to surface as a sidebar row (Reading your page…/Pen down,
    // waiting…) plus a comet sweep and countdown bar on the canvas itself. Both reflowed the
    // sidebar and animated on top of the page every time ink changed — distracting during
    // the exact moment you're writing — so that whole feedback layer was removed. The
    // coordinator's debounce/check logic underneath is untouched; only the UI mirror of it
    // is gone. TutorStatus.Text below still reports state, just without the layout churn.
    private void OnTutorStatusChanged(object? sender, TutorStatus status) =>
        Dispatcher.Invoke(() => TutorStatus.Text = status.Message);

    private async Task ReviewPageAsync()
    {
        if (_currentPage is null)
        {
            NoticeWindow.Tell(this, "Nothing to review", "Open a page first.");
            return;
        }

        if (_currentPage.TutorMode == TutorMode.Practice)
        {
            var answer = NoticeWindow.Confirm(
                this,
                "End practice and check this page?",
                "The page is in Practice mode, so nothing has been read yet.",
                "Check it now");

            if (!answer)
            {
                return;
            }

            SetModeRadio(TutorMode.Review);
            await SetModeAsync(TutorMode.Review);
        }

        await SaveCurrentPageAsync();
        var review = await _tutor.ReviewPageAsync(_currentPage.Id);
        await RefreshFeedbackAsync();
        await UpdateUsageAsync();

        // Only set when WeaknessAggregator found a topic recurring in this page's history
        // (active findings and ones already fixed alike) — nothing recurring yet just
        // means the usual badge refresh above, no chat opens.
        if (review is { Thread: not null, Opening: not null })
        {
            await SwitchToThreadAsync(review.Thread, anchor: null, "Reviewing your work", expanded: true);
        }
        else if (review.Findings.Count == 0)
        {
            // Nothing drew on the page (no findings) and no chat opened, so without an
            // explicit word here a clean page, an offline queue, and a blocked budget all
            // look identical to a click that silently did nothing. review.Message already
            // names whichever of those actually happened.
            NoticeWindow.Tell(this, "Review", review.Message);
        }
    }

    private async Task ShowSessionReportAsync()
    {
        var report = await _tutor.SummarizeSessionAsync(_sessionStart);
        await UpdateUsageAsync();

        new TextReportWindow("Session report", report) { Owner = this }.ShowDialog();
    }

    private async Task FlushQueueAsync()
    {
        var processed = await _tutor.FlushPendingJobsAsync();
        TutorStatus.Text = processed == 0 ? "Nothing queued." : $"Sent {processed} queued check(s).";
        await RefreshFeedbackAsync();
        await UpdateUsageAsync();
    }

    private async Task OpenThreadAsync(TutorFeedback feedback)
    {
        var thread = await _host.Tutor.GetOrCreateThreadAsync(
            feedback.PageId, feedback.Id, feedback.Label, ThreadKind.General, AppSettings.AppStarted);
        await SwitchToThreadAsync(thread, feedback, feedback.Label);
    }

    /// <summary>
    /// The chat thread for when nothing is flagged — no anchor, so no per-finding crop
    /// either; <see cref="TutorCoordinator.AskAsync"/> falls back to a readable crop of the
    /// whole page whenever it is asked with a null anchor.
    /// </summary>
    private async Task OpenPageThreadAsync()
    {
        if (_currentPage is null)
        {
            return;
        }

        var thread = await _host.Tutor.GetOrCreateThreadAsync(
            _currentPage.Id, null, "Page questions", ThreadKind.General, AppSettings.AppStarted);
        await SwitchToThreadAsync(thread, anchor: null, "Nothing flagged — ask about anything on the page.");
    }

    /// <summary>
    /// Shared by every chat entry point (a flagged finding, general page questions, a
    /// Review-mode weakness session): points the panel at a thread and replays its full
    /// history — a freshly-seeded Review thread's opening message is already persisted by
    /// the time this runs, so the replay picks it up with no separate append needed.
    /// </summary>
    private async Task SwitchToThreadAsync(
        TutorThread thread,
        TutorFeedback? anchor,
        string sidebarSummary,
        bool expanded = false)
    {
        _activeAnchor = anchor;
        _activeThreadId = thread.Id;
        _socraticSessionTurn = 0;
        _ladderSkill = string.Empty;

        await ClearChatAsync();
        foreach (var message in await _host.Tutor.GetMessagesAsync(thread.Id))
        {
            await AppendChatMessageAsync(message);
        }

        SidebarSummary.Text = sidebarSummary;
        SetSidebarVisible(true, expanded);
        _ = RunComposerScriptAsync("window.composer.focus()");
    }

    // Guards against overlapping turns: the Enter-key handler calls SendChatAsync directly, so
    // disabling SendButton alone wouldn't stop a second Enter press from starting a second turn
    // that streams into the same bubble as the first. One turn at a time, checked before
    // anything else runs.
    private bool _chatTurnInFlight;

    /// <summary>Guards Shift+R against a second press while the first mark is still in flight.</summary>
    private bool _markInFlight;

    /// <summary>Guards the report button against a second press while one is being written.</summary>
    private bool _reportInFlight;

    /// <summary>Whether the transcript holds anything, which is what decides the sidebar's shape.</summary>
    private bool _chatHasContent;

    private async Task SendChatAsync()
    {
        if (_currentPage is null || _chatTurnInFlight)
        {
            return;
        }

        var text = (await ReadComposerTextAsync()).Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!await ConfirmSpendingPastTodayAsync())
        {
            return;
        }

        _chatTurnInFlight = true;
        SendButton.IsEnabled = false;
        await RunComposerScriptAsync("window.composer.setEnabled(false)");
        try
        {
            // Figure out which finding (if any) this message is actually about — an explicit
            // number, "next", or otherwise whatever's already active — before anything else, so
            // the thread we post to and the image the tutor attaches both match it.
            var anchor = ResolveAnchorForQuestion(text);

            if (anchor is not null)
            {
                if (_activeThreadId == 0 || _activeAnchor is null || _activeAnchor.Id != anchor.Id)
                {
                    await OpenThreadAsync(anchor);
                }
            }
            else if (_activeAnchor is not null || _activeThreadId == 0)
            {
                await OpenPageThreadAsync();
            }

            await RunComposerScriptAsync("window.composer.clear()");
            await AppendChatMessageAsync(new TutorMessage
            {
                Role = MessageRole.User,
                Content = text,
            });

            _socraticSessionTurn++;

            // Open an empty bubble up front so there is something on screen to fill, rather than a
            // blank panel until the whole reply lands. Any residue from a previous turn that
            // failed mid-stream is dropped here rather than being prepended to this one.
            lock (_streamGate)
            {
                _streamPending.Clear();
            }

            await RunChatScriptAsync("beginStreamingMessage()");

            TutorMessage? reply = null;
            try
            {
                reply = await _tutor.AskAsync(
                    _currentPage.Id,
                    _activeThreadId,
                    text,
                    _activeAnchor,
                    ladderTurn: _socraticSessionTurn,
                    onDelta: OnReplyDelta,
                    sectionId: _currentPage.SectionId);
            }
            finally
            {
                await DrainReplyStreamAsync();

                // A refused turn (over budget) or a thrown one must not leave an empty grey box
                // sitting in the transcript. Otherwise hand the finished bubble the authoritative
                // text, so what is on screen is exactly what was saved.
                await RunChatScriptAsync(
                    reply is null
                        ? "cancelStreamingMessage()"
                        : $"finishStreamingMessage({JsonSerializer.Serialize(reply.Content)})");
            }

            ResetLadderIfWorkMovedOn();
            await UpdateUsageAsync();

            // The reply just banked a verdict. Refreshing here is what makes Review feel live
            // rather than something that catches up the next time the mode is toggled.
            await RefreshReviewAsync();
            UpdateSidebarState();
        }
        finally
        {
            SendButton.IsEnabled = true;
            _chatTurnInFlight = false;
            await RunComposerScriptAsync("window.composer.setEnabled(true)");
            await RunComposerScriptAsync("window.composer.focus()");
        }
    }

    /// <summary>
    /// Brings up the KaTeX-capable chat surface: a WebView2 pointed at a virtual host mapped
    /// to <c>Assets/chat</c> on disk (no CDN — the app stays offline-capable for everything
    /// except the tutor calls themselves). Awaited once from the constructor and stashed in
    /// <see cref="_chatWebViewReady"/> so every render call can just await that instead of
    /// re-deriving readiness.
    /// </summary>
    private async Task InitializeChatWebViewAsync()
    {
        // Both surfaces are served from the same mapped folder, so the composer's MathLive
        // bundle and the transcript's KaTeX are equally offline.
        await Task.WhenAll(
            NavigateChatSurfaceAsync(ChatWebView, "chat.html"),
            NavigateChatSurfaceAsync(ChatInputView, "composer.html"));
    }

    private static async Task NavigateChatSurfaceAsync(
        Microsoft.Web.WebView2.Wpf.WebView2 view,
        string page)
    {
        await view.EnsureCoreWebView2Async();

        var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "chat");
        view.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "chat.notetaker.local", assetsPath, CoreWebView2HostResourceAccessKind.Allow);

        var navigationDone = new TaskCompletionSource();
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) =>
            navigationDone.TrySetResult();

        view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        try
        {
            view.CoreWebView2.Navigate($"https://chat.notetaker.local/{page}");
            await navigationDone.Task;
        }
        finally
        {
            view.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    /// <summary>
    /// Handles the composer page's postMessage bridge — Enter-to-send, and the placeholder's
    /// empty/non-empty state.
    /// </summary>
    /// <remarks>
    /// The payload is JSON this app authored and is parsed as data, never evaluated. A page
    /// served from the mapped asset folder is the only thing that can post here.
    /// </remarks>
    private async void OnComposerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string kind;
        var pointerType = string.Empty;
        var height = 0;
        try
        {
            using var payload = JsonDocument.Parse(e.TryGetWebMessageAsString() ?? "{}");
            if (!payload.RootElement.TryGetProperty("type", out var type))
            {
                return;
            }

            kind = type.GetString() ?? string.Empty;

            if (payload.RootElement.TryGetProperty("pointerType", out var pointer))
            {
                pointerType = pointer.GetString() ?? string.Empty;
            }

            if (payload.RootElement.TryGetProperty("height", out var reported)
                && reported.TryGetInt32(out var value))
            {
                height = value;
            }
        }
        catch (JsonException)
        {
            return;
        }

        switch (kind)
        {
            case "submit":
                await SendChatAsync();
                break;

            case "pointer-focus" when pointerType is "touch" or "pen":
                ShowTouchKeyboard();
                break;

            // A WebView2 does not size itself to its content, so the composer's page measures
            // itself and says how tall it needs to be. Clamped here rather than trusted: the
            // page is local and not hostile, but a runaway value would push the transcript off
            // the panel, and the floor keeps the box usable if a measurement ever comes back 0.
            case "resize":
                ChatInputView.Height = Math.Clamp(height, ComposerMinHeight, ComposerMaxHeight);
                break;
        }
    }

    /// <summary>One line of text plus its padding — the composer with nothing in it.</summary>
    private const double ComposerMinHeight = 38;

    /// <summary>
    /// Enough for a few lines of question and a two-line typeset preview above it. Past this
    /// the composer would be taking room the transcript needs more.
    /// </summary>
    private const double ComposerMaxHeight = 190;

    // ── Making room for the touch keyboard ───────────────────────────────────────────────
    //
    // The keyboard is an always-on-top window laid over the bottom of the screen, so it
    // covers the composer — the one control the student just tapped. Rather than move the
    // window (it is usually maximised, and un-maximising to slide it up would be visibly
    // worse), the whole interface is inset from the bottom by however much is actually
    // covered, which lifts the composer clear while the page keeps its full width.
    //
    // Polled, because there is no notification to subscribe to from Win32 for a keyboard that
    // belongs to another process. Background priority and a cheap FindWindow keeps it well
    // clear of pen input, and it only runs while the keyboard is up.
    private DispatcherTimer? _keyboardWatcher;

    private void ShowTouchKeyboard()
    {
        TouchKeyboard.Show();

        _keyboardWatcher ??= CreateKeyboardWatcher();
        _keyboardWatcher.Start();
    }

    private DispatcherTimer CreateKeyboardWatcher()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };

        var idleTicks = 0;

        timer.Tick += (_, _) =>
        {
            var inset = MeasureKeyboardInset();
            SetKeyboardInset(inset);

            // Stand down once the keyboard has been gone for a couple of seconds, so the app
            // is not polling forever after a single tap.
            idleTicks = inset > 0 ? 0 : idleTicks + 1;
            if (idleTicks > 10)
            {
                timer.Stop();
                idleTicks = 0;
            }
        };

        return timer;
    }

    /// <summary>Keyboard overlap in DIPs, which is what a WPF Thickness expects.</summary>
    private double MeasureKeyboardInset()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        var devicePixels = TouchKeyboard.OccludedHeight(handle);
        if (devicePixels <= 0)
        {
            return 0;
        }

        // GetWindowRect answers in device pixels; WPF lays out in DIPs. On this machine those
        // differ, so skipping the conversion would inset by the wrong amount at any scaling
        // other than 100%.
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        var dips = transform is { } matrix ? devicePixels * matrix.M22 : devicePixels;

        // Never surrender more than half the window. If the measurement is ever wrong, a
        // cramped interface is recoverable; one whose controls are all pushed off the top of
        // the screen is not.
        return Math.Clamp(dips, 0, ActualHeight / 2);
    }

    private void SetKeyboardInset(double inset)
    {
        if (Math.Abs(WindowFrame.Margin.Bottom - inset) < 0.5)
        {
            return; // no layout pass for sub-pixel jitter
        }

        WindowFrame.Margin = new Thickness(0, 0, 0, inset);
    }

    /// <summary>Reads what the student has composed, as text with math in <c>$…$</c>.</summary>
    private async Task<string> ReadComposerTextAsync()
    {
        await _chatWebViewReady;

        // ExecuteScriptAsync hands back the result JSON-encoded, so a plain string arrives
        // wrapped in quotes with its backslashes escaped — and LaTeX is mostly backslashes.
        // Deserializing rather than trimming the quotes is what keeps \frac from becoming
        // "rac".
        var raw = await ChatInputView.CoreWebView2.ExecuteScriptAsync("window.composer.getText()");
        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private async Task RunComposerScriptAsync(string script)
    {
        await _chatWebViewReady;
        await ChatInputView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task ClearChatAsync()
    {
        _chatHasContent = false;
        ApplySidebarLayout();

        await _chatWebViewReady;
        await ChatWebView.CoreWebView2.ExecuteScriptAsync("clearMessages()");
    }

    /// <summary>
    /// Decides how the sidebar divides between the panel above and the transcript below.
    /// </summary>
    /// <remarks>
    /// Review and the tutor want opposite shapes. The tutor is a conversation, so the transcript
    /// takes the room and the panel above it is capped. Review is a page of standings that
    /// nobody talks to until they have read it — and under the tutor's shape it lost: a report
    /// and four meters squeezed into 300px, the report cut off mid-sentence, the meters pushed
    /// out of sight entirely, all so an empty transcript could hold half the panel.
    ///
    /// So Review takes the whole sidebar until there is something in the transcript, leaving the
    /// composer docked at the bottom where it always was. Ask a question and the transcript
    /// appears, taking the room back.
    /// </remarks>
    private void ApplySidebarLayout()
    {
        var reviewOnly = ReviewPanel.Visibility == Visibility.Visible && !_chatHasContent;

        PanelRow.Height = reviewOnly ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        PanelRow.MaxHeight = reviewOnly ? double.PositiveInfinity : 300;
        ChatRow.Height = reviewOnly ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ChatWebView.Visibility = reviewOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Renders one message into the chat surface. Content is passed as a JSON-encoded string
    /// literal — never string-concatenated into the script — so nothing the tutor or the
    /// student typed can break out of the argument and run as script; the page's own
    /// <c>addMessage</c> then sets it via <c>textContent</c>, which the browser escapes for
    /// display, before KaTeX renders any <c>$...$</c> math it finds in that safe text.
    /// </summary>
    private async Task AppendChatMessageAsync(TutorMessage message)
    {
        // The first message is what turns Review's full-height standings back into a
        // conversation, so the transcript has somewhere to appear.
        if (!_chatHasContent)
        {
            _chatHasContent = true;
            ApplySidebarLayout();
        }

        await _chatWebViewReady;

        var role = message.Role == MessageRole.User ? "user" : "tutor";
        var script = $"addMessage({JsonSerializer.Serialize(role)}, {JsonSerializer.Serialize(message.Content)})";
        await ChatWebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    // ── Streaming a tutor reply into the chat surface ────────────────────────────────
    //
    // Deltas arrive on a background thread from the transport, but ExecuteScriptAsync must be
    // called from the UI thread, and two of them awaited concurrently could land out of order
    // — which in a text stream means scrambled words. So deltas accumulate behind a lock and a
    // single pump drains them: while a flush is in flight, new text just piles up, and the
    // pump loops until the buffer is empty before standing down. One writer, strict order, and
    // one script call per frame's worth of text instead of one per token.
    private readonly object _streamGate = new();
    private readonly StringBuilder _streamPending = new();
    private bool _streamPumpRunning;

    private void OnReplyDelta(string delta)
    {
        lock (_streamGate)
        {
            _streamPending.Append(delta);
            if (_streamPumpRunning)
            {
                return; // the running pump will pick this up before it exits
            }

            _streamPumpRunning = true;
        }

        // Background priority, like every other non-interactive dispatcher op in this app:
        // rendering tutor text must never cut in front of queued pen input.
        _ = Dispatcher.InvokeAsync(
            async () =>
            {
                while (true)
                {
                    string chunk;
                    lock (_streamGate)
                    {
                        chunk = _streamPending.ToString();
                        _streamPending.Clear();

                        if (chunk.Length == 0)
                        {
                            _streamPumpRunning = false;
                            return;
                        }
                    }

                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                        $"appendToStreamingMessage({JsonSerializer.Serialize(chunk)})");
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>Waits for the pump to drain, so the final render sees the whole reply.</summary>
    private async Task DrainReplyStreamAsync()
    {
        while (true)
        {
            lock (_streamGate)
            {
                if (!_streamPumpRunning && _streamPending.Length == 0)
                {
                    return;
                }
            }

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private async Task RunChatScriptAsync(string script)
    {
        await _chatWebViewReady;
        await ChatWebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    /// <summary>
    /// Decides which finding (if any) this message is about, via <see cref="ChatAnchorResolver"/>
    /// — see that class for why resolving fresh against the live list every time is what
    /// keeps "next" honest when the count changes mid-conversation.
    /// </summary>
    private TutorFeedback? ResolveAnchorForQuestion(string text)
    {
        if (_feedback.Count == 0)
        {
            return null;
        }

        var ordered = _feedback.OrderBy(f => f.Number).ToList();
        var orderedIds = ordered.Select(f => f.Feedback.Id).ToList();

        var resolvedId = ChatAnchorResolver.Resolve(text, orderedIds, _activeAnchor?.Id);
        return resolvedId is null
            ? null
            : ordered.First(f => f.Feedback.Id == resolvedId).Feedback;
    }

    private async Task UpdateUsageAsync()
    {
        // The bar measures today against today's derived share, not against the month: a month
        // bar would sit near empty for weeks and say nothing about whether there is room for the
        // next question, which is the only thing being asked of it mid-session.
        var now = DateTimeOffset.UtcNow;
        var summary = await _host.Usage.GetSummaryAsync(BudgetDay.StartOf(now));

        // The ceiling, not the bare share: it carries the day's floor and any borrow, so the bar
        // fills to exactly the point where the tutor actually stops.
        var budget = await _tutor.BudgetSnapshotAsync();
        var ceiling = budget.CeilingToday;

        var fraction = ceiling <= 0 ? 1 : Math.Clamp((double)(summary.Cost / ceiling), 0, 1);
        BudgetFill.Width = 70 * fraction;
        BudgetFill.SetResourceReference(
            System.Windows.Controls.Border.BackgroundProperty,
            fraction >= 1 ? "Major" : "Accent");

        UsageStatus.Text = $"budget {fraction * 100:0}%";
        _overBudget = fraction >= 1;
        UpdateNotices();
        await RefreshStreakAsync();
    }

    /// <summary>
    /// Rebuilds the header's week strip: seven bars, Monday first, rising to a peak on today.
    /// </summary>
    /// <remarks>
    /// This reads the day of the week at a glance — Monday peaks on the first bar, Thursday on
    /// the fourth. It replaces a rolling seven-day activity sparkline, which could not do that:
    /// its last bar was always today, so the shape was identical whatever day it happened to be.
    ///
    /// The heights are therefore POSITION, not data. Past days climb toward today on a fixed
    /// wobble so the run has some shape instead of a single spike over six flat stubs, and days
    /// still to come sit faint at the floor. The wobble is a constant, never random: a strip that
    /// rearranged itself on every refresh would read as movement that means something.
    ///
    /// The real call counts have not gone anywhere — they are in the tooltip, per day, and the
    /// budget bar beside it carries the spend. Encoding usage in the heights as well is what made
    /// the old version unreadable as either one thing or the other.
    /// </remarks>
    private async Task RefreshStreakAsync()
    {
        const int days = 7;
        const double peak = 14;
        const double floorHeight = 3;

        // Monday-first, so the strip reads the way a timetable does.
        var today = DateTimeOffset.Now.Date;
        var weekday = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-weekday);

        var cumulative = new int[days + 1];
        for (var i = 0; i <= days; i++)
        {
            var boundary = BudgetDay.StartOf(
                new DateTimeOffset(monday.AddDays(i), DateTimeOffset.Now.Offset));
            cumulative[i] = (await _host.Usage.GetSummaryAsync(boundary)).Calls;
        }

        var counts = new int[days];
        for (var i = 0; i < days; i++)
        {
            counts[i] = Math.Max(0, cumulative[i] - cumulative[i + 1]);
        }

        // Fixed, so the same day always draws the same shape.
        double[] wobble = [0.0, 1.5, -1.0, 1.0, -0.5, 1.5, -1.0];
        var sage = (Color)FindResource("Sage500");

        StreakStrip.Children.Clear();
        for (var i = 0; i < days; i++)
        {
            var isToday = i == weekday;

            double height;
            if (isToday)
            {
                height = peak;
            }
            else if (i > weekday)
            {
                height = floorHeight;
            }
            else
            {
                var climb = weekday == 0 ? 0 : (double)i / weekday;
                height = Math.Clamp(5 + (4.5 * climb) + wobble[i], 4, peak - 2);
            }

            var bar = new Border
            {
                Width = 3,
                Height = height,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, i == days - 1 ? 0 : 3, 0),
                Background = new SolidColorBrush(sage)
                {
                    Opacity = isToday ? 1.0 : i > weekday ? 0.16 : 0.5,
                },
            };

            if (isToday)
            {
                bar.Effect = new DropShadowEffect
                {
                    Color = sage,
                    BlurRadius = 7,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                };
            }

            StreakStrip.Children.Add(bar);
        }

        var names = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        var breakdown = string.Join(
            "  ",
            Enumerable.Range(0, weekday + 1).Select(i => $"{names[i]} {counts[i]}"));

        ToolTipService.SetToolTip(
            StreakStrip,
            $"{names[weekday]} · {counts[weekday]} tutor call{(counts[weekday] == 1 ? "" : "s")} today"
            + $" · {counts.Sum()} this week{Environment.NewLine}{breakdown}");
    }

    private void UpdateConnectivity(bool online)
    {
        ConnectivityStatus.Text = online ? "online" : "offline";
        ConnectivityIcon.Geometry = (Geometry)FindResource(online ? "IconWifi" : "IconWifiOff");
        ConnectivityIcon.Foreground = (Brush)FindResource(online ? "TextMuted" : "Major");
        UpdateNotices();
    }

    /// <summary>
    /// The four banners from the mockup. Each one states what still works rather than only
    /// what does not.
    /// </summary>
    private void UpdateNotices()
    {
        var hasKey = !string.IsNullOrWhiteSpace(
            _host.Secrets.Get(LlmSettings.SecretKey(_host.Settings.VisionProvider)));
        var practice = _currentPage?.TutorMode == TutorMode.Practice;

        KeyNotice.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
        OfflineNotice.Visibility = _host.Connectivity.IsOnline ? Visibility.Collapsed : Visibility.Visible;
        BudgetNotice.Visibility = _overBudget && hasKey ? Visibility.Visible : Visibility.Collapsed;
        PracticeNotice.Visibility = practice ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePageStatus()
    {
        if (_currentPage is null)
        {
            PageStatus.Text = string.Empty;
            PageCrumbText.Text = string.Empty;
            return;
        }

        var section = _notebooks.SelectMany(n => n.Sections)
            .FirstOrDefault(s => s.Id == _currentPage.SectionId);

        if (section is null)
        {
            PageStatus.Text = string.Empty;
            PageCrumbText.Text = _currentPage.Title;
            return;
        }

        var position = section.Pages.IndexOf(_currentPage) + 1;
        PageStatus.Text = position > 0 ? $"page {position} of {section.Pages.Count}" : string.Empty;

        // The lesson, and the page within it only when there is more than one — a "1/1" beside
        // every lesson name is noise on the majority of pages.
        PageCrumbText.Text = section.Pages.Count > 1 && position > 0
            ? $"{section.Name}  ·  {position}/{section.Pages.Count}"
            : section.Name;
    }

    /// <summary>
    /// <paramref name="expanded"/> is only ever true for a Review session — the "sit down
    /// and work through it" moment, wide enough to read a holistic summary while the page
    /// stays usable alongside it. Every other opener (a flagged badge, "ask about the
    /// page", Ctrl+T) and every close pass false, snapping back to the compact width —
    /// expansion is tied to how Review opened it, not a state that lingers after you
    /// move on to something else.
    /// </summary>
    private void SetSidebarVisible(bool visible, bool expanded = false)
    {
        AsideToggle.IsChecked = visible;
        MenuShowAside.IsChecked = visible;

        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (visible && expanded)
        {
            PageColumn.Width = new GridLength(2, GridUnitType.Star);
            SidebarColumn.Width = new GridLength(3, GridUnitType.Star);
        }
        else
        {
            PageColumn.Width = new GridLength(1, GridUnitType.Star);
            SidebarColumn.Width = visible ? new GridLength(324) : new GridLength(0);
        }
    }

    private void SetNavVisible(bool visible)
    {
        NavToggle.IsChecked = visible;
        NavSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NavColumn.Width = visible ? new GridLength(250) : new GridLength(0);
    }

    private void ApplyTool()
    {
        var tool = ToolHighlighter.IsChecked == true ? InkTool.Highlighter
            : ToolEraser.IsChecked == true ? InkTool.Eraser
            : ToolSelect.IsChecked == true ? InkTool.Select
            : InkTool.Pen;

        var palette = ActivePalette;
        var index = Math.Clamp(_swatchIndex, 0, palette.Length - 1);
        PageEditor.SetTool(tool, palette[index].Colour, WidthSlider.Value);
    }

    private void StepZoom(double factor)
    {
        // Anchored on the middle of the viewport so keyboard and button zoom feel like pinch.
        var anchor = new Point(PageEditor.ActualWidth / 2, PageEditor.ActualHeight / 2);
        PageEditor.SetZoomAt(PageEditor.ZoomLevel * factor, anchor);
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = $"{PageEditor.ZoomLevel * 100:0}%";

    private void FitZoom()
    {
        var available = PageEditor.ActualHeight - 48;
        if (available > 0)
        {
            PageEditor.ZoomLevel = available / PageGeometry.Height;
            PageEditor.CenterPage();
        }
    }

    private async Task AddNotebookAsync()
    {
        var name = PromptWindow.Ask(this, "New notebook", "Name", "Notebook");
        if (name is null)
        {
            return;
        }

        await _host.Notebooks.CreateNotebookAsync(name);
        await ReloadNavigationAsync(_currentPage?.Id);
    }

    private async Task AddSectionAsync()
    {
        var notebookId = CurrentNotebookId();
        if (notebookId is null)
        {
            NoticeWindow.Tell(this, "Pick a notebook first.", "A section has to live inside one.");
            return;
        }

        var name = PromptWindow.Ask(this, "New section", "Name", "Section");
        if (name is null)
        {
            return;
        }

        await _host.Notebooks.CreateSectionAsync(notebookId.Value, name);
        await ReloadNavigationAsync(_currentPage?.Id);
    }

    private async Task AddPageAsync()
    {
        var sectionId = CurrentSectionId();
        if (sectionId is null)
        {
            NoticeWindow.Tell(this, "Pick a section first.", "A page has to live inside one.");
            return;
        }

        var page = await _host.Pages.CreatePageAsync(sectionId.Value, $"Page {DateTime.Now:MMM d HH:mm}");
        await ReloadNavigationAsync(page.Id);
    }

    private async Task RenamePageAsync()
    {
        if (_currentPage is null)
        {
            return;
        }

        var title = PromptWindow.Ask(this, "Rename page", "Title", _currentPage.Title);
        if (title is null)
        {
            return;
        }

        _currentPage.Title = title;
        await _host.Pages.UpdatePageAsync(_currentPage.Model);
        Title = $"NoteTaker — {title}";
    }

    private async Task DeletePageAsync()
    {
        if (_currentPage is null)
        {
            return;
        }

        if (!NoticeWindow.Confirm(
                this,
                $"Delete \"{_currentPage.Title}\"?",
                "The page and its ink go with it. This can't be undone.",
                "Delete it"))
        {
            return;
        }

        await _host.Pages.DeletePageAsync(_currentPage.Id);
        _currentPage = null;
        _dirty = false;
        await ReloadNavigationAsync();
    }

    private async Task AttachPdfAsync()
    {
        if (_currentPage is null)
        {
            return;
        }

        var dialog = new OpenFileDialog { Filter = "PDF documents (*.pdf)|*.pdf" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var pageCount = PdfBackgroundService.GetPageCount(dialog.FileName);
        if (pageCount == 0)
        {
            NoticeWindow.Tell(this, "That PDF wouldn't open.", "Try exporting it again from wherever it came from.");
            return;
        }

        var index = 0;
        if (pageCount > 1)
        {
            var answer = PromptWindow.Ask(this, "Attach a PDF", $"Page number (1–{pageCount})", "1");
            if (answer is null)
            {
                return;
            }

            if (int.TryParse(answer, out var parsed))
            {
                index = Math.Clamp(parsed - 1, 0, pageCount - 1);
            }
        }

        _currentPage.Model.PdfPath = dialog.FileName;
        _currentPage.Model.PdfPageIndex = index;
        await _host.Pages.UpdatePageAsync(_currentPage.Model);

        _currentBackground = PdfBackgroundService.Render(dialog.FileName, index);
        PageEditor.PageBackground = _currentBackground;
        _dirty = true;
        await SaveCurrentPageAsync();
    }

    private void Export(string format)
    {
        if (_currentPage is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"{SanitizeFileName(_currentPage.Title)}.{format}",
            Filter = format switch
            {
                "png" => "PNG image (*.png)|*.png",
                "pdf" => "PDF document (*.pdf)|*.pdf",
                _ => "SVG image (*.svg)|*.svg",
            },
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            switch (format)
            {
                case "png":
                    ExportService.SavePng(dialog.FileName, PageEditor.Strokes, _currentBackground);
                    break;
                case "pdf":
                    ExportService.SavePdf(dialog.FileName, PageEditor.Strokes, _currentBackground);
                    break;
                default:
                    ExportService.SaveSvg(dialog.FileName, PageEditor.Strokes);
                    break;
            }

            SaveStatus.Text = $"exported {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            NoticeWindow.Tell(this, "That didn't export.", ex.Message);
        }
    }

    private void OpenSearch(bool showRelated = false)
    {
        var window = new SearchWindow(_host, _currentPage?.Id, showRelated) { Owner = this };
        if (window.ShowDialog() == true && window.SelectedPageId is { } pageId)
        {
            var node = _notebooks.SelectMany(n => n.Sections).SelectMany(s => s.Pages)
                .FirstOrDefault(p => p.Id == pageId);

            if (node is not null)
            {
                node.IsSelected = true;
                _ = OpenPageAsync(node);
            }
        }
    }

    private void OpenPracticeGenerator()
    {
        if (_currentPage is null)
        {
            return;
        }

        new PracticeWindow(_host, _tutor, _currentPage.SectionId) { Owner = this }.ShowDialog();
        _ = UpdateUsageAsync();
    }

    private async Task ReindexAsync()
    {
        SaveStatus.Text = "rebuilding index…";
        var pages = await _host.Pages.GetAllPagesAsync();

        foreach (var page in pages)
        {
            var ink = await _host.Ink.LoadAsync(page.Id);
            if (ink is null || ink.IsfBlob.Length == 0)
            {
                continue;
            }

            try
            {
                var strokes = new StrokeCollection(new MemoryStream(ink.IsfBlob));
                var background = page.PdfPath is { Length: > 0 } path && File.Exists(path)
                    ? PdfBackgroundService.Render(path, page.PdfPageIndex ?? 0)
                    : null;

                var png = PageRenderer.RenderPagePng(strokes, background, 512);
                var text = await InkRecognitionService.RecognizeAsync(strokes);
                await _host.Indexing.IndexPageAsync(page.Id, png, text, ink.Revision);
            }
            catch (Exception)
            {
                // Skip pages that fail rather than aborting the whole rebuild.
            }
        }

        SaveStatus.Text = $"indexed {pages.Count} page(s)";
    }

    /// <summary>
    /// Rebuilds the bitmap used for export and for chat captures: the worksheet with the
    /// placed pictures drawn over it. The on-screen pictures are real elements in the page's
    /// image layer — this is only for the paths that need a single flat image.
    /// </summary>
    private void RefreshBackgroundComposite()
    {
        _currentBackground = PageComposer.Flatten(_pdfBackground, PageEditor.PageImages);
        PdfOnlyBackground();
    }

    /// <summary>The page shows the PDF itself; placed pictures render as their own elements.</summary>
    private void PdfOnlyBackground() => PageEditor.PageBackground = _pdfBackground;

    /// <summary>
    /// Puts a picture on the page. Everything non-ink the student adds goes through here,
    /// so placement, storage and the tutor-visibility rule each live in one place.
    /// </summary>
    private async Task AddImageAsync(ImageSource image, InsertPlacement placement)
    {
        if (_currentPage is null)
        {
            return;
        }

        var bounds = PageComposer.PlacementBounds(placement, image);

        // Centre it on what the student is actually looking at. The placement above is
        // relative to the A4 sheet, which on an infinite canvas can be a long way off-screen
        // — pan somewhere to work and a paste would land back on the sheet, invisible.
        var view = PageEditor.ViewCentre;
        bounds.Location = new Point(view.X - (bounds.Width / 2), view.Y - (bounds.Height / 2));

        // Then offset each new picture down-right of the last, so a second one does not land
        // exactly on top of the first. Inserting a graph over a pasted picture looked like
        // the paste had been deleted — it was still there, perfectly hidden underneath.
        var cascade = PageEditor.PageImages.Count * 48;
        bounds.Offset(cascade, cascade);

        var placed = new PageImage
        {
            PageId = _currentPage.Id,
            Png = PageComposer.ToPng(image),
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
        };

        // Added through the page control so it joins the undo stack; SyncImagesAsync
        // persists it.
        PageEditor.AddImage(placed);

        _dirty = true;
        await SaveCurrentPageAsync();
    }

    /// <summary>
    /// Drags pictures stored miles from the page back onto the sheet.
    /// </summary>
    /// <remarks>
    /// Early paste builds saved sheet-relative coordinates into a world-coordinate field, so
    /// those pictures live around x=90 while the sheet starts near x=99380. They are
    /// invisible, unreachable, and — because the capture rectangle spans everything on the
    /// page — they stretched it across ~100,000 units and crashed the renderer trying to
    /// allocate a bitmap for it. Repairing on load is kinder than asking the student to hunt
    /// for something they cannot see.
    /// </remarks>
    private async Task<IReadOnlyList<PageImage>> RepairStrayImagesAsync(IReadOnlyList<PageImage> images)
    {
        // Generous: a picture may legitimately sit well off the sheet on the open canvas.
        // This only catches ones nowhere near it at all.
        var sane = PageGeometry.SheetBounds;
        sane.Inflate(PageGeometry.Width * 4, PageGeometry.Height * 4);

        foreach (var image in images)
        {
            if (sane.Contains(new Rect(image.X, image.Y, image.Width, image.Height)))
            {
                continue;
            }

            image.X = PageGeometry.OriginX + ((PageGeometry.Width - image.Width) / 2);
            image.Y = PageGeometry.OriginY + ((PageGeometry.Height - image.Height) / 2);
            await _host.Ink.UpdateImageBoundsAsync(image);
        }

        return images;
    }

    /// <summary>
    /// Makes the database match the pictures currently on the page.
    /// </summary>
    /// <remarks>
    /// Undo and redo move pictures on and off the page without going near storage, so the
    /// two drift apart the moment Ctrl+Z touches a paste — and a reload would resurrect a
    /// picture the student had just taken back. Reconciling against what is actually on the
    /// page keeps one source of truth and works no matter which direction history moved in.
    /// </remarks>
    private async Task SyncImagesAsync()
    {
        if (_currentPage is null)
        {
            return;
        }

        var onPage = PageEditor.PageImages;

        foreach (var image in onPage.Where(i => !_persistedImageIds.Contains(i.Id)))
        {
            image.PageId = _currentPage.Id;
            await _host.Ink.AddImageAsync(image);
            _persistedImageIds.Add(image.Id);
        }

        var live = onPage.Select(i => i.Id).ToHashSet();
        foreach (var goneId in _persistedImageIds.Where(id => !live.Contains(id)).ToList())
        {
            await _host.Ink.DeleteImageAsync(goneId);
            _persistedImageIds.Remove(goneId);
        }
    }

    /// <summary>
    /// Pastes a picture from the clipboard onto the page. Never reaches a vision model —
    /// see <see cref="ActiveBackground"/> for where that is enforced.
    /// </summary>
    private async Task PasteImageAsync()
    {
        if (_currentPage is null)
        {
            NoticeWindow.Tell(this, "Nothing to paste onto", "Open a page first.");
            return;
        }

        var image = ReadClipboardImage();
        if (image is null)
        {
            NoticeWindow.Tell(
                this,
                "No picture on the clipboard",
                "Copy an image — a screenshot, or a picture file — and try again.");
            return;
        }

        await AddImageAsync(image, InsertPlacement.Middle);
        TutorStatus.Text = "Pasted a picture — ask the tutor about it in chat.";
    }

    /// <summary>
    /// Bitmap data first, then a copied file: copying a picture in File Explorer puts a path
    /// on the clipboard rather than pixels, and "paste" should mean the same thing either way.
    /// </summary>
    private static ImageSource? ReadClipboardImage()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            {
                bitmap.Freeze();
                return bitmap;
            }

            if (Clipboard.ContainsFileDropList())
            {
                foreach (var path in Clipboard.GetFileDropList())
                {
                    if (path is null || !File.Exists(path))
                    {
                        continue;
                    }

                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp"))
                    {
                        continue;
                    }

                    var decoded = new BitmapImage();
                    decoded.BeginInit();
                    decoded.CacheOption = BitmapCacheOption.OnLoad;
                    decoded.UriSource = new Uri(path);
                    decoded.EndInit();
                    decoded.Freeze();
                    return decoded;
                }
            }
        }
        catch (Exception)
        {
            // Clipboard access races another app holding it open; treat as "nothing to paste".
        }

        return null;
    }

    private async void InsertGraph()
    {
        var window = new GraphWindow { Owner = this };
        if (window.ShowDialog() == true && window.Result is { } image)
        {
            await AddImageAsync(image, window.Placement);
        }
    }

    private async void RunPythonCell()
    {
        var window = new PythonWindow(_host.Settings) { Owner = this };
        if (window.ShowDialog() == true && window.Result is { } image)
        {
            await AddImageAsync(image, window.Placement);
        }
    }

    private async Task ToggleSplitViewAsync()
    {
        if (!MenuSplitView.IsChecked)
        {
            SecondPaneColumn.Width = new GridLength(0);
            SecondPage.Visibility = Visibility.Collapsed;
            PaneSplitter.Visibility = Visibility.Collapsed;
            return;
        }

        var reference = FindReferencePage();
        if (reference is null)
        {
            MenuSplitView.IsChecked = false;
            NoticeWindow.Tell(
                this,
                "There's nothing to compare against.",
                "Add another page to this section and try again.");
            return;
        }

        var ink = await _host.Ink.LoadAsync(reference.Id);
        var strokes = ink is { IsfBlob.Length: > 0 }
            ? new StrokeCollection(new MemoryStream(ink.IsfBlob))
            : new StrokeCollection();

        SecondPage.LoadStrokes(strokes);
        SecondPage.PageBackground = reference.Model.PdfPath is { Length: > 0 } path && File.Exists(path)
            ? PdfBackgroundService.Render(path, reference.Model.PdfPageIndex ?? 0)
            : null;

        // Reference pane only: keeping it read-only avoids ambiguity about which page
        // the tutor and auto-save are acting on.
        SecondPage.SetTool(InkTool.Select, Colors.Black, 2);
        SecondPage.ToolTip = $"Reference: {reference.Title} (read-only)";

        SecondPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        SecondPage.Visibility = Visibility.Visible;
        PaneSplitter.Visibility = Visibility.Visible;
    }

    private PageNode? FindReferencePage()
    {
        if (_currentPage is null)
        {
            return null;
        }

        var section = _notebooks.SelectMany(n => n.Sections)
            .FirstOrDefault(s => s.Id == _currentPage.SectionId);

        return section?.Pages.FirstOrDefault(p => p.Id != _currentPage.Id);
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_host) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _host.ReloadSettings(_snapshots);
            _tutor.FeedbackProduced -= OnFeedbackProduced;
            _tutor.StatusChanged -= OnTutorStatusChanged;

            if (_host.Coordinator is { } coordinator)
            {
                coordinator.FeedbackProduced += OnFeedbackProduced;
                coordinator.StatusChanged += OnTutorStatusChanged;
            }

            ModelStatus.Text = _host.Settings.ChatModel;
            ApplyVisionAvailability();
            _ = UpdateUsageAsync();
        }
    }

    /// <summary>
    /// Stops at the day's ceiling and says, in dollars, what carrying on would cost the days
    /// after it. Returns whether the turn may proceed.
    /// </summary>
    /// <remarks>
    /// The point of the monthly cap is that a quiet day feeds the ones after it. That only
    /// rewards saving if the reverse is visible: without this, one long afternoon silently eats
    /// a week, and the student finds out on Thursday when the tutor has nothing left to give.
    ///
    /// So the day's ceiling is a real stop, and getting past it is a decision made in front of
    /// the numbers — never a slow leak. Borrowing lasts until midnight Pacific and never crosses
    /// the month's own cap, which nothing here can raise.
    /// </remarks>
    /// <summary>
    /// Puts the hint ladder back to the bottom once the current mistake is behind them.
    /// </summary>
    /// <remarks>See <see cref="HintLadder"/> for which signals count and why.</remarks>
    private void ResetLadderIfWorkMovedOn()
    {
        if (HintLadder.ShouldReset(_tutor.LastVerdict, _ladderSkill))
        {
            _socraticSessionTurn = 0;
        }

        if (_tutor.LastVerdict is { } verdict)
        {
            _ladderSkill = verdict.Skill;
        }
    }

    /// <summary>
    /// Marks the current page for Review and says what was recorded. No tutoring, no transcript.
    /// </summary>
    private async Task RecordAttemptAsync()
    {
        if (_currentPage is null || _markInFlight)
        {
            return;
        }

        _markInFlight = true;
        try
        {
            // Saved first: the mark is about the ink as it stands, and an unsaved stroke would
            // be judged and then not be there when Review came looking for the page.
            await SaveCurrentPageAsync();
            await _tutor.RecordAttemptAsync(_currentPage.Id, _currentPage.SectionId);
            await UpdateUsageAsync();
            await RefreshReviewAsync();
        }
        finally
        {
            _markInFlight = false;
        }
    }

    private async Task<bool> ConfirmSpendingPastTodayAsync()
    {
        var budget = await _tutor.BudgetSnapshotAsync();

        // Either there is room, or the month itself is gone and no dialog can help — in the
        // second case the tutor's own refusal message is the honest answer.
        if (budget.RemainingToday > 0 || budget.RemainingThisMonth <= 0)
        {
            return true;
        }

        // A borrow worth making: enough turns to finish a problem, never more than the month has.
        var borrow = Math.Min(budget.RemainingThisMonth, Math.Max(0.05m, budget.CeilingToday / 2m));
        var (ifYouStop, ifYouBorrow) = MonthlyBudget.DaysAfterToday(
            budget.MonthlyCap, budget.SpentThisMonth, borrow, budget.DaysLeft);

        var daysAfter = Math.Max(0, budget.DaysLeft - 1);
        var message =
            $"""
             Today's limit is spent: ${budget.SpentToday:0.00} of ${budget.CeilingToday:0.00}.

             ${budget.RemainingThisMonth:0.00} is left of this month's ${budget.MonthlyCap:0.00},
             with {daysAfter} day(s) still to go. Stop now and each of them gets ${ifYouStop:0.00}.

             Carry on and today takes another ${borrow:0.00} out of them, leaving ${ifYouBorrow:0.00}
             a day. Tomorrow the limit resets on its own.

             Keep going anyway?
             """;

        var answer = MessageBox.Show(
            this,
            message,
            "Today's budget is spent",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        _tutor.GrantBudgetBorrow(borrow);
        return true;
    }

    private async Task ShowUsageAsync()
    {
        var reportNow = DateTimeOffset.UtcNow;
        var todayStart = BudgetDay.StartOf(reportNow);
        var today = await _host.Usage.GetSummaryAsync(todayStart);
        // Pacific, like every other boundary the budget uses — a UTC month start would roll the
        // ledger over seventeen hours early and hand out a fresh cap on the last evening.
        var month = await _host.Usage.GetSummaryAsync(BudgetDay.StartOfMonth(reportNow));
        var breakdown = await _host.Usage.GetBreakdownAsync(todayStart);

        var todayShare = (await _tutor.BudgetSnapshotAsync()).CeilingToday;

        var report =
            $"""
             Today
               Calls:   {today.Calls}
               Tokens:  {today.TokensIn:N0} in ({today.CachedIn:N0} cached) / {today.TokensOut:N0} out
               Cost:    ${today.Cost:0.0000}
               Limit:   ${todayShare:0.0000} (hard stop) of ${_host.Settings.MonthlyCostCapUsd:0.00} this month, over {BudgetDay.DaysLeftInMonth(reportNow)} day(s) left

             This month
               Calls:   {month.Calls}
               Tokens:  {month.TokensIn:N0} in ({month.CachedIn:N0} cached) / {month.TokensOut:N0} out
               Cost:    ${month.Cost:0.0000}

             Projected monthly cost at today's rate: ${today.Cost * 30:0.00}

             Today, by call type and model
             {FormatUsageBreakdown(breakdown)}
             Page similarity model: {_host.EmbeddingModelName}
             Handwriting recognition: {(InkRecognitionService.IsAvailable ? "available" : "not installed")}
             """;

        new TextReportWindow("Usage and cost", report) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Turns a breakdown into a plain-text table: rows already come highest-cost-first from
    /// the repository, which is what actually answers "where is the money going" — a total on
    /// its own could not have shown that chat was 82% of one measured day's spend on one model.
    /// </summary>
    private static string FormatUsageBreakdown(IReadOnlyList<UsageBreakdownRow> rows)
    {
        if (rows.Count == 0)
        {
            return "  (nothing logged yet today)\n";
        }

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            var cachedShare = row.TokensIn == 0 ? 0 : 100.0 * row.TokensCached / row.TokensIn;
            builder.AppendLine(
                $"  {row.CallType,-18} {row.Model,-24} x{row.Calls,-4} " +
                $"{row.TokensIn,7:N0} in ({cachedShare,3:0}% cached) / {row.TokensOut,6:N0} out   ${row.Cost:0.0000}");
        }

        return builder.ToString();
    }

    private long? CurrentNotebookId()
    {
        if (NavTree.SelectedItem is NotebookNode notebook)
        {
            return notebook.Id;
        }

        if (NavTree.SelectedItem is SectionNode section)
        {
            return section.NotebookId;
        }

        if (_currentPage is not null)
        {
            return _notebooks.FirstOrDefault(n => n.Sections.Any(s => s.Id == _currentPage.SectionId))?.Id;
        }

        return _notebooks.FirstOrDefault()?.Id;
    }

    private long? CurrentSectionId()
    {
        if (NavTree.SelectedItem is SectionNode section)
        {
            return section.Id;
        }

        return _currentPage?.SectionId ?? _notebooks.FirstOrDefault()?.Sections.FirstOrDefault()?.Id;
    }

    private void OnShortcut(object sender, KeyEventArgs e)
    {
        // Shift+R marks the page for Review without a word of tutoring — practising when you do
        // not want help. Guarded on focus, because inside the composer Shift+R has to keep
        // meaning "type a capital R".
        if (Keyboard.Modifiers == ModifierKeys.Shift
            && e.Key == Key.R
            && !ChatInputView.IsKeyboardFocusWithin)
        {
            _ = RecordAttemptAsync();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z:
                    PageEditor.Undo();
                    e.Handled = true;
                    break;
                case Key.Y:
                    PageEditor.Redo();
                    e.Handled = true;
                    break;
                case Key.S:
                    _ = SaveCurrentPageAsync();
                    e.Handled = true;
                    break;
                case Key.F:
                    OpenSearch();
                    e.Handled = true;
                    break;
                case Key.N:
                    _ = AddPageAsync();
                    e.Handled = true;
                    break;
                case Key.H:
                    if (HomeHost.Visibility == Visibility.Visible)
                    {
                        HideHome();
                    }
                    else
                    {
                        _ = ShowHomeAsync();
                    }

                    e.Handled = true;
                    break;
                case Key.T:
                    SetSidebarVisible(Sidebar.Visibility != Visibility.Visible);
                    e.Handled = true;
                    break;
                case Key.V:
                    // Only when the pen surface has focus — inside the chat box Ctrl+V has
                    // to keep meaning "paste text".
                    if (!ChatInputView.IsKeyboardFocusWithin)
                    {
                        _ = PasteImageAsync();
                        e.Handled = true;
                    }

                    break;
                case Key.OemPlus or Key.Add:
                    StepZoom(1.25);
                    e.Handled = true;
                    break;
                case Key.OemMinus or Key.Subtract:
                    StepZoom(1 / 1.25);
                    e.Handled = true;
                    break;
                case Key.D0 or Key.NumPad0:
                    FitZoom();
                    e.Handled = true;
                    break;
            }
        }
        else if (e.Key == Key.F5)
        {
            _ = ReviewPageAsync();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None
                 && e.Key is Key.OemQuestion or Key.Divide
                 && !IsTypingText())
        {
            // The slash is swallowed rather than forwarded: inside the composer MathLive reads
            // "/" as "open a fraction", so passing it on would put the caret in a numerator
            // instead of at the start of a sentence.
            _ = JumpToComposerAsync();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Whether a keystroke belongs to something the student is already typing into, and so
    /// must not be reinterpreted as a shortcut.
    /// </summary>
    /// <remarks>
    /// Keys pressed inside a WebView2 do not surface as WPF key events at all, so the composer
    /// is mostly self-guarding — but the search box, the notebook-tree rename box and the
    /// settings fields are plain WPF controls on the same window, and a bare "/" is a
    /// character every one of them has to be able to receive.
    /// </remarks>
    private bool IsTypingText() =>
        ChatInputView.IsKeyboardFocusWithin
        || Keyboard.FocusedElement is TextBox or PasswordBox or RichTextBox;

    /// <summary>
    /// Puts the caret in the chat composer from anywhere, opening the sidebar if it is shut.
    /// </summary>
    private async Task JumpToComposerAsync()
    {
        if (Sidebar.Visibility != Visibility.Visible)
        {
            SetSidebarVisible(true);
        }

        // Both halves are needed: the WPF focus moves the window's focus into the WebView2
        // host, and the script moves the caret into the text box inside it. Without the
        // first, typing goes nowhere; without the second, the field looks focused but is not.
        ChatInputView.Focus();
        await RunComposerScriptAsync("window.composer.focus()");
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
