using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Threading;
using NoteTaker.App.Services;

namespace NoteTaker.App.Controls;

/// <summary>Preview rectangle for the eraser tip, in page coordinates.</summary>
public readonly record struct EraserPreviewArgs(bool Visible, Rect Bounds);

/// <summary>
/// The barrel-button lasso as it is being drawn, in page coordinates. <paramref name="Closed"/>
/// marks the pen lifting, which is when the enclosed content becomes a selection.
/// </summary>
public readonly record struct LassoArgs(IReadOnlyList<Point> Path, bool Closed);

/// <summary>
/// Pen/mouse ink only. Finger never inks. Surface Pen eraser tip erases with a
/// OneNote-style square that grows with tip speed while in contact.
/// </summary>
public sealed class PenInkCanvas : InkCanvas
{
    private const double MinEraserSize = 14;
    private const double MaxEraserSize = 180;
    private const double BaseEraserSize = 18;
    /// <summary>Speed (page-px/s) that maps near the soft mid of the size curve.</summary>
    private const double SpeedMid = 1200;
    private const double SizeLerp = 0.2;
    private const double DeadzonePx = 1.5;
    private const double MinSampleMs = 32;

    private readonly RejectTouchPlugIn _rejectTouch = new();
    private InkCanvasEditingMode _writingMode = InkCanvasEditingMode.Ink;
    private bool _eraseTool;
    private bool _eraserTipActive;
    private bool _dropNextStroke;

    /// <summary>
    /// Whether the mouse contact currently down is being ignored as a finger. Latched at
    /// pen-down and held until the up, rather than re-decided per event.
    /// </summary>
    /// <remarks>
    /// <see cref="ShouldIgnoreAsNonPen"/> consults a live touch counter, so its answer can
    /// change in the middle of a drag. When it flipped between the down and the up, one end of
    /// the gesture was swallowed and the other was not — and WPF's ink collection, having been
    /// handed an up it had no down for, tried to commit a stroke with no points:
    /// "StylusPointCollection cannot be empty when attached to a Stroke", thrown from
    /// InkCollectionBehavior.StylusInputEnd inside InputManager.ProcessStagingArea. Nothing
    /// downstream can catch that — it is raised from a class handler on the event route, not
    /// from the base call here — so the only cure is to never hand WPF a half gesture.
    /// </remarks>
    private bool _swallowingMouseContact;
    private int? _strokeDeviceId;
    private bool _win32Eraser;

    /// <summary>
    /// The pen tip is physically down, according to WM_POINTER. Authoritative over the
    /// touch-contact counter: when both say yes, a real pen is writing while a palm rests.
    /// </summary>
    private bool _win32PenInContact;

    /// <summary>
    /// Whether WM_POINTER confirmed pen contact at any point during the stroke being drawn.
    /// </summary>
    private bool _penSeenThisStroke;

    /// <summary>
    /// Whether WM_POINTER has EVER reported pen contact. Guards the palm filter below: on a
    /// machine where the pointer hook never reports a pen, "no pen seen" says nothing about
    /// the stroke, and filtering on it would throw away everything the user writes.
    /// </summary>
    private bool _win32PenEverSeen;

    private bool _eraserContact;

    private double _eraserSize = BaseEraserSize;
    private Point? _lastEraserPoint;
    private Point? _lastEraseStamp;
    private Point _sampleOrigin;
    private long _sampleStartTicks;
    private double _smoothedSpeed;
    private string _eraserSource = "none";

    public PenInkCanvas()
    {
        DynamicRenderer = new PenOnlyDynamicRenderer(_rejectTouch)
        {
            DrawingAttributes = DefaultDrawingAttributes.Clone(),
        };

        var rendererIndex = StylusPlugIns.IndexOf(DynamicRenderer);
        if (rendererIndex < 0)
        {
            StylusPlugIns.Insert(0, _rejectTouch);
        }
        else
        {
            StylusPlugIns.Insert(rendererIndex, _rejectTouch);
        }

        // WPF's ink cursor is a dot the size of the pen nib. Under a pen that is invisible and
        // irrelevant — the tip is the pointer — but under a mouse it replaces the arrow with a
        // few dark pixels on a dark page, so there is nothing to aim menus, handles or the
        // scrollbar with. UseCustomCursor is what stops InkCanvas recomputing it per tool.
        UseCustomCursor = true;
        Cursor = Cursors.Arrow;

        EditingModeInverted = InkCanvasEditingMode.EraseByPoint;
        ApplyEraserShape();

        StrokeCollected += OnStrokeCollected;
        FollowStrokeCollection();
    }

    /// <summary>The collection currently hooked, so the subscription can be moved with it.</summary>
    private StrokeCollection? _subscribedStrokes;

    /// <summary>
    /// Points <see cref="OnStrokesChangedGuard"/> at whichever collection the canvas is
    /// actually using now.
    /// </summary>
    /// <remarks>
    /// Assigning <see cref="InkCanvas.Strokes"/> — which loading a page does — swaps in a new
    /// instance and silently leaves the old subscription attached to the orphan. A trace
    /// confirmed it: the canvas reported its live collection was not the one we had hooked,
    /// so the off-page guard had been dead for the whole session.
    /// </remarks>
    private void FollowStrokeCollection()
    {
        if (ReferenceEquals(_subscribedStrokes, Strokes))
        {
            return;
        }

        if (_subscribedStrokes is not null)
        {
            _subscribedStrokes.StrokesChanged -= OnStrokesChangedGuard;
        }

        _subscribedStrokes = Strokes;
        _subscribedStrokes.StrokesChanged += OnStrokesChangedGuard;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == StrokesProperty)
        {
            FollowStrokeCollection();
        }
    }

    public event EventHandler<EraserPreviewArgs>? EraserPreviewChanged;

    /// <summary>The barrel-button lasso being drawn, or closing.</summary>
    /// <summary>The barrel-button lasso being drawn, or closing.</summary>
    public event EventHandler<LassoArgs>? LassoChanged;

    /// <summary>
    /// Path of the lasso in progress, in page coordinates; null when not lassoing. Its
    /// non-null-ness IS the "selection mode is active" flag, which is why nothing clears it
    /// until the pen lifts — releasing the barrel button mid-gesture must not end the lasso.
    /// </summary>
    private List<Point>? _lassoPath;

    public void SetWritingMode(InkCanvasEditingMode mode)
    {
        // EraseByPoint tip size is sticky for the whole gesture in WPF — we stamp-erase
        // ourselves so the dashed preview always matches what is removed.
        _eraseTool = mode == InkCanvasEditingMode.EraseByPoint;
        _writingMode = _eraseTool ? InkCanvasEditingMode.None : mode;
        EditingModeInverted = InkCanvasEditingMode.None;
        ApplyEraserShape();

        if (!_eraserTipActive && !_win32Eraser)
        {
            EditingMode = _writingMode;
        }

        if (!_eraseTool)
        {
            EndEraserContact();
        }
    }

    /// <summary>Called from the WM_POINTER hook when WPF's tablet device list is empty.</summary>
    internal void NotifyWin32Pen(bool inverted, bool inRange, bool inContact, Point? pagePoint)
    {
        // WM_POINTER is the ONLY place this app can still tell a pen from a finger once WPF's
        // stylus stack is inactive: at that point the pen arrives as a plain mouse with a null
        // StylusDevice, indistinguishable from a promoted finger to the mouse handlers below.
        // A trace on the target device showed exactly that — every stroke collected with no
        // stylus event of any kind behind it — which meant the "is a finger down?" guard was
        // swallowing the pen's own moves whenever a palm rested on the glass.
        _win32PenInContact = inRange && inContact;
        if (_win32PenInContact)
        {
            _win32PenEverSeen = true;
            _penSeenThisStroke = true;
        }

        if (!inRange)
        {
            _win32PenInContact = false;
            if (_win32Eraser)
            {
                _win32Eraser = false;
                ApplyEraserTip(false);
            }

            EndEraserContact();
            _dropNextStroke = false;
            return;
        }

        _win32Eraser = inverted;
        if (!inverted)
        {
            _dropNextStroke = false;
        }

        ApplyEraserTip(inverted);

        var erasing = inverted || _eraseTool;
        if (erasing && inContact && pagePoint is { } point)
        {
            _eraserSource = "win32";
            TrackEraserContact(point);
        }
        else if (!inContact)
        {
            EndEraserContact();
        }
    }

    internal static bool IsTouch(StylusDevice? device)
    {
        if (device is null)
        {
            return false;
        }

        try
        {
            StylusDeviceClassifier.RememberDevice(device);

            // The device's own reported type is authoritative and settles it either way.
            //
            // This used to be `type == Touch || IsTouchId(id)`, which let a cached id verdict
            // OVERRULE a device that plainly said it was a pen. One bad cache entry then
            // classified the pen as a finger for the rest of the session: its stylus events
            // were swallowed as touch, WPF promoted the swallowed events to mouse, the canvas
            // never saw a coherent stroke, and every trace vanished the moment the pen lifted.
            // The id cache is a fallback for input that arrives with no device to ask — it
            // must never contradict one that does.
            switch (device.TabletDevice?.Type)
            {
                case TabletDeviceType.Touch:
                    return true;
                case TabletDeviceType.Stylus:
                    return false;
            }

            return StylusDeviceClassifier.IsTouchId(device.Id);
        }
        catch (InvalidOperationException)
        {
            return StylusDeviceClassifier.IsTouchId(device.Id);
        }
    }

    internal static bool IsTouchPromotedMouse(MouseEventArgs e) =>
        e.StylusDevice is not null && IsTouch(e.StylusDevice);

    /// <summary>
    /// Whether this mouse event is anything other than the pen, and so must not leave ink.
    /// </summary>
    /// <remarks>
    /// The page takes pen only. A finger scrolls and pinches, and a mouse points at things —
    /// neither writes, because a stray drag with either is a mark on work you cannot easily
    /// separate from your own hand.
    ///
    /// Which channel decides matters here, and getting it wrong disables the pen. An ink trace
    /// on this machine showed WPF's stylus stack failing to engage at all: every stroke arrived
    /// as plain mouse with a null StylusDevice. Treating "no StylusDevice" as "not a pen" would
    /// therefore have rejected the PEN whenever that happened, and the page would take no ink
    /// from anything.
    ///
    /// WM_POINTER keeps reporting PT_PEN correctly in exactly that situation — it is the whole
    /// reason PointerInputGuard exists — so a pen in contact by EITHER channel wins. Only a
    /// contact that neither channel can call a pen is discarded.
    /// </remarks>
    private bool ShouldIgnoreAsNonPen(MouseEventArgs e)
    {
        if (IsTouchPromotedMouse(e))
        {
            return true;
        }

        // Carrying a StylusDevice that is not touch means WPF has already called it a pen.
        if (e.StylusDevice is not null)
        {
            return false;
        }

        return !_win32PenInContact;
    }

    private static bool IsEraserTip(StylusDevice? device)
    {
        if (device is null)
        {
            return false;
        }

        try
        {
            if (device.Inverted)
            {
                return true;
            }

            var name = device.ToString() ?? string.Empty;
            if (name.Contains("eraser", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (StylusButton button in device.StylusButtons)
            {
                var buttonName = button.Name ?? string.Empty;
                if (buttonName.Contains("eraser", StringComparison.OrdinalIgnoreCase)
                    && button.StylusButtonState == StylusButtonState.Down)
                {
                    return true;
                }
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return false;
    }

    private void ApplyEraserTip(bool active)
    {
        if (active == _eraserTipActive)
        {
            return;
        }

        _eraserTipActive = active;

        if (active)
        {
            ApplyEraserShape();
            // None: block ink; erasure is done manually to match the preview box.
            EditingMode = InkCanvasEditingMode.None;
            EditingModeInverted = InkCanvasEditingMode.None;
        }
        else
        {
            EditingMode = _writingMode;
            EditingModeInverted = InkCanvasEditingMode.None;
            EndEraserContact();
        }
    }

    private void ApplyEraserShape()
    {
        var size = Math.Clamp(_eraserSize, MinEraserSize, MaxEraserSize);
        EraserShape = new RectangleStylusShape(size, size);
    }

    private void TrackEraserContact(Point pagePoint)
    {
        // Win32 is authoritative on this Surface — ignore duplicate mouse samples that fight it.
        if (_win32Eraser && _eraserSource == "mouse")
        {
            return;
        }

        _eraserContact = true;
        var now = Environment.TickCount64;

        if (_sampleStartTicks == 0)
        {
            _sampleOrigin = pagePoint;
            _sampleStartTicks = now;
            _smoothedSpeed = 0;
            _lastEraserPoint = pagePoint;
        }
        else
        {
            var dtMs = now - _sampleStartTicks;
            var distance = (pagePoint - _sampleOrigin).Length;

            // Sample over a window so sub-pixel / 1ms jitter cannot spike speed.
            if (dtMs >= MinSampleMs)
            {
                var instantSpeed = distance < DeadzonePx ? 0 : (distance * 1000.0 / dtMs);
                _smoothedSpeed = (_smoothedSpeed * 0.75) + (instantSpeed * 0.25);
                _sampleOrigin = pagePoint;
                _sampleStartTicks = now;
            }
        }

        var speedT = 1.0 - Math.Exp(-_smoothedSpeed / SpeedMid);
        var target = BaseEraserSize + ((MaxEraserSize - BaseEraserSize) * speedT);
        _eraserSize += (target - _eraserSize) * SizeLerp;
        _eraserSize = Math.Clamp(_eraserSize, MinEraserSize, MaxEraserSize);

        ApplyEraserShape();
        ShowEraserPreview(pagePoint);
        EraseWithCurrentTip(pagePoint);
        _lastEraserPoint = pagePoint;
    }

    /// <summary>
    /// Stamp-erase with the current preview size. WPF's built-in EraseByPoint freezes the tip
    /// size at gesture start, which made the dashed box larger than the real eraser.
    /// </summary>
    private void EraseWithCurrentTip(Point center)
    {
        var size = _eraserSize;
        var shape = new RectangleStylusShape(size, size);
        var path = new List<Point>();

        if (_lastEraseStamp is { } prev)
        {
            var dist = (center - prev).Length;
            var steps = Math.Max(1, (int)Math.Ceiling(dist / Math.Max(3.0, size * 0.2)));
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                path.Add(new Point(
                    prev.X + ((center.X - prev.X) * t),
                    prev.Y + ((center.Y - prev.Y) * t)));
            }
        }
        else
        {
            path.Add(center);
        }

        _lastEraseStamp = center;

        // Everything the tip could possibly have touched this sample, with the tip's own size
        // added on each side. Strokes outside it cannot be affected, and rejecting them with a
        // rectangle comparison instead of GetEraseResult is what keeps this loop cheap.
        //
        // Without it, erasing ran full stroke-splitting geometry against EVERY stroke on the
        // page on EVERY move sample — around 140 strokes at ~7ms intervals late in a session.
        // A trace measured the UI thread held for over a second at a stretch, with no
        // dispatcher operation to blame, which is exactly the eraser "freezing".
        var swept = new Rect(path[0], path[^1]);
        swept.Union(new Rect(center, center));
        swept.Inflate(size, size);

        var replacements = new List<(Stroke Original, StrokeCollection Pieces)>();
        foreach (Stroke stroke in Strokes)
        {
            if (!stroke.GetBounds().IntersectsWith(swept))
            {
                continue;
            }

            StrokeCollection? pieces;
            try
            {
                pieces = stroke.GetEraseResult(path, shape);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (pieces is null)
            {
                continue;
            }

            replacements.Add((stroke, pieces));
        }

        if (replacements.Count == 0)
        {
            return;
        }

        foreach (var (original, pieces) in replacements)
        {
            Strokes.Remove(original);
            foreach (Stroke piece in pieces)
            {
                Strokes.Add(piece);
            }
        }
    }

    private void EndEraserContact()
    {
        if (!_eraserContact && _lastEraserPoint is null)
        {
            HideEraserPreview();
            return;
        }

        _eraserContact = false;
        HideEraserPreview();
        ResetEraserMotion();
    }

    private void ShowEraserPreview(Point center)
    {
        var size = _eraserSize;
        var bounds = new Rect(center.X - (size / 2), center.Y - (size / 2), size, size);
        EraserPreviewChanged?.Invoke(this, new EraserPreviewArgs(true, bounds));
    }

    private void HideEraserPreview() =>
        EraserPreviewChanged?.Invoke(this, new EraserPreviewArgs(false, Rect.Empty));

    private void ResetEraserMotion()
    {
        _lastEraserPoint = null;
        _lastEraseStamp = null;
        _sampleStartTicks = 0;
        _smoothedSpeed = 0;
        _eraserSize = BaseEraserSize;
        ApplyEraserShape();
    }

    private bool IsEraserModeActive() =>
        _eraserTipActive || _win32Eraser || _eraseTool;

    protected override void OnStylusInRange(StylusEventArgs e)
    {
        StylusDeviceClassifier.RememberDevice(e.StylusDevice);

        if (!IsTouch(e.StylusDevice))
        {
            ApplyEraserTip(IsEraserTip(e.StylusDevice));
        }

        base.OnStylusInRange(e);
    }

    protected override void OnStylusOutOfRange(StylusEventArgs e)
    {
        if (!IsTouch(e.StylusDevice) && _eraserTipActive)
        {
            ApplyEraserTip(false);
        }

        EndEraserContact();
        base.OnStylusOutOfRange(e);
    }

    protected override void OnPreviewStylusInAirMove(StylusEventArgs e)
    {
        if (!IsTouch(e.StylusDevice))
        {
            ApplyEraserTip(IsEraserTip(e.StylusDevice));
            // Hover only — never show the box until the tip is down.
            EndEraserContact();
        }

        base.OnPreviewStylusInAirMove(e);
    }

    protected override void OnPreviewStylusDown(StylusDownEventArgs e)
    {
        var deviceId = e.StylusDevice?.Id ?? -1;
        RecoverStuckCapture(deviceId);
        StylusDeviceClassifier.RememberDevice(e.StylusDevice);

        var touch = IsTouch(e.StylusDevice);
        var eraser = !touch && IsEraserTip(e.StylusDevice);

        if (touch)
        {
            InkTrace.Log(InkEvent.TouchDown, e.StylusDevice?.Id ?? -1);
            e.Handled = true;
            return;
        }

        // Barrel held at touchdown means selecting, not writing.
        //
        // Note what is NOT done here: e.Handled is never set. Under EnablePointerSupport —
        // which this app requires, see the csproj — WPF's own PointerLogic.PromotePreviewToMain
        // throws a NullReferenceException when it tries to promote a HANDLED preview stylus
        // event, from inside InputManager.ProcessStagingArea where nothing can catch it. That
        // has now bitten this exact branch twice.
        //
        // So the lasso suppresses ink in two places that cost WPF nothing: the dynamic renderer
        // skips the contact entirely (no wet ink), and the stroke the canvas goes on to build
        // from real packets is discarded at pen-up. Parking the packets instead was tried and
        // is worse — it left the collection empty and WPF threw "StylusPointCollection cannot
        // be empty" from InkCollectionBehavior.StylusInputEnd.
        if (_rejectTouch.IsLassoContact)
        {
            _lassoPath = [e.GetPosition(this)];
            LassoChanged?.Invoke(this, new LassoArgs(_lassoPath, false));
            base.OnPreviewStylusDown(e);
            return;
        }

        InkTrace.Log(InkEvent.StylusDown, deviceId, eraser ? 1 : 0);

        // Whether the canvas is actually in a mode that BUILDS strokes. A pen-down that is
        // neither collected nor dropped never produced a stroke in the first place, and the
        // only thing upstream of collection that can cause that is the editing mode.
        InkTrace.Log(
            InkEvent.CanvasState,
            (int)EditingMode,
            (_eraserTipActive ? 1 : 0) | (_win32Eraser ? 2 : 0) | (_eraseTool ? 4 : 0),
            DefaultDrawingAttributes.Width);
        _dropNextStroke = false;
        ClaimStroke(deviceId);
        ApplyEraserTip(eraser);
        if (eraser || IsEraserModeActive())
        {
            _eraserSource = "stylus";
            TrackEraserContact(e.GetPosition(this));
        }

        base.OnPreviewStylusDown(e);
    }

    protected override void OnPreviewStylusMove(StylusEventArgs e)
    {
        if (IsTouch(e.StylusDevice))
        {
            // If this fires with the id that is mid-stroke, the classifier just changed its
            // mind about the pen and the rest of the stroke is being thrown away.
            InkTrace.Log(InkEvent.PreviewSwallowed, e.StylusDevice?.Id ?? -1, _strokeDeviceId ?? -1);
            e.Handled = true;
            return;
        }

        if (_lassoPath is not null)
        {
            AppendLassoPoint(e.GetPosition(this));
            base.OnPreviewStylusMove(e);
            return;
        }

        InkTrace.Log(InkEvent.StylusMove, e.StylusDevice?.Id ?? -1);

        if (_eraserContact)
        {
            TrackEraserContact(e.GetPosition(this));
        }

        base.OnPreviewStylusMove(e);
    }

    protected override void OnPreviewStylusUp(StylusEventArgs e)
    {
        InkTrace.Log(InkEvent.StylusUp, e.StylusDevice?.Id ?? -1);

        if (_lassoPath is not null)
        {
            var path = _lassoPath;
            _lassoPath = null;
            LassoChanged?.Invoke(this, new LassoArgs(path, true));

            // The canvas has been collecting a real stroke behind the lasso the whole time —
            // invisibly, since the renderer skipped it. Discard it here rather than letting
            // the outline persist as ink.
            _dropNextStroke = true;
            base.OnPreviewStylusUp(e);
            return;
        }

        // Posted, not inline: this is the PREVIEW of the up, which runs before the canvas has
        // had a chance to collect anything. Reading the stroke count here would always miss
        // the stroke we are asking about.
        Dispatcher.BeginInvoke(LogCanvasTruth, DispatcherPriority.Background);

        if (IsTouch(e.StylusDevice))
        {
            e.Handled = true;
            return;
        }

        EndEraserContact();
        base.OnPreviewStylusUp(e);
    }

    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        StylusDeviceClassifier.RememberDevice(e.StylusDevice);

        if (IsTouch(e.StylusDevice))
        {
            e.Handled = true;
            return;
        }

        _dropNextStroke = false;
        ClaimStroke(e.StylusDevice.Id);
        ApplyEraserTip(IsEraserTip(e.StylusDevice));
        base.OnStylusDown(e);
    }

    protected override void OnStylusMove(StylusEventArgs e)
    {
        if (IsTouch(e.StylusDevice))
        {
            e.Handled = true;
            return;
        }

        base.OnStylusMove(e);
    }

    protected override void OnStylusUp(StylusEventArgs e)
    {
        if (IsTouch(e.StylusDevice))
        {
            e.Handled = true;
            return;
        }

        EndEraserContact();
        base.OnStylusUp(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        RecoverStuckCapture(e.StylusDevice?.Id ?? -1);

        // Decided once, for the whole contact. See _swallowingMouseContact.
        _swallowingMouseContact = ShouldIgnoreAsNonPen(e);

        if (_swallowingMouseContact)
        {
            StylusDeviceClassifier.RememberDevice(e.StylusDevice);
            InkTrace.Log(InkEvent.PreviewSwallowed, -1, 0, StylusDeviceClassifier.ActiveTouches);
            e.Handled = true;
            return;
        }

        InkTrace.Log(InkEvent.MouseDown, _win32PenInContact ? 1 : 0);

        // Mirrors the stylus path's pen-down probe. Both state probes used to live only on the
        // stylus handlers, so in exactly the sessions worth diagnosing — stylus stack dead,
        // everything arriving as mouse — the canvas's state during the failing drag was
        // unrecorded, and had to be argued about instead of read.
        InkTrace.Log(
            InkEvent.CanvasState,
            (int)EditingMode,
            _eraserTipActive ? 1 : 0,
            DefaultDrawingAttributes.Width);

        _penSeenThisStroke = _win32PenInContact;
        _dropNextStroke = false;
        if (IsEraserModeActive() && !_win32Eraser)
        {
            _eraserSource = "mouse";
            TrackEraserContact(e.GetPosition(this));
        }

        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        // Mid-drag, the contact's verdict from pen-down wins. Between drags there is no
        // contact to be consistent with, so ask afresh — that is what keeps a resting palm
        // from panning the page while no button is held.
        var ignore = e.LeftButton == MouseButtonState.Pressed
            ? _swallowingMouseContact
            : ShouldIgnoreAsNonPen(e);

        if (ignore)
        {
            InkTrace.Log(InkEvent.PreviewSwallowed, -1, 1, StylusDeviceClassifier.ActiveTouches);
            e.Handled = true;
            return;
        }

        // Only while the tip is actually down: hover moves are not what fluidity depends on.
        // The gap between consecutive entries IS the sample rate the ink is built from, which
        // is the difference between a smooth curve and a three-point approximation of one.
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            InkTrace.Log(InkEvent.MouseMove, _win32PenInContact ? 1 : 0);
        }

        if (IsEraserModeActive() && e.LeftButton == MouseButtonState.Pressed && !_win32Eraser)
        {
            _eraserSource = "mouse";
            TrackEraserContact(e.GetPosition(this));
        }

        base.OnPreviewMouseMove(e);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        var ignore = _swallowingMouseContact;
        _swallowingMouseContact = false;

        if (ignore)
        {
            InkTrace.Log(InkEvent.PreviewSwallowed, -1, 2, StylusDeviceClassifier.ActiveTouches);
            e.Handled = true;
            return;
        }

        EndEraserContact();
        Dispatcher.BeginInvoke(LogCanvasTruth, DispatcherPriority.Background);
        base.OnPreviewMouseUp(e);
    }

    protected override void OnTouchDown(TouchEventArgs e)
    {
        StylusDeviceClassifier.TouchContactDown();

        // Capture so the contact is guaranteed an ending.
        //
        // Windows retires a contact with WM_POINTERCAPTURECHANGED instead of an up — palm
        // rejection does exactly this when the pen comes into range, which is the common case
        // here. Without a capture there is no lost-capture event either, so the increment
        // above had no matching decrement and the counter could only climb. A permanently
        // non-zero count makes ShouldIgnoreAsNonPen swallow every promoted-mouse event for the
        // rest of the session: no drawing, no panning, nothing.
        e.TouchDevice.Capture(this);
        InkTrace.Log(InkEvent.TouchContact, StylusDeviceClassifier.ActiveTouches, 1);
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e) => e.Handled = true;

    protected override void OnTouchUp(TouchEventArgs e)
    {
        ReleaseTouchContact(e);
        e.Handled = true;
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        // The contact was cancelled rather than lifted. Same bookkeeping either way.
        ReleaseTouchContact(e);
        base.OnLostTouchCapture(e);
    }

    private void ReleaseTouchContact(TouchEventArgs e)
    {
        if (ReferenceEquals(e.TouchDevice.Captured, this))
        {
            e.TouchDevice.Capture(null);
        }

        StylusDeviceClassifier.TouchContactUp();
        InkTrace.Log(InkEvent.TouchContact, StylusDeviceClassifier.ActiveTouches, 0);
    }

    /// <summary>
    /// First contact of a stroke wins, and keeps ownership until that stroke is collected.
    /// </summary>
    /// <remarks>
    /// This used to assign unconditionally, which reopened — one layer down — the same bug
    /// the <see cref="OnStrokeCollected"/> comment describes. A palm landing mid-stroke only
    /// returns early from the handlers above when <c>IsTouch</c> already knows it is touch;
    /// on the first contact of a new device that classification is not populated yet, so the
    /// palm fell through and overwrote the id belonging to the stroke the pen was still
    /// drawing. By the time the pen lifted, <c>RememberDevice</c> HAD learned that id was a
    /// finger, so <see cref="OnStrokeCollected"/> looked up the palm's id, got "touch", and
    /// deleted the pen's stroke. Refusing to reassign mid-stroke closes that window: the
    /// slot is cleared only when the stroke it belongs to is collected.
    ///
    /// A leftover id from a stroke that was never collected (an erase gesture, say) is
    /// harmless — only a touch id can trigger a drop, and touch never reaches here.
    /// </remarks>
    private void ClaimStroke(int deviceId) => _strokeDeviceId ??= deviceId;

    /// <summary>
    /// Releases input capture left behind by a contact that never finished, so this one can ink.
    /// </summary>
    /// <remarks>
    /// A contact interrupted mid-stroke — a low-battery toast did it — can end without the
    /// canvas ever completing its stroke. Capture stays held, and from that moment the canvas
    /// builds nothing at all: pen-down and pen-up keep arriving, EditingMode is still Ink, and
    /// no stroke is ever collected OR dropped. The wet-ink renderer runs independently, so ink
    /// still appears under the tip and is gone the instant the pen lifts — from the page, that
    /// is indistinguishable from every stroke being thrown away, and it never recovers.
    ///
    /// One trace pins it exactly: CanvasTruth flags 7 at every pen-up for nineteen minutes, then
    /// 15 — IsStylusCaptureWithin — from one interrupted contact onward, with Strokes.Count
    /// frozen at 122 while four further contacts came and went.
    ///
    /// A fresh contact never inherits a live capture, so finding one here means the previous one
    /// did not clean up. Releasing costs nothing when the guess is wrong and restores writing
    /// when it is right, which is the right trade for a state the student cannot otherwise
    /// escape without restarting.
    /// </remarks>
    private void RecoverStuckCapture(int arriving)
    {
        if (!IsStylusCaptureWithin && !IsMouseCaptureWithin)
        {
            return;
        }

        InkTrace.Log(InkEvent.CaptureRecovered, arriving, _strokeDeviceId ?? -1);

        // Released through the static devices rather than this element's own Release* methods:
        // the capture may sit on a child, in which case releasing "this" does nothing.
        Stylus.Capture(null);
        Mouse.Capture(null);

        // The abandoned stroke's device id would otherwise be the one OnStrokeCollected judges
        // the NEXT stroke by, and a stale "drop this" would take the recovered stroke with it.
        _strokeDeviceId = null;
        _dropNextStroke = false;
    }

    /// <summary>
    /// Adds a lasso sample, thinning out the ones too close to matter. The pen reports every
    /// few milliseconds; keeping all of it would make the containment test walk thousands of
    /// edges for an outline whose detail nobody can see.
    /// </summary>
    private void AppendLassoPoint(Point point)
    {
        const double minimumStep = 2.0;

        if (_lassoPath is null)
        {
            return;
        }

        var last = _lassoPath[^1];
        if (Math.Abs(point.X - last.X) < minimumStep && Math.Abs(point.Y - last.Y) < minimumStep)
        {
            return;
        }

        _lassoPath.Add(point);
        LassoChanged?.Invoke(this, new LassoArgs(_lassoPath, false));
    }


    /// <summary>
    /// Reads the canvas itself rather than trusting our own hooks, which are exactly what is
    /// in question when a stroke is neither collected nor dropped.
    /// </summary>
    private void LogCanvasTruth()
    {
        var flags = (ReferenceEquals(Strokes, _subscribedStrokes) ? 1 : 0)
            | (IsEnabled ? 2 : 0)
            | (IsHitTestVisible ? 4 : 0)
            | (IsStylusCaptureWithin ? 8 : 0);

        InkTrace.Log(InkEvent.CanvasTruth, Strokes.Count, flags, StylusPlugIns.Count);
    }

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        // Judge the stroke by the device that actually DREW it (_strokeDeviceId, captured at
        // StylusDown), never by whatever device happens to be current now.
        //
        // This fires on stylus-UP. Testing Stylus.CurrentStylusDevice here — as this did —
        // asks "is a finger touching the glass at this instant?", which is a different
        // question entirely: planting your hand to write a small tight superscript, or
        // re-planting between the two bars of an "=", routinely puts a palm down at the exact
        // moment the pen lifts. That deleted a perfectly good pen stroke the wet-ink renderer
        // had already drawn — ink appears while writing, then vanishes on lift. That is
        // precisely the reported "it skips some strokes".
        var byTouchDevice = _strokeDeviceId is int id && StylusDeviceClassifier.IsTouchId(id);
        var offPage = StrokeIsOffPageJunk(e.Stroke);

        // Last-resort palm filter, for the mouse-promoted path ONLY.
        //
        // It exists because when the pen arrives as promoted mouse there is no device to ask,
        // and a trace separated 63 such strokes perfectly on WM_POINTER contact alone: all 58
        // it confirmed were real writing, all 5 it did not were single-point dots — a palm.
        // But it is a heuristic, and it cannot tell a palm dot from a decimal point or a
        // multiplication dot, so it must never run when something better is available.
        //
        // Once the stylus stack is live (EnablePointerSupport) every stroke carries a real
        // device id and a palm is caught properly by byTouchDevice above — at which point
        // this would only be able to do harm. A trace after that switch showed it dropping
        // four strokes it had no business judging. So: only when there is no device.
        // Stands down the moment the stylus stack proves itself, exactly as the paragraph above
        // requires. It had been running anyway: two traces from one evening show it eating 39 and
        // 47 strokes while EnablePointerSupport was live and the probe was logging 35 StylusDown
        // against 0 MouseDown. Every one of those was a one- or two-point stroke — a decimal
        // point, a multiplication dot, the dot on an i, a short tick — which is precisely what
        // the comment above says it cannot tell from a palm.
        var palmDot = _strokeDeviceId is null
            && _win32PenEverSeen
            && !_penSeenThisStroke
            && !StylusDeviceClassifier.StylusStackIsLive
            && e.Stroke.StylusPoints.Count <= 2;

        var drop = _dropNextStroke || byTouchDevice || offPage || palmDot;

        // Point count and elapsed time are what separate the two failure modes that look
        // identical on the page: a stroke truncated by rejection ends early with a normal
        // sample density, while one flattened by a dispatcher stall spans its full duration
        // with almost no samples in it.
        InkTrace.Log(
            drop ? InkEvent.StrokeDropped : InkEvent.StrokeCollected,
            _strokeDeviceId ?? -1,
            e.Stroke.StylusPoints.Count,
            drop
                ? _dropNextStroke ? 1 : byTouchDevice ? 2 : offPage ? 3 : palmDot ? 4 : 0
                : e.Stroke.GetBounds().Width);

        // The raw path, exactly as captured, so its shape can be inspected directly instead
        // of inferred from counts and bounds.
        //
        // Dropped strokes are logged too. Leaving them out meant a trace could say 47 strokes
        // were discarded and not say WHERE any of them was, so "ink is unreliable at the top of
        // the screen" could not be checked against it at all — the one question the geometry
        // exists to answer.
        if (InkTrace.IsRecording)
        {
            InkTrace.LogGeometry(e.Stroke.StylusPoints.Select(p => p.ToPoint()).ToList(), drop);
        }

        _dropNextStroke = false;
        _strokeDeviceId = null;

        if (drop)
        {
            Strokes.Remove(e.Stroke);
        }
    }

    private void OnStrokesChangedGuard(object? sender, StrokeCollectionChangedEventArgs e)
    {
        // Logged before the early return: this fires for every stroke the canvas builds,
        // whatever our filters later decide, so it separates "the canvas never made a stroke"
        // from "it made one and something removed it".
        if (e.Added.Count > 0)
        {
            InkTrace.Log(InkEvent.StrokeAdded, e.Added.Count, e.Added[0].StylusPoints.Count);
        }

        if (e.Added.Count == 0 || !_dropNextStroke)
        {
            return;
        }

        foreach (Stroke stroke in e.Added)
        {
            if (StrokeIsOffPageJunk(stroke) || _dropNextStroke)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (Strokes.Contains(stroke))
                    {
                        Strokes.Remove(stroke);
                    }
                });
            }
        }
    }

    private static bool StrokeIsOffPageJunk(Stroke stroke)
    {
        var bounds = stroke.GetBounds();
        return bounds.Right < -64 || bounds.Bottom < -64
            || bounds.Left > PageGeometry.WorldWidth + 64
            || bounds.Top > PageGeometry.WorldHeight + 64;
    }
}
