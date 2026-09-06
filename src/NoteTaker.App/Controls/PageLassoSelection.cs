using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Shapes;
using NoteTaker.App.Services;
using NoteTaker.Core.Models;

namespace NoteTaker.App.Controls;

/// <summary>What a finished lasso caught, and where it started out.</summary>
internal sealed record LassoContents(
    IReadOnlyList<Stroke> Strokes,
    IReadOnlyList<PageImage> Images,
    Rect Origin);

/// <summary>
/// A region of the page picked out with the barrel-button lasso, behaving like a pasted
/// picture: it draws a dashed frame, a finger drags it, and three taps delete it.
/// </summary>
/// <remarks>
/// Deliberately not resizable, unlike <see cref="PageImageLayer"/>. Scaling handwriting
/// changes the nib width relative to the letterforms and it stops looking like the rest of
/// the page — moving a worked line somewhere it fits is the thing that is actually wanted.
/// </remarks>
internal sealed class PageLassoSelection(Canvas overlay)
{
    /// <summary>Matches <see cref="PageImageLayer"/> so both selections feel the same.</summary>
    private const double MultiTapMilliseconds = 600;

    private const double MultiTapSlop = 60;

    /// <summary>Grab margin outside the frame, in page units — sized for a fingertip.</summary>
    private const double GrabMargin = 20;

    private readonly List<Stroke> _strokes = [];
    private readonly List<PageImage> _images = [];

    private Rect _bounds = Rect.Empty;
    private bool _dragging;
    private Point _dragFrom;
    private Vector _dragTotal;

    private int _tapCount;
    private DateTime _lastTapAt;
    private Point _lastTapPoint;

    /// <summary>A drag finished: the total offset applied, for a single undo entry.</summary>
    public event EventHandler<Vector>? Moved;

    /// <summary>Triple-tapped: the caller removes the contents from the page and storage.</summary>
    public event EventHandler<LassoContents>? DeleteRequested;

    public bool IsActive => _strokes.Count > 0 || _images.Count > 0;

    public bool IsDragging => _dragging;

    public LassoContents Contents => new([.. _strokes], [.. _images], _bounds);

    /// <summary>
    /// Takes whatever the closed path encloses. Returns false when it caught nothing, so the
    /// caller can leave the page untouched rather than showing an empty frame.
    /// </summary>
    public bool Capture(IReadOnlyList<Point> path, StrokeCollection strokes, IReadOnlyList<PageImage> images)
    {
        Clear();

        // Three points is the minimum that encloses any area at all; below that the student
        // dotted rather than circled, and every containment test would be vacuously false.
        if (path.Count < 3)
        {
            return false;
        }

        var lasso = new Rect(path[0], path[0]);
        foreach (var point in path)
        {
            lasso.Union(point);
        }

        foreach (Stroke stroke in strokes)
        {
            // Bounds first: the polygon walk is far more expensive, and most strokes on a page
            // are nowhere near the lasso.
            if (stroke.GetBounds().IntersectsWith(lasso) && MostlyInside(stroke, path))
            {
                _strokes.Add(stroke);
            }
        }

        foreach (var image in images)
        {
            var centre = new Point(image.X + (image.Width / 2), image.Y + (image.Height / 2));
            if (lasso.Contains(centre) && Contains(path, centre))
            {
                _images.Add(image);
            }
        }

        InkTrace.Log(InkEvent.LassoClosed, path.Count, _strokes.Count, _images.Count);

        if (!IsActive)
        {
            return false;
        }

        RecomputeBounds();
        Draw();
        return true;
    }

    /// <summary>
    /// Whether enough of a stroke falls inside the outline to count as selected.
    /// </summary>
    /// <remarks>
    /// A majority rather than all of it, because a lasso drawn at speed routinely clips the
    /// tail of a descender or the far end of a fraction bar, and dropping the whole stroke for
    /// that reads as the selection randomly missing things.
    /// </remarks>
    internal static bool MostlyInside(Stroke stroke, IReadOnlyList<Point> path)
    {
        var points = stroke.StylusPoints;
        if (points.Count == 0)
        {
            return false;
        }

        var inside = 0;
        for (var i = 0; i < points.Count; i++)
        {
            if (Contains(path, points[i].ToPoint()))
            {
                inside++;
            }
        }

        return inside * 2 > points.Count;
    }

    /// <summary>Even-odd crossing test against the lasso, treated as a closed polygon.</summary>
    internal static bool Contains(IReadOnlyList<Point> path, Point point)
    {
        var inside = false;

        for (int i = 0, j = path.Count - 1; i < path.Count; j = i++)
        {
            var a = path[i];
            var b = path[j];

            if (a.Y > point.Y != b.Y > point.Y
                && point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Whether a finger here should drag the selection rather than pan the page.</summary>
    public bool HitTest(Point page)
    {
        if (!IsActive)
        {
            return false;
        }

        var grab = _bounds;
        grab.Inflate(GrabMargin, GrabMargin);
        return grab.Contains(page);
    }

    /// <summary>A finger went down. Returns true if the selection is taking the gesture.</summary>
    public bool Begin(Point page)
    {
        if (!IsActive)
        {
            return false;
        }

        if (!HitTest(page))
        {
            // Tapping away from the selection lets it go, the same way tapping bare paper
            // deselects a picture.
            Clear();
            return false;
        }

        if (CountTap(page) >= 3)
        {
            var contents = Contents;
            ResetTapRun();
            DeleteRequested?.Invoke(this, contents);
            Clear();
            return true;
        }

        _dragging = true;
        _dragFrom = page;
        _dragTotal = default;
        return true;
    }

    public void Move(Point page)
    {
        if (!_dragging)
        {
            return;
        }

        var delta = page - _dragFrom;
        if (delta.LengthSquared <= 0)
        {
            return;
        }

        _dragFrom = page;
        _dragTotal += delta;
        Offset(delta);
    }

    public void End()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        if (_dragTotal.LengthSquared > 0)
        {
            Moved?.Invoke(this, _dragTotal);
        }

        _dragTotal = default;
    }

    /// <summary>Shifts everything selected, and the frame with it.</summary>
    public void Offset(Vector delta)
    {
        var matrix = Matrix.Identity;
        matrix.Translate(delta.X, delta.Y);

        foreach (var stroke in _strokes)
        {
            stroke.Transform(matrix, applyToStylusTip: false);
        }

        foreach (var image in _images)
        {
            image.X += delta.X;
            image.Y += delta.Y;
        }

        _bounds.Offset(delta);
        Draw();
    }

    private int CountTap(Point page)
    {
        var now = DateTime.UtcNow;
        var soonEnough = (now - _lastTapAt).TotalMilliseconds <= MultiTapMilliseconds;
        var closeEnough = Math.Abs(page.X - _lastTapPoint.X) <= MultiTapSlop
            && Math.Abs(page.Y - _lastTapPoint.Y) <= MultiTapSlop;

        _tapCount = soonEnough && closeEnough ? _tapCount + 1 : 1;
        _lastTapAt = now;
        _lastTapPoint = page;
        return _tapCount;
    }

    private void ResetTapRun() => _tapCount = 0;

    private void RecomputeBounds()
    {
        _bounds = Rect.Empty;

        foreach (var stroke in _strokes)
        {
            _bounds.Union(stroke.GetBounds());
        }

        foreach (var image in _images)
        {
            _bounds.Union(new Rect(image.X, image.Y, image.Width, image.Height));
        }
    }

    public void Clear()
    {
        _strokes.Clear();
        _images.Clear();
        _bounds = Rect.Empty;
        _dragging = false;
        ResetTapRun();
        overlay.Children.Clear();
    }

    private void Draw()
    {
        overlay.Children.Clear();
        if (!IsActive || _bounds.IsEmpty)
        {
            return;
        }

        var frame = _bounds;
        frame.Inflate(8, 8);

        var accent = (Brush)Application.Current.FindResource("Accent");

        var rectangle = new Rectangle
        {
            Width = frame.Width,
            Height = frame.Height,
            Stroke = accent,
            StrokeThickness = 2,
            StrokeDashArray = [6, 4],
            Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
        };

        Canvas.SetLeft(rectangle, frame.X);
        Canvas.SetTop(rectangle, frame.Y);
        overlay.Children.Add(rectangle);
    }
}
