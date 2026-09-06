using System.Windows;
using System.Windows.Input;
using NoteTaker.App.Services;

namespace NoteTaker.App.Controls;

/// <summary>
/// Touch navigation for the infinite canvas: one finger pans the camera, two fingers pinch
/// to zoom, and a stationary tap is forwarded so tutor highlights stay reachable.
/// </summary>
internal sealed class PageTouchGestures
{
    private const double TapSlop = 12;
    private const double TapMilliseconds = 450;

    private readonly FrameworkElement _viewport;
    private readonly Dictionary<int, Point> _contacts = [];

    private Point _anchor;
    private double _spread;
    private Point _tapOrigin;
    private DateTime _tapStart;
    private bool _moved;

    public PageTouchGestures(FrameworkElement viewport)
    {
        _viewport = viewport;

        _viewport.PreviewStylusDown += OnStylusDown;
        _viewport.PreviewStylusMove += OnStylusMove;
        _viewport.PreviewStylusUp += OnStylusUp;
        _viewport.PreviewStylusSystemGesture += OnSystemGesture;
    }

    /// <summary>
    /// Consulted before a one-finger pan begins. Returning true means a picture took the
    /// gesture — panning stands down for its duration, since dragging the paper and dragging
    /// something on the paper cannot both happen from one finger.
    /// </summary>
    public Func<Point, bool>? BeginImageGesture { get; set; }

    public Action<Point>? MoveImageGesture { get; set; }

    public Action? EndImageGesture { get; set; }

    /// <summary>Converts a viewport point into page coordinates for the image layer.</summary>
    public Func<Point, Point>? ToPagePoint { get; set; }

    private bool _draggingImage;

    public Func<double>? GetScale { get; set; }

    public Action<double, Point>? ScaleTo { get; set; }

    /// <summary>Pan the camera by a viewport-pixel delta.</summary>
    public Action<double, double>? PanBy { get; set; }

    public event EventHandler<Point>? Tapped;

    /// <summary>WM_POINTER touch contact, in viewport coordinates.</summary>
    public void Win32TouchDown(int pointerId, Point pointInViewport) =>
        BeginContact(pointerId, pointInViewport, countTouchClassifier: false);

    public void Win32TouchMove(int pointerId, Point pointInViewport) =>
        MoveContact(pointerId, pointInViewport);

    public void Win32TouchUp(int pointerId, Point pointInViewport) =>
        EndContact(pointerId, pointInViewport, countTouchClassifier: false);

    /// <summary>
    /// A contact Windows cancelled rather than lifted (WM_POINTERCAPTURECHANGED — typically
    /// palm rejection as the pen comes into range). Drops the contact without firing a tap:
    /// a cancelled contact is explicitly not a deliberate gesture.
    /// </summary>
    public void Win32TouchCancel(int pointerId)
    {
        if (_contacts.Remove(pointerId))
        {
            _moved = true; // suppress any pending tap interpretation
            Rebase();
        }
    }

    private void OnStylusDown(object sender, StylusDownEventArgs e)
    {
        if (!PenInkCanvas.IsTouch(e.StylusDevice))
        {
            return;
        }

        StylusDeviceClassifier.RememberDevice(e.StylusDevice);
        BeginContact(e.StylusDevice.Id, e.GetPosition(_viewport), countTouchClassifier: true);
        e.Handled = true;
    }

    private void OnStylusMove(object sender, StylusEventArgs e)
    {
        if (!PenInkCanvas.IsTouch(e.StylusDevice))
        {
            return;
        }

        var deviceId = e.StylusDevice.Id;

        // This runs on the Viewport, an ancestor of the ink canvas, so Handled here means the
        // canvas never sees the move at all. If it ever fires for the pen, that alone
        // truncates the stroke.
        InkTrace.Log(InkEvent.GestureSwallowed, deviceId);
        e.Handled = true;
        MoveContact(deviceId, e.GetPosition(_viewport));
    }

    private void OnStylusUp(object sender, StylusEventArgs e)
    {
        if (!PenInkCanvas.IsTouch(e.StylusDevice))
        {
            return;
        }

        e.Handled = true;
        EndContact(e.StylusDevice.Id, e.GetPosition(_viewport), countTouchClassifier: true);
    }

    private void OnSystemGesture(object sender, StylusSystemGestureEventArgs e)
    {
        if (PenInkCanvas.IsTouch(e.StylusDevice))
        {
            e.Handled = true;
        }
    }

    private void BeginContact(int id, Point point, bool countTouchClassifier)
    {
        if (countTouchClassifier)
        {
            StylusDeviceClassifier.TouchContactDown();
        }

        _contacts[id] = point;

        // Field A is the live contact count. One physical finger reporting 2 here means
        // something is registering it twice, which reads as a pinch.
        InkTrace.Log(InkEvent.TouchContact, _contacts.Count, id);

        if (_contacts.Count == 1)
        {
            _tapOrigin = point;
            _tapStart = DateTime.UtcNow;
            _moved = false;

            // First finger only: a second contact means a pinch, and a pinch should zoom the
            // page even if it started over a picture.
            if (ToPagePoint is { } toPage && BeginImageGesture is { } begin)
            {
                _draggingImage = begin(toPage(point));
            }
        }

        Rebase();
    }

    private void MoveContact(int id, Point point)
    {
        if (!_contacts.ContainsKey(id))
        {
            return;
        }

        _contacts[id] = point;

        if (_draggingImage && _contacts.Count == 1)
        {
            _moved = true;
            if (ToPagePoint is { } toPage)
            {
                MoveImageGesture?.Invoke(toPage(point));
            }

            Rebase();
            return;
        }

        var anchor = Centroid();
        var spread = Spread();

        var shiftX = anchor.X - _anchor.X;
        var shiftY = anchor.Y - _anchor.Y;

        if (Math.Abs(anchor.X - _tapOrigin.X) > TapSlop || Math.Abs(anchor.Y - _tapOrigin.Y) > TapSlop)
        {
            _moved = true;
        }

        PanBy?.Invoke(shiftX, shiftY);

        if (_contacts.Count >= 2 && _spread > 0 && spread > 0)
        {
            var ratio = Math.Clamp(spread / _spread, 0.5, 2.0);
            if (Math.Abs(ratio - 1) > 0.002 && GetScale is { } read && ScaleTo is { } write)
            {
                write(read() * ratio, anchor);
                _moved = true;
            }
        }

        _anchor = anchor;
        _spread = spread;
    }

    private void EndContact(int id, Point point, bool countTouchClassifier)
    {
        if (countTouchClassifier)
        {
            StylusDeviceClassifier.TouchContactUp();
        }

        _contacts[id] = point;

        if (!_contacts.Remove(id))
        {
            return;
        }

        if (_draggingImage && _contacts.Count == 0)
        {
            EndImageGesture?.Invoke();
            _draggingImage = false;
            Rebase();
            return;
        }

        var wasTap = _contacts.Count == 0
            && !_moved
            && (DateTime.UtcNow - _tapStart).TotalMilliseconds < TapMilliseconds;

        if (wasTap)
        {
            Tapped?.Invoke(this, _tapOrigin);
        }

        Rebase();
    }

    private void Rebase()
    {
        _anchor = Centroid();
        _spread = Spread();
    }

    private Point Centroid()
    {
        if (_contacts.Count == 0)
        {
            return default;
        }

        double x = 0;
        double y = 0;

        foreach (var point in _contacts.Values)
        {
            x += point.X;
            y += point.Y;
        }

        return new Point(x / _contacts.Count, y / _contacts.Count);
    }

    private double Spread()
    {
        if (_contacts.Count < 2)
        {
            return 0;
        }

        var points = _contacts.Values.ToList();
        var widest = 0.0;

        for (var i = 0; i < points.Count - 1; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                widest = Math.Max(widest, (points[i] - points[j]).Length);
            }
        }

        return widest;
    }
}
