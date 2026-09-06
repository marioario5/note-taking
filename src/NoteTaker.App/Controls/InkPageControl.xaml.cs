using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NoteTaker.App.Services;
using NoteTaker.Core.Models;

namespace NoteTaker.App.Controls;

public sealed record InkChangedEventArgs(NormalizedRegion ChangedRegion);

public enum InkTool
{
    Pen,
    Highlighter,
    Eraser,
    Select,
}

/// <summary>
/// Which surface the page is standing in for. The night ground is the default; a page with a
/// PDF attached switches to paper so printed worksheets and dark ink stay legible.
/// </summary>
public enum PageGround
{
    Night,
    Paper,
}

public partial class InkPageControl : UserControl
{
    // Practical float floor only — there is no world extent, so zoom-out is free.
    private const double MinZoom = 0.02;
    private const double MaxZoom = 8.0;

    private static readonly Color MajorMark = Color.FromRgb(0xE8, 0x8F, 0x74);
    private static readonly Color MinorMark = Color.FromRgb(0xFE, 0xBB, 0x55);
    private static readonly Color NotationMark = Color.FromRgb(0xE8, 0x9B, 0x2E);

    private readonly Stack<PageEdit> _undo = new();
    private readonly Stack<PageEdit> _redo = new();
    private readonly List<(TutorFeedback Feedback, Rect Bounds)> _highlightHits = [];
    private readonly PageTouchGestures _gestures;
    private readonly PageImageLayer _images;

    private readonly PageLassoSelection _lasso;
    private bool _suppressHistory;
    private bool _centeredOnce;
    private IReadOnlyList<TutorFeedback> _feedback = [];

    public InkPageControl()
    {
        InitializeComponent();
        ApplyWorldLayout();

        Ink.DefaultDrawingAttributes = CreatePenAttributes();
        Ink.Strokes.StrokesChanged += OnStrokesChanged;
        Ink.EraserPreviewChanged += OnEraserPreviewChanged;
        PageRoot.PreviewMouseLeftButtonDown += OnPagePointerDown;
        PageRoot.PreviewStylusDown += OnPageStylusDown;

        _images = new PageImageLayer(ImageLayer, ImageSelectionLayer);
        _images.BoundsCommitted += (_, change) =>
        {
            var after = new Rect(change.Image.X, change.Image.Y, change.Image.Width, change.Image.Height);
            PushImageBoundsEdit(change.Image, change.Before, after);
            ImageBoundsCommitted?.Invoke(this, change.Image);
        };
        _images.Changed += (_, _) => ImagesChanged?.Invoke(this, EventArgs.Empty);
        _images.DeleteRequested += (_, image) => ImageDeleteRequested?.Invoke(this, image);

        _lasso = new PageLassoSelection(LassoSelectionLayer);
        _lasso.Moved += (_, delta) => PushLassoMoveEdit(_lasso.Contents, delta);
        _lasso.DeleteRequested += (_, contents) => DeleteLassoContents(contents);
        Ink.LassoChanged += OnLassoChanged;

        _gestures = new PageTouchGestures(Viewport)
        {
            GetScale = () => ZoomLevel,
            ScaleTo = SetZoomAt,
            PanBy = PanCameraBy,
            ToPagePoint = point => Viewport.TranslatePoint(point, PageRoot),

            // The lasso selection is asked first: while one is up it is the thing the student
            // is working with, and a picture sitting underneath it must not steal the drag.
            BeginImageGesture = page => _lasso.Begin(page) || _images.Begin(page),
            MoveImageGesture = page =>
            {
                if (_lasso.IsDragging)
                {
                    _lasso.Move(page);
                }
                else
                {
                    _images.Move(page);
                }
            },
            EndImageGesture = () =>
            {
                _lasso.End();
                _images.End();
            },
        };

        _gestures.Tapped += (_, point) => TryTapHighlight(Viewport.TranslatePoint(point, PageRoot));

        Viewport.PreviewMouseWheel += OnViewportWheel;
        Loaded += OnLoaded;
        SizeChanged += (_, _) =>
        {
            if (!_centeredOnce && ActualWidth > 0 && ActualHeight > 0)
            {
                CenterPage();
                _centeredOnce = true;
            }
        };
    }

    public event EventHandler<InkChangedEventArgs>? InkChanged;

    public event EventHandler<TutorFeedback>? HighlightTapped;

    /// <summary>Raised whenever the page scale changes, including by pinch or wheel.</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>Raised when the ground flips, so the ink palette can follow it.</summary>
    public event EventHandler? GroundChanged;

    public StrokeCollection Strokes => Ink.Strokes;

    public PageGround Ground { get; private set; } = PageGround.Night;

    /// <summary>Win32 pointer hook for when WPF's tablet device list is empty.</summary>
    public void AttachPointerGuard(Window window) =>
        new PointerInputGuard(Ink, _gestures, Viewport).Attach(window);

    /// <summary>The PDF or composed content drawn beneath the ink layer.</summary>
    public ImageSource? PageBackground
    {
        get => PdfLayer.Source;
        set
        {
            PdfLayer.Source = value;
            ApplyGround(value is null ? PageGround.Night : PageGround.Paper);
        }
    }

    /// <summary>Feint 31px ruling, matching the mockup's ruled ground.</summary>
    public bool IsRuled
    {
        get => InfiniteRules.Visibility == Visibility.Visible;
        set
        {
            var visibility = value ? Visibility.Visible : Visibility.Collapsed;
            InfiniteRules.Visibility = visibility;
            RuleLayer.Visibility = visibility;
        }
    }

    // The blank-page overlay is gone: first its mascot, then its two lines of type. It sat over
    // a live canvas on every empty page, and the page being empty is already legible without
    // being told. Its drift animation had to be hand-started and hand-stopped too, because a
    // Forever storyboard keeps running behind a Collapsed element and holds a Render-priority
    // frame ahead of pen input. Use IsPageOpen for the genuinely-nothing-to-draw-on case.

    /// <summary>
    /// Whether a page is actually open. Hides the writing surface entirely when there is
    /// nothing to write on, so strokes can't be made against a null page and silently lost.
    /// </summary>
    public bool IsPageOpen
    {
        get => Viewport.Visibility == Visibility.Visible;
        set => Viewport.Visibility = value ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>A picture finished being moved or resized; its new rectangle needs saving.</summary>
    public event EventHandler<PageImage>? ImageBoundsCommitted;

    /// <summary>The set or geometry of placed pictures changed.</summary>
    public event EventHandler? ImagesChanged;

    /// <summary>A picture was triple-tapped and should be removed.</summary>
    public event EventHandler<PageImage>? ImageDeleteRequested;

    /// <summary>Pictures on this page, for compositing into exports and chat captures.</summary>
    public IReadOnlyList<PageImage> PageImages => _images.Items;

    public PageImage? SelectedImage => _images.Selected;

    /// <summary>
    /// Centre of what the student is currently looking at, in world coordinates.
    /// </summary>
    /// <remarks>
    /// New pictures land here rather than at the middle of the A4 sheet. On an infinite
    /// canvas those are usually nowhere near each other: pan away to some working space and
    /// a paste would drop onto the sheet, off-screen, looking like nothing happened at all.
    /// </remarks>
    /// <summary>Camera offset, so the view can be restored exactly where it was left.</summary>
    public Point PanOffset
    {
        get => new(Pan.X, Pan.Y);
        set
        {
            Pan.X = value.X;
            Pan.Y = value.Y;

            // Setting the camera explicitly counts as taking charge of it. The control
            // centres the sheet once on first layout, and that pass runs after startup
            // finishes — without this it would quietly undo a restored view.
            _centeredOnce = true;
        }
    }

    /// <summary>
    /// The world rectangle currently on screen — what the student can actually see.
    /// </summary>
    /// <remarks>
    /// This is what a tutor capture should frame. Sizing the capture to "everything on the
    /// page" sounds more thorough and is worse: the drawable world is 200,000 units across,
    /// so one stray stroke left in a far corner stretches the rectangle enormously and the
    /// real work renders as a few hairlines. A saved capture showed exactly that — 140
    /// strokes reduced to scratches on a blank field. What is on screen is also what the
    /// student means when they ask "is this right?".
    /// </remarks>
    public Rect ViewBounds
    {
        get
        {
            if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0)
            {
                return PageGeometry.SheetBounds;
            }

            var topLeft = Viewport.TranslatePoint(new Point(0, 0), PageRoot);
            var bottomRight = Viewport.TranslatePoint(
                new Point(Viewport.ActualWidth, Viewport.ActualHeight), PageRoot);

            return new Rect(topLeft, bottomRight);
        }
    }

    public Point ViewCentre =>
        Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0
            ? PageGeometry.SheetCenter
            : Viewport.TranslatePoint(
                new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2),
                PageRoot);

    public void LoadImages(IEnumerable<PageImage> images) => _images.Load(images);

    /// <summary>
    /// Adds a picture and records it in the SAME history as ink, so Ctrl+Z after a paste
    /// takes the paste back rather than reaching past it for an older stroke.
    /// </summary>
    public void AddImage(PageImage image)
    {
        _images.Add(image);
        PushEdit(new PageEdit(
            Undo: () => _images.Remove(image),
            Redo: () => _images.Add(image),
            Bounds: Rect.Empty));
    }

    public void RemoveImage(PageImage image)
    {
        _images.Remove(image);
        PushEdit(new PageEdit(
            Undo: () => _images.Add(image),
            Redo: () => _images.Remove(image),
            Bounds: Rect.Empty));
    }

    /// <summary>Records a move/resize so it can be undone like anything else.</summary>
    /// <summary>Draws the lasso as it is made, and turns it into a selection on pen-up.</summary>
    private void OnLassoChanged(object? sender, LassoArgs e)
    {
        if (!e.Closed)
        {
            LassoPath.Points = [.. e.Path];
            LassoPath.Visibility = e.Path.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        LassoPath.Visibility = Visibility.Collapsed;
        LassoPath.Points = [];

        // A lasso that caught nothing clears any previous selection rather than leaving a
        // stale frame behind — circling empty paper reads as "never mind".
        if (!_lasso.Capture(e.Path, Ink.Strokes, _images.Items))
        {
            _lasso.Clear();
        }
    }

    private void PushLassoMoveEdit(LassoContents contents, Vector delta) =>
        PushEdit(new PageEdit(
            Undo: () => OffsetLassoContents(contents, -delta),
            Redo: () => OffsetLassoContents(contents, delta),
            Bounds: contents.Origin));

    /// <summary>
    /// Undo/redo of a lasso move. Works on the captured contents directly rather than through
    /// the selection, which may well have been dismissed by the time this runs.
    /// </summary>
    private void OffsetLassoContents(LassoContents contents, Vector delta)
    {
        var matrix = Matrix.Identity;
        matrix.Translate(delta.X, delta.Y);

        foreach (var stroke in contents.Strokes)
        {
            stroke.Transform(matrix, applyToStylusTip: false);
        }

        foreach (var image in contents.Images)
        {
            _images.SetBounds(image, new Rect(
                image.X + delta.X,
                image.Y + delta.Y,
                image.Width,
                image.Height));
        }

        _lasso.Clear();
        ImagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteLassoContents(LassoContents contents)
    {
        foreach (var stroke in contents.Strokes)
        {
            Ink.Strokes.Remove(stroke);
        }

        // Pictures go out through the page's own delete path so storage stays in step.
        foreach (var image in contents.Images)
        {
            ImageDeleteRequested?.Invoke(this, image);
        }

        PushEdit(new PageEdit(
            Undo: () =>
            {
                foreach (var stroke in contents.Strokes)
                {
                    Ink.Strokes.Add(stroke);
                }
            },
            Redo: () =>
            {
                foreach (var stroke in contents.Strokes)
                {
                    Ink.Strokes.Remove(stroke);
                }
            },
            Bounds: contents.Origin));
    }

    private void PushImageBoundsEdit(PageImage image, Rect before, Rect after) =>
        PushEdit(new PageEdit(
            Undo: () => _images.SetBounds(image, before),
            Redo: () => _images.SetBounds(image, after),
            Bounds: Rect.Empty));

    private void PushEdit(PageEdit edit)
    {
        if (_suppressHistory)
        {
            return;
        }

        _undo.Push(edit);
        _redo.Clear();
    }

    public void DeselectImage() => _images.Deselect();

    public double ZoomLevel
    {
        get => Zoom.ScaleX;
        set
        {
            var clamped = Math.Clamp(value, MinZoom, MaxZoom);
            if (Math.Abs(clamped - Zoom.ScaleX) < 0.0001)
            {
                return;
            }

            // Zoom about viewport center when set directly (fit / buttons without an anchor).
            SetZoomAt(clamped, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
        }
    }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Replaces page content without registering the swap as an undoable edit.</summary>
    public void LoadStrokes(StrokeCollection strokes)
    {
        _suppressHistory = true;
        try
        {
            MigrateLegacySheetStrokes(strokes);

            Ink.Strokes.StrokesChanged -= OnStrokesChanged;
            Ink.Strokes = strokes;
            Ink.Strokes.StrokesChanged += OnStrokesChanged;

            _undo.Clear();
            _redo.Clear();
            _lasso.Clear();
            ClearHighlights();
        }
        finally
        {
            _suppressHistory = false;
        }
    }

    public void SetTool(InkTool tool, Color colour, double width)
    {
        switch (tool)
        {
            case InkTool.Pen:
                Ink.DefaultDrawingAttributes = CreatePenAttributes(colour, width);
                Ink.SetWritingMode(InkCanvasEditingMode.Ink);
                break;

            case InkTool.Highlighter:
                Ink.DefaultDrawingAttributes = new DrawingAttributes
                {
                    Color = Color.FromArgb(90, colour.R, colour.G, colour.B),
                    Width = Math.Max(12, width * 6),
                    Height = Math.Max(12, width * 6),
                    IsHighlighter = true,
                    FitToCurve = true,
                    IgnorePressure = true,
                };
                Ink.SetWritingMode(InkCanvasEditingMode.Ink);
                break;

            case InkTool.Eraser:
                Ink.SetWritingMode(InkCanvasEditingMode.EraseByPoint);
                break;

            case InkTool.Select:
                Ink.SetWritingMode(InkCanvasEditingMode.None);
                break;
        }
    }

    /// <summary>Scales while keeping <paramref name="anchor"/> (viewport coords) fixed on screen.</summary>
    public void SetZoomAt(double scale, Point anchor)
    {
        var previous = Zoom.ScaleX;
        var clamped = Math.Clamp(scale, MinZoom, MaxZoom);

        if (Math.Abs(clamped - previous) < 0.0001)
        {
            return;
        }

        // screen = world * scale + pan  =>  world = (screen - pan) / scale
        var worldX = (anchor.X - Pan.X) / previous;
        var worldY = (anchor.Y - Pan.Y) / previous;

        Zoom.ScaleX = clamped;
        Zoom.ScaleY = clamped;
        Pan.X = anchor.X - (worldX * clamped);
        Pan.Y = anchor.Y - (worldY * clamped);

        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Centers the A4 sheet in the viewport (used by fit-to-view).</summary>
    public void CenterPage()
    {
        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0)
        {
            return;
        }

        var scale = Zoom.ScaleX;
        var center = PageGeometry.SheetCenter;
        Pan.X = (Viewport.ActualWidth / 2) - (center.X * scale);
        Pan.Y = (Viewport.ActualHeight / 2) - (center.Y * scale);
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var edit = _undo.Pop();
        _suppressHistory = true;
        try
        {
            edit.Undo();
        }
        finally
        {
            _suppressHistory = false;
        }

        _redo.Push(edit);
        RaiseInkChanged(edit.Bounds);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var edit = _redo.Pop();
        _suppressHistory = true;
        try
        {
            edit.Redo();
        }
        finally
        {
            _suppressHistory = false;
        }

        _undo.Push(edit);
        RaiseInkChanged(edit.Bounds);
    }

    /// <summary>
    /// Draws the tutor's findings as the mockup's halo mark: a tinted glow over the ink plus a
    /// numbered chip that matches the row in the sidebar. Passing an empty list clears the layer.
    /// </summary>
    public void ShowHighlights(IReadOnlyList<TutorFeedback> feedback)
    {
        _feedback = feedback;
        HighlightLayer.Children.Clear();
        _highlightHits.Clear();

        for (var index = 0; index < feedback.Count; index++)
        {
            var item = feedback[index];
            var rect = PageGeometry.ToPageRect(item.Region);

            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            var colour = SeverityColour(item.Severity);
            var tint = item.Severity == FeedbackSeverity.Major ? 41 : 36;

            var halo = new Rectangle
            {
                Width = rect.Width + 10,
                Height = rect.Height + 8,
                Fill = new SolidColorBrush(Color.FromArgb((byte)tint, colour.R, colour.G, colour.B)),
                RadiusX = 8,
                RadiusY = 8,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 0,
                    Color = colour,
                    Opacity = 0.45,
                },
            };

            Canvas.SetLeft(halo, rect.X - 5);
            Canvas.SetTop(halo, rect.Y - 4);
            HighlightLayer.Children.Add(halo);

            HighlightLayer.Children.Add(BuildChip(index + 1, colour, rect));
            _highlightHits.Add((item, new Rect(rect.X - 5, rect.Y - 4, rect.Width + 34, rect.Height + 8)));
        }
    }

    public void ClearHighlights() => ShowHighlights([]);

    public void ScrollToRegion(NormalizedRegion region)
    {
        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0)
        {
            return;
        }

        var rect = PageGeometry.ToPageRect(region);
        var scale = Zoom.ScaleX;
        var worldX = rect.X + (rect.Width / 2);
        var worldY = rect.Y + (rect.Height / 2);

        Pan.X = (Viewport.ActualWidth / 2) - (worldX * scale);
        Pan.Y = (Viewport.ActualHeight / 2) - (worldY * scale);
    }

    private void PanCameraBy(double dx, double dy)
    {
        Pan.X += dx;
        Pan.Y += dy;

        // Pan sits AFTER the scale in the transform group, so it is in viewport pixels and a
        // finger delta should move the page one-for-one. If the reported delta and the
        // resulting camera position disagree, the page is not following the finger.
        InkTrace.Log(InkEvent.PanApplied, (int)dx, (int)dy, Pan.X);
    }

    private void ApplyWorldLayout()
    {
        PageRoot.Width = PageGeometry.WorldWidth;
        PageRoot.Height = PageGeometry.WorldHeight;
        Canvas.SetLeft(PageRoot, 0);
        Canvas.SetTop(PageRoot, 0);
        Canvas.SetLeft(PdfLayer, PageGeometry.OriginX);
        Canvas.SetTop(PdfLayer, PageGeometry.OriginY);
        PdfLayer.Width = PageGeometry.Width;
        PdfLayer.Height = PageGeometry.Height;
    }

    /// <summary>
    /// Older pages stored ink in sheet space at (0,0). Shift them onto the centered sheet once.
    /// </summary>
    private static void MigrateLegacySheetStrokes(StrokeCollection strokes)
    {
        if (strokes.Count == 0 || PageGeometry.OriginX <= 0)
        {
            return;
        }

        var bounds = strokes.GetBounds();
        var looksLegacy = bounds.Left >= -40
            && bounds.Top >= -40
            && bounds.Right <= PageGeometry.Width + 80
            && bounds.Bottom <= PageGeometry.Height + 80;

        if (!looksLegacy)
        {
            return;
        }

        var offset = new Vector(PageGeometry.OriginX, PageGeometry.OriginY);
        var matrix = new Matrix();
        matrix.Translate(offset.X, offset.Y);
        strokes.Transform(matrix, false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWorldLayout();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_centeredOnce)
            {
                CenterPage();
                _centeredOnce = true;
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnEraserPreviewChanged(object? sender, EraserPreviewArgs e)
    {
        if (!e.Visible)
        {
            EraserPreview.Visibility = Visibility.Collapsed;
            return;
        }

        EraserPreview.Width = e.Bounds.Width;
        EraserPreview.Height = e.Bounds.Height;
        Canvas.SetLeft(EraserPreview, e.Bounds.X);
        Canvas.SetTop(EraserPreview, e.Bounds.Y);
        EraserPreview.Visibility = Visibility.Visible;
    }

    private static FrameworkElement BuildChip(int number, Color colour, Rect rect)
    {
        var chip = new Grid { Width = 19, Height = 19 };

        chip.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(colour),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 0,
                Color = colour,
                Opacity = 0.5,
            },
        });

        chip.Children.Add(new TextBlock
        {
            Text = number.ToString(),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x20, 0x33)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Canvas.SetLeft(chip, Math.Min(rect.Right + 8, PageGeometry.OriginX + PageGeometry.Width - 21));
        Canvas.SetTop(chip, rect.Y + (rect.Height / 2) - 9.5);
        return chip;
    }

    /// <summary>
    /// Default nib, in page units (1240 units = A4's 210 mm, so ~5.9 units/mm — this is
    /// 0.25 mm, a fine-liner).
    /// </summary>
    /// <remarks>
    /// Was 2.4. WPF builds a stroke as the tip stamped along the path, so any interior
    /// feature narrower than the nib is closed by construction, and a glyph needs roughly
    /// 8x the nib width to stay legible. At 2.4 that put the floor at ~19 units — and a
    /// trace of 16 "x cubed" attempts split exactly there: every large "3" (24.9-32.9 units
    /// wide) came out right, every small one (13.4-20.9) collapsed. Superscripts and
    /// multiplication dots are the whole point of this app, so the default has to clear the
    /// smallest thing a student actually writes, not the average. At 1.5 the floor is ~12
    /// units, which covers the small digits in that trace.
    /// </remarks>
    public const double DefaultPenWidth = 1.5;

    private static DrawingAttributes CreatePenAttributes(Color? colour = null, double width = DefaultPenWidth) => new()
    {
        Color = colour ?? Color.FromRgb(0xEC, 0xE4, 0xD6),
        Width = width,
        Height = width,
        // Deliberately OFF, and it must stay off for the pen.
        //
        // This was briefly true, on the theory that a sparse fast stroke needs fitting to
        // avoid rendering as straight facets. An ink trace disproved it. Across 16 attempts
        // at "x cubed", every "3" was captured COMPLETE — 21 to 50 points, not one stroke
        // truncated — yet 8 of them rendered as a single arc. The split was by point count:
        // strokes of <=32 points came out wrong 7 times in 9, strokes of >=41 points came out
        // right 6 times in 7. That is Bezier fitting doing exactly what it exists to do. A "3"
        // IS a cusp — two arcs meeting at a sharp reversal — and smoothing corners away is
        // the whole point of a curve fit. Given enough samples the fitter preserves the
        // reversal; given a handful it rounds straight through it and emits one clean arc.
        //
        // The facet worry it was meant to solve does not arise: the same trace measured pen
        // sampling at a 7.1 ms median (~141 Hz), so the raw polyline is already dense enough
        // to read as a smooth curve. Fitting bought nothing and cost the letterforms.
        FitToCurve = false,
        IgnorePressure = false,
        StylusTip = StylusTip.Ellipse,
    };

    private static Color SeverityColour(FeedbackSeverity severity) => severity switch
    {
        FeedbackSeverity.Major => MajorMark,
        FeedbackSeverity.Minor => MinorMark,
        _ => NotationMark,
    };

    private void ApplyGround(PageGround ground)
    {
        if (Ground == ground)
        {
            return;
        }

        Ground = ground;

        var night = ground == PageGround.Night;
        InfiniteGround.SetResourceReference(Shape.FillProperty, night ? "GroundWash" : "PaperWash");
        InfiniteRules.SetResourceReference(Shape.FillProperty, night ? "RuleBrushNight" : "RuleBrushPaper");
        // The design system composites grain with mix-blend-mode:multiply, which over the
        // dark night ground darkens by a hair — imperceptible, "tooth, never noise". WPF's
        // ImageBrush has no blend mode, so it paints the light noise texture *additively*
        // instead: over dark navy that reads as heavy speckle, the exact opposite of the
        // intent. Off on night; kept on paper, where lightening cream by 5.5% is the same
        // near-invisible tooth the multiply would have produced.
        InfiniteGrain.Opacity = night ? 0 : 0.7;
        RuleLayer.SetResourceReference(Shape.FillProperty, night ? "RuleBrushNight" : "RuleBrushPaper");
        // Page plate stays transparent — the infinite plane carries the ground.

        GroundChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Control))
        {
            return;
        }

        SetZoomAt(ZoomLevel * (e.Delta > 0 ? 1.12 : 1 / 1.12), e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        var bounds = Rect.Empty;

        foreach (var stroke in e.Added)
        {
            bounds.Union(stroke.GetBounds());
        }

        foreach (var stroke in e.Removed)
        {
            bounds.Union(stroke.GetBounds());
        }

        if (!_suppressHistory)
        {
            var added = e.Added;
            var removed = e.Removed;

            PushEdit(new PageEdit(
                Undo: () =>
                {
                    foreach (var stroke in added)
                    {
                        Ink.Strokes.Remove(stroke);
                    }

                    foreach (var stroke in removed)
                    {
                        Ink.Strokes.Add(stroke);
                    }
                },
                Redo: () =>
                {
                    foreach (var stroke in removed)
                    {
                        Ink.Strokes.Remove(stroke);
                    }

                    foreach (var stroke in added)
                    {
                        Ink.Strokes.Add(stroke);
                    }
                },
                Bounds: bounds));
        }

        RaiseInkChanged(bounds);
    }

    private void RaiseInkChanged(Rect bounds)
    {
        var region = bounds.IsEmpty
            ? default
            : PageGeometry.ToNormalized(bounds);

        InkChanged?.Invoke(this, new InkChangedEventArgs(region));
    }

    private void OnPagePointerDown(object sender, MouseButtonEventArgs e) =>
        TryTapHighlight(e.GetPosition(PageRoot));

    private void OnPageStylusDown(object sender, StylusDownEventArgs e) =>
        TryTapHighlight(e.GetPosition(PageRoot));

    private void TryTapHighlight(Point position)
    {
        if (Ink.EditingMode != InkCanvasEditingMode.None || _feedback.Count == 0)
        {
            return;
        }

        foreach (var (feedback, bounds) in _highlightHits)
        {
            if (bounds.Contains(position))
            {
                HighlightTapped?.Invoke(this, feedback);
                return;
            }
        }
    }

    /// <summary>
    /// One reversible change to the page. Ink edits and picture edits share a single history
    /// so Ctrl+Z means "undo the last thing I did", not "undo the last thing I did that
    /// happened to be a stroke" — pasting a picture and then undoing used to leave it there
    /// while silently removing a stroke from several actions ago.
    /// </summary>
    private sealed record PageEdit(Action Undo, Action Redo, Rect Bounds);
}
