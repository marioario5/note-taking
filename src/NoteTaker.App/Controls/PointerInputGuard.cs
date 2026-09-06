using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace NoteTaker.App.Controls;

/// <summary>
/// When WPF's tablet device list is empty, pen and finger both arrive as mouse.
/// WM_POINTER still reports PT_TOUCH / PT_PEN / PEN_FLAG_INVERTED correctly — and
/// drives one-finger pan / pinch through <see cref="PageTouchGestures"/>.
/// </summary>
internal sealed class PointerInputGuard
{
    private const int WmPointerUpdate = 0x0245;
    private const int WmPointerDown = 0x0246;
    private const int WmPointerUp = 0x0247;

    /// <summary>
    /// A cancelled contact — Windows sends this INSTEAD of WM_POINTERUP, so a contact that
    /// ends this way never balances its down. Palm rejection fires exactly this when the pen
    /// comes into range, which is the common case here: rest a hand, bring the pen down to
    /// write, and the resting contact is cancelled rather than lifted.
    /// </summary>
    private const int WmPointerCaptureChanged = 0x024B;

    private const uint PtTouch = 2;
    private const uint PtPen = 3;
    private const uint PenFlagInverted = 0x2;
    private const uint PointerFlagInRange = 0x00000002;
    private const uint PointerFlagInContact = 0x00000004;

    private readonly PenInkCanvas _ink;
    private readonly PageTouchGestures _gestures;
    private readonly FrameworkElement _viewport;

    public PointerInputGuard(PenInkCanvas ink, PageTouchGestures gestures, FrameworkElement viewport)
    {
        _ink = ink;
        _gestures = gestures;
        _viewport = viewport;
    }

    public void Attach(Window window)
    {
        // The hook stays installed even under pointer support. Standing it down entirely was
        // tried against the dead-stylus bug and measured: the pen still arrived as mouse and
        // the canvas still faulted, so this is not the culprit and the pen branch is the only
        // thing that can tell a pen from a finger if WPF's stack does go quiet. Only the touch
        // branch stands down (see WpfDeliversTouch), which is a real duplicate-contact fix.
        if (window.IsLoaded)
        {
            Hook(window);
        }
        else
        {
            window.Loaded += (_, _) => Hook(window);
        }
    }

    private void Hook(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (WmPointerDown or WmPointerUpdate or WmPointerUp or WmPointerCaptureChanged))
        {
            return IntPtr.Zero;
        }

        var pointerId = (uint)(wParam.ToInt64() & 0xFFFF);

        if (msg == WmPointerCaptureChanged)
        {
            // The pointer is usually already retired by now, so GetPointerType would fail and
            // we'd never learn it was a touch. Ending the gesture and clearing the contact
            // count unconditionally is the safe direction: over-clearing costs at most one
            // missed palm rejection, while under-clearing leaves the counter stuck above zero
            // permanently, which makes the mouse-promoted path reject EVERY subsequent pen
            // stroke for the rest of the session.
            _gestures.Win32TouchCancel(unchecked((int)pointerId));
            StylusDeviceClassifier.ResetTouchContacts();
            return IntPtr.Zero;
        }

        if (!GetPointerType(pointerId, out var type))
        {
            // GetPointerType routinely fails on the up message because the pointer has
            // already been retired — so a touch-up can be missed here. Clear rather than
            // leak, for the same reason as above.
            if (msg == WmPointerUp)
            {
                StylusDeviceClassifier.ResetTouchContacts();
            }

            return IntPtr.Zero;
        }

        if (type == PtTouch)
        {
            // Only when WPF is NOT delivering touch itself.
            //
            // This class exists for the case where WPF's stylus stack is dead and everything
            // arrives as promoted mouse. With EnablePointerSupport (see the csproj) WPF reads
            // WM_POINTER directly and raises real touch events, so forwarding here as well
            // registered every finger TWICE — under two different ids. One finger then looked
            // like two contacts: image dragging fell through its single-contact guard, and the
            // gesture layer read the pair as a pinch, so trying to move a picture panned the
            // page instead.
            if (!WpfDeliversTouch())
            {
                HandleTouch(pointerId, msg);
            }

            return IntPtr.Zero;
        }

        if (type == PtPen)
        {
            var inverted = false;
            if (GetPointerPenInfo(pointerId, out var penInfo))
            {
                inverted = (penInfo.PenFlags & PenFlagInverted) != 0;
            }

            var info = new PointerInfo();
            if (!GetPointerInfo(pointerId, ref info))
            {
                return IntPtr.Zero;
            }

            var inRange = (info.PointerFlags & PointerFlagInRange) != 0;
            var inContact = (info.PointerFlags & PointerFlagInContact) != 0;
            if (msg == WmPointerUp)
            {
                inContact = false;
            }

            Point? pagePoint = null;
            try
            {
                pagePoint = _ink.PointFromScreen(new Point(info.PixelX, info.PixelY));
            }
            catch (InvalidOperationException)
            {
                // Visual not connected yet.
            }

            _ink.NotifyWin32Pen(inverted, inRange, inContact, pagePoint);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Whether WPF is raising touch events itself, in which case this class must not forward
    /// touch as well or every finger is counted twice.
    /// </summary>
    /// <remarks>
    /// There are two ways WPF can be doing that, and both have to be covered.
    ///
    /// Under pointer support it reads WM_POINTER directly and raises real touch events while
    /// <see cref="Tablet.TabletDevices"/> stays EMPTY — so asking the device list alone
    /// answered "WPF is not handling touch" while WPF was handling touch, and one finger
    /// registered as two contacts: dragging a picture panned the page instead.
    ///
    /// Under the legacy stack the device list is the right question and the switch is off. On
    /// this Surface it enumerates four digitizers, two of them touch, and delivers touch
    /// normally. Keying only on the switch would therefore resume double-counting the moment
    /// pointer support was turned back off.
    ///
    /// Evaluated per message rather than cached: the device list is empty until WPF has
    /// finished enumerating, which is not yet true when this class is attached.
    /// </remarks>
    private static readonly bool WpfOwnsPointerInput =
        AppContext.TryGetSwitch("Switch.System.Windows.Input.Stylus.EnablePointerSupport", out var enabled)
        && enabled;

    private static bool WpfDeliversTouch()
    {
        if (WpfOwnsPointerInput)
        {
            return true;
        }

        try
        {
            return Tablet.TabletDevices.Count > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void HandleTouch(uint pointerId, int msg)
    {
        if (!TryGetViewportPoint(pointerId, out var point))
        {
            if (msg == WmPointerDown)
            {
                StylusDeviceClassifier.TouchContactDown();
            }
            else if (msg == WmPointerUp)
            {
                StylusDeviceClassifier.TouchContactUp();
            }

            return;
        }

        var id = unchecked((int)pointerId);

        if (msg == WmPointerDown)
        {
            StylusDeviceClassifier.TouchContactDown();
            _gestures.Win32TouchDown(id, point);
        }
        else if (msg == WmPointerUpdate)
        {
            _gestures.Win32TouchMove(id, point);
        }
        else if (msg == WmPointerUp)
        {
            _gestures.Win32TouchUp(id, point);
            StylusDeviceClassifier.TouchContactUp();
        }
    }

    private bool TryGetViewportPoint(uint pointerId, out Point pointInViewport)
    {
        pointInViewport = default;
        var info = new PointerInfo();
        if (!GetPointerInfo(pointerId, ref info))
        {
            return false;
        }

        try
        {
            pointInViewport = _viewport.PointFromScreen(new Point(info.PixelX, info.PixelY));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerInfo(uint pointerId, ref PointerInfo pointerInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerPenInfo(uint pointerId, out PointerPenInfo penInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint PointerType;
        public uint PointerId;
        public uint FrameId;
        public uint PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr HwndTarget;
        public int PixelX;
        public int PixelY;
        public int HimetricX;
        public int HimetricY;
        public int PixelRawX;
        public int PixelRawY;
        public int HimetricRawX;
        public int HimetricRawY;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPenInfo
    {
        public PointerInfo PointerInfo;
        public uint PenFlags;
        public uint PenMask;
        public uint Pressure;
        public uint Rotation;
        public int TiltX;
        public int TiltY;
    }
}
