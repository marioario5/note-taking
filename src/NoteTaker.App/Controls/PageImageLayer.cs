using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NoteTaker.App.Services;
using NoteTaker.Core.Models;

namespace NoteTaker.App.Controls;

/// <summary>Which grip a finger landed on, if any.</summary>
internal enum ImageGrip
{
    None,
    Move,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Owns the pictures placed on a page: drawing them, deciding which one a finger touched,
/// and moving or resizing the selected one.
/// </summary>
/// <remarks>
/// Deliberately touch-driven and pen-blind. The pen is for writing — including writing on
/// top of a pasted picture — so if the nib could drag images, annotating one would shove it
/// across the page instead. A finger has no other job over the paper except panning, and
/// panning is the thing worth giving up when the finger is on top of a picture.
/// </remarks>
internal sealed class PageImageLayer(Canvas images, Canvas selection)
{
    /// <summary>Page units. Comfortably bigger than a fingertip at typical zoom.</summary>
    private const double GripRadius = 34;

    private const double MinimumSize = 40;

    private readonly List<(PageImage Model, Image Element)> _items = [];

    private PageImage? _selected;
    private ImageGrip _grip = ImageGrip.None;
    private Point _dragOrigin;
    private Rect _dragStartBounds;

    // Triple-tap to delete. Deliberately three and not two: a picture is easy to tap twice
    // by accident while positioning it, and there is no undo for a deleted one.
    private const double MultiTapMilliseconds = 600;
    private const double MultiTapSlop = 60;
    private long? _tapTargetId;
    private int _tapCount;
    private DateTime _lastTapAt;
    private Point _lastTapAt2;

    /// <summary>
    /// A move or resize finished: the picture, and the rectangle it started from. The
    /// "before" is what makes the change undoable.
    /// </summary>
    public event EventHandler<(PageImage Image, Rect Before)>? BoundsCommitted;

    /// <summary>Raised when the set of images changes, so the page can re-render for export.</summary>
    public event EventHandler? Changed;

    /// <summary>Triple-tapped: the caller should remove it from storage.</summary>
    public event EventHandler<PageImage>? DeleteRequested;

    public PageImage? Selected => _selected;

    public IReadOnlyList<PageImage> Items => _items.Select(i => i.Model).ToList();

    public void Load(IEnumerable<PageImage> models)
    {
        _items.Clear();
        images.Children.Clear();
        Deselect();

        foreach (var model in models)
        {
            AddElement(model);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(PageImage model)
    {
        AddElement(model);
        Select(model);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(PageImage model)
    {
        var index = _items.FindIndex(i => i.Model.Id == model.Id);
        if (index < 0)
        {
            return;
        }

        images.Children.Remove(_items[index].Element);
        _items.RemoveAt(index);

        if (_selected?.Id == model.Id)
        {
            Deselect();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AddElement(PageImage model)
    {
        var element = new Image
        {
            Source = Decode(model.Png),
            Stretch = Stretch.Fill,
            Width = model.Width,
            Height = model.Height,
        };

        Canvas.SetLeft(element, model.X);
        Canvas.SetTop(element, model.Y);

        images.Children.Add(element);
        _items.Add((model, element));
    }

    private static BitmapImage Decode(byte[] png)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Whether a finger at this page point should manipulate a picture rather than pan.
    /// Grips on the current selection win over the pictures themselves, so a grip sitting
    /// over a neighbouring image still resizes rather than selecting the one underneath.
    /// </summary>
    public bool HitTest(Point page)
    {
        if (_selected is not null && GripAt(page, Bounds(_selected)) != ImageGrip.None)
        {
            return true;
        }

        return TopmostAt(page) is not null;
    }

    private PageImage? TopmostAt(Point page)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (Bounds(_items[i].Model).Contains(page))
            {
                return _items[i].Model;
            }
        }

        return null;
    }

    private static Rect Bounds(PageImage model) => new(model.X, model.Y, model.Width, model.Height);

    private static ImageGrip GripAt(Point page, Rect bounds)
    {
        if (Near(page, bounds.TopLeft)) return ImageGrip.TopLeft;
        if (Near(page, bounds.TopRight)) return ImageGrip.TopRight;
        if (Near(page, bounds.BottomLeft)) return ImageGrip.BottomLeft;
        if (Near(page, bounds.BottomRight)) return ImageGrip.BottomRight;
        return bounds.Contains(page) ? ImageGrip.Move : ImageGrip.None;
    }

    private static bool Near(Point a, Point b) =>
        Math.Abs(a.X - b.X) <= GripRadius && Math.Abs(a.Y - b.Y) <= GripRadius;

    /// <summary>A finger went down. Returns true if this layer is taking the gesture.</summary>
    public bool Begin(Point page)
    {
        // A CORNER grip on the current selection takes priority over reselecting.
        //
        // This used to accept ImageGrip.Move as well, which quietly made triple-tap
        // impossible: the first tap selects, and from then on every tap inside the picture
        // matched Move and returned here — above the tap counter. A trace showed the count
        // logged exactly once and never again. Only the corners short-circuit now; a tap in
        // the body falls through to be counted, and still ends up dragging.
        if (_selected is not null)
        {
            var grip = GripAt(page, Bounds(_selected));
            if (grip is not (ImageGrip.None or ImageGrip.Move))
            {
                StartDrag(_selected, grip, page);
                return true;
            }
        }

        var hit = TopmostAt(page);
        if (hit is null)
        {
            // Tapping bare paper puts the picture down again.
            InkTrace.Log(InkEvent.ImageHit, -1, 0, 0);
            ResetTapRun();
            Deselect();
            return false;
        }

        var taps = CountTap(hit, page);

        // Field B is the consecutive-tap count: three in a row is a delete. If a trace shows
        // this stuck at 1, the run is being broken between taps rather than the threshold
        // being wrong.
        InkTrace.Log(InkEvent.ImageHit, (int)hit.Id, taps, 0);

        if (taps >= 3)
        {
            ResetTapRun();
            DeleteRequested?.Invoke(this, hit);
            return true;
        }

        Select(hit);
        StartDrag(hit, ImageGrip.Move, page);
        return true;
    }

    /// <summary>
    /// Counts consecutive taps on the same picture. The run resets if the finger lands
    /// somewhere else, on a different picture, or after a pause — so a triple-tap has to be
    /// deliberate rather than the tail of a fidgety repositioning.
    /// </summary>
    private int CountTap(PageImage hit, Point page)
    {
        var now = DateTime.UtcNow;
        var sameTarget = _tapTargetId == hit.Id;
        var soonEnough = (now - _lastTapAt).TotalMilliseconds <= MultiTapMilliseconds;
        var closeEnough = Math.Abs(page.X - _lastTapAt2.X) <= MultiTapSlop
            && Math.Abs(page.Y - _lastTapAt2.Y) <= MultiTapSlop;

        _tapCount = sameTarget && soonEnough && closeEnough ? _tapCount + 1 : 1;
        _tapTargetId = hit.Id;
        _lastTapAt = now;
        _lastTapAt2 = page;
        return _tapCount;
    }

    private void ResetTapRun()
    {
        _tapCount = 0;
        _tapTargetId = null;
    }

    private void StartDrag(PageImage model, ImageGrip grip, Point page)
    {
        _selected = model;
        _grip = grip;
        _dragOrigin = page;
        _dragStartBounds = Bounds(model);
    }

    public void Move(Point page)
    {
        if (_selected is null || _grip == ImageGrip.None)
        {
            return;
        }

        var dx = page.X - _dragOrigin.X;
        var dy = page.Y - _dragOrigin.Y;
        var start = _dragStartBounds;

        var bounds = _grip switch
        {
            ImageGrip.Move => new Rect(start.X + dx, start.Y + dy, start.Width, start.Height),
            ImageGrip.TopLeft => FromCorners(start.Right, start.Bottom, start.X + dx, start.Y + dy),
            ImageGrip.TopRight => FromCorners(start.X, start.Bottom, start.Right + dx, start.Y + dy),
            ImageGrip.BottomLeft => FromCorners(start.Right, start.Y, start.X + dx, start.Bottom + dy),
            _ => FromCorners(start.X, start.Y, start.Right + dx, start.Bottom + dy),
        };

        Apply(_selected, bounds);
    }

    /// <summary>
    /// Builds a rectangle from a fixed corner and a dragged one, preserving aspect ratio.
    /// </summary>
    /// <remarks>
    /// Corners scale rather than stretch: a pasted screenshot of a problem is unreadable
    /// once it has been squashed, and there is no obvious way back. Edge handles could offer
    /// free stretching later if it is ever wanted.
    /// </remarks>
    private Rect FromCorners(double anchorX, double anchorY, double dragX, double dragY)
    {
        var aspect = _dragStartBounds.Height / Math.Max(_dragStartBounds.Width, 1);

        var width = Math.Max(Math.Abs(dragX - anchorX), MinimumSize);
        var height = Math.Max(width * aspect, MinimumSize);
        width = height / Math.Max(aspect, 0.0001);

        var x = dragX < anchorX ? anchorX - width : anchorX;
        var y = dragY < anchorY ? anchorY - height : anchorY;
        return new Rect(x, y, width, height);
    }

    private void Apply(PageImage model, Rect bounds)
    {
        model.X = bounds.X;
        model.Y = bounds.Y;
        model.Width = bounds.Width;
        model.Height = bounds.Height;

        var element = _items.First(i => i.Model.Id == model.Id).Element;
        Canvas.SetLeft(element, bounds.X);
        Canvas.SetTop(element, bounds.Y);
        element.Width = bounds.Width;
        element.Height = bounds.Height;

        DrawSelection();
    }

    public void End()
    {
        if (_selected is not null && _grip != ImageGrip.None && Bounds(_selected) != _dragStartBounds)
        {
            BoundsCommitted?.Invoke(this, (_selected, _dragStartBounds));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        _grip = ImageGrip.None;
    }

    /// <summary>Puts a picture back to a given rectangle, for undo/redo.</summary>
    public void SetBounds(PageImage model, Rect bounds)
    {
        if (_items.Any(i => i.Model.Id == model.Id))
        {
            Apply(model, bounds);
        }
    }

    public void Select(PageImage model)
    {
        _selected = model;
        DrawSelection();
    }

    public void Deselect()
    {
        _selected = null;
        _grip = ImageGrip.None;
        selection.Children.Clear();
    }

    private void DrawSelection()
    {
        selection.Children.Clear();
        if (_selected is null)
        {
            return;
        }

        var bounds = Bounds(_selected);
        var accent = (Brush)Application.Current.FindResource("Accent");

        var frame = new Rectangle
        {
            Width = bounds.Width,
            Height = bounds.Height,
            Stroke = accent,
            StrokeThickness = 2,
            StrokeDashArray = [6, 4],
            Fill = Brushes.Transparent,
        };

        Canvas.SetLeft(frame, bounds.X);
        Canvas.SetTop(frame, bounds.Y);
        selection.Children.Add(frame);

        foreach (var corner in new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight })
        {
            // Sized for a fingertip, not a mouse cursor.
            const double size = 26;
            var grip = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = accent,
                Stroke = Brushes.White,
                StrokeThickness = 2,
            };

            Canvas.SetLeft(grip, corner.X - (size / 2));
            Canvas.SetTop(grip, corner.Y - (size / 2));
            selection.Children.Add(grip);
        }
    }
}
