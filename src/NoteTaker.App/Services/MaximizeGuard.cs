using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NoteTaker.App.Services;

/// <summary>
/// Keeps a <see cref="System.Windows.Shell.WindowChrome"/> window aligned to its monitor when
/// maximized.
///
/// Without this, Windows sizes a maximized custom-chrome window to the monitor rectangle
/// *inflated by the resize border* (our <c>ResizeBorderThickness</c>), so the window overhangs
/// every screen edge: the right side of the header is pushed off-screen and the left edge no
/// longer sits flush, letting whatever is behind show through. Answering WM_GETMINMAXINFO with
/// the monitor's work area pins maximize to exactly the visible area instead.
///
/// Work area rather than the full monitor rect, so the taskbar is never covered. All values
/// here are physical pixels — both <c>GetMonitorInfo</c> and <c>MINMAXINFO</c> use device
/// pixels, so this needs no DPI conversion and stays correct on per-monitor-DPI setups.
///
/// Also puts the window back when something other than the user un-maximizes it, which is
/// what the on-screen keyboard does — see <c>Reassert</c>.
/// </summary>
public static class MaximizeGuard
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmSize = 0x0005;
    private const int WmActivate = 0x0006;
    private const int WmSysCommand = 0x0112;
    private const int MonitorDefaultToNearest = 0x00000002;

    /// <summary>User asked to un-maximize, through the caption button or the system menu.</summary>
    private const int ScRestore = 0xF120;

    private const int ScMaximize = 0xF030;

    /// <summary>
    /// How short a maximized window has to be before it counts as squashed rather than just
    /// overhanging its work area by the chrome's resize border.
    /// </summary>
    private const int ShortWindowSlack = 48;

    /// <summary>Whether the window is meant to be maximized, as distinct from whether it is.</summary>
    private sealed class Intent
    {
        public bool WantsMaximized;

        /// <summary>Set while the state round-trip runs, so its own WM_SIZEs do not re-enter.</summary>
        public bool Reasserting;
    }

    private static readonly ConditionalWeakTable<Window, Intent> Intents = new();

    private static Intent IntentFor(Window window) => Intents.GetOrCreateValue(window);

    /// <summary>
    /// Maximizes or restores, recording that the user meant it.
    /// </summary>
    /// <remarks>
    /// Callers must use this rather than assigning <see cref="Window.WindowState"/>, otherwise
    /// a deliberate restore is indistinguishable from the on-screen keyboard un-maximizing us
    /// and gets immediately undone.
    /// </remarks>
    public static void ToggleMaximize(Window window)
    {
        var maximized = window.WindowState == WindowState.Maximized;
        IntentFor(window).WantsMaximized = !maximized;
        window.WindowState = maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>Hooks the window, whether or not its native handle exists yet.</summary>
    public static void Attach(Window window)
    {
        // Maximizing by any route — caption button, Win+Up, snap — counts as wanting it.
        // Restoring does NOT clear the intent here; only an explicit user restore does, which
        // is exactly the distinction that makes this work.
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Maximized)
            {
                IntentFor(window).WantsMaximized = true;
            }
        };

        // A window that opens maximized — restored from settings, set in XAML — may have had
        // its state assigned before it existed, which does not necessarily reach StateChanged.
        // Without this, such a window would have no recorded intent and the keyboard could
        // un-maximize it for good.
        window.Loaded += (_, _) =>
        {
            if (window.WindowState == WindowState.Maximized)
            {
                IntentFor(window).WantsMaximized = true;
            }
        };

        HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            Hook(window, hwnd, msg, wParam, lParam, ref handled);

        if (PresentationSource.FromVisual(window) is HwndSource existing)
        {
            existing.AddHook(hook);
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource created)
            {
                created.AddHook(hook);
            }
        };
    }

    private static IntPtr Hook(
        Window window,
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            if (ClampToWorkArea(hwnd, lParam))
            {
                handled = true;
            }

            return IntPtr.Zero;
        }

        // A user-driven restore, through the caption button or the system menu. That is the
        // one case where being un-maximized is intended, so stop wanting to be maximized.
        if (msg == WmSysCommand)
        {
            var command = wParam.ToInt32() & 0xFFF0;
            if (command == ScRestore)
            {
                IntentFor(window).WantsMaximized = false;
            }
            else if (command == ScMaximize)
            {
                IntentFor(window).WantsMaximized = true;
            }

            // Logged for every SC_*, not just the two handled above: when the window drops out
            // of maximized and stays there, the question is whether some other component asked
            // for it, and only the full picture answers that.
            InkTrace.Log(InkEvent.WindowCommand, command, IntentFor(window).WantsMaximized ? 1 : 0);
            return IntPtr.Zero;
        }

        // Put the window back when something else un-maximized it.
        //
        // The on-screen keyboard does exactly that. A trace of it: state went 2 -> 0 and the
        // height 1824 -> 1108, while the monitor work area never moved off 1824 — so this is
        // not a work-area change and there is nothing to re-fill, the window has genuinely
        // been restored out from under us. Closing the keyboard sends no message at all, which
        // is why the window just stays small; WM_ACTIVATE on regaining focus is the first
        // moment we hear anything.
        if (msg is WmSize or WmActivate)
        {
            Log(window, hwnd);
            window.Dispatcher.BeginInvoke(new Action(() => Reassert(window, hwnd)));
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Puts the window back when something other than the user shrinks it.
    /// </summary>
    /// <remarks>
    /// The on-screen keyboard does this two different ways, and both were seen in one trace:
    /// it either drops the window out of maximized (state 2 -> 0), or leaves it maximized and
    /// simply resizes it short — "state=2 height=1108 work=1824". The second is why the first
    /// attempt at this looked like it did nothing: it only ever handled the state.
    ///
    /// Never fixed with SetWindowPos. Resizing a maximized window with an explicit rectangle
    /// CLEARS the maximized style, which is how an earlier version of this un-maximized the
    /// window a few hundred milliseconds after launch. A state round-trip is the only thing
    /// that re-runs WM_GETMINMAXINFO and so re-derives the size the clamp above wants.
    /// </remarks>
    private static void Reassert(Window window, IntPtr hwnd)
    {
        ApplyChromeInset(window, hwnd);

        var intent = IntentFor(window);
        if (!intent.WantsMaximized || intent.Reasserting)
        {
            return;
        }

        // Minimized is a legitimate resting state — restoring from the taskbar brings the
        // maximized state back by itself.
        if (window.WindowState == WindowState.Normal)
        {
            window.WindowState = WindowState.Maximized;
            return;
        }

        if (window.WindowState != WindowState.Maximized
            || !TryWorkAreaHeight(hwnd, out var workHeight)
            || !GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        // A healthy maximized window is a little TALLER than the work area — the custom chrome
        // overhangs by its resize border, which is why the comparison is one-sided and has
        // room in it. Only a window shorter than the screen is one the keyboard has squashed.
        if (rect.Bottom - rect.Top >= workHeight - ShortWindowSlack)
        {
            return;
        }

        intent.Reasserting = true;
        try
        {
            window.WindowState = WindowState.Normal;
            window.WindowState = WindowState.Maximized;
        }
        finally
        {
            intent.Reasserting = false;
        }
    }

    private static bool TryWorkAreaHeight(IntPtr hwnd, out int height)
    {
        height = 0;
        if (!TryWorkArea(hwnd, out var work))
        {
            return false;
        }

        height = work.Bottom - work.Top;
        return height > 0;
    }

    private static bool TryWorkArea(IntPtr hwnd, out NativeRect work)
    {
        work = default;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        work = info.WorkArea;
        return true;
    }

    /// <summary>
    /// Pulls the content in by however far a maximized window overhangs the visible screen.
    /// </summary>
    /// <remarks>
    /// A WindowChrome window maximizes to a rectangle bigger than the work area by its resize
    /// border — measured here at 1850 against a work area of 1824. The extra sits off-screen,
    /// most damagingly under the taskbar, where it swallowed the bottom of the status bar.
    ///
    /// Fixed by insetting the content rather than by shrinking the window: the overhang is how
    /// Windows expects a maximized window to sit, and forcing the frame smaller is what
    /// un-maximized it in an earlier attempt. Measured each time instead of hard-coded, since
    /// it varies with DPI and with which monitor the window is on.
    ///
    /// Bottom edge ONLY. The other three overhang off the sides and top of the screen where
    /// nothing is lost, and insetting those put a visible gap around the window.
    /// </remarks>
    private static void ApplyChromeInset(Window window, IntPtr hwnd)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        if (window.WindowState != WindowState.Maximized)
        {
            root.Margin = default;
            return;
        }

        if (!TryWorkArea(hwnd, out var work) || !GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        // Device pixels from GetWindowRect; Margin is in DIPs.
        var toDip = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        var hidden = toDip.Transform(new Point(0, Math.Max(0, rect.Bottom - work.Bottom)));
        var inset = new Thickness(0, 0, 0, hidden.Y);

        if (root.Margin != inset)
        {
            root.Margin = inset;
        }
    }

    /// <summary>
    /// Records what Windows actually did to the window, so the on-screen-keyboard case can be
    /// read off a trace instead of guessed at. Cheap and only fires on resize messages.
    /// </summary>
    private static void Log(Window window, IntPtr hwnd)
    {
        var workHeight = 0;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var probe = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref probe))
            {
                workHeight = probe.WorkArea.Bottom - probe.WorkArea.Top;
            }
        }

        var height = GetWindowRect(hwnd, out var rect) ? rect.Bottom - rect.Top : 0;
        InkTrace.Log(InkEvent.WindowResize, (int)window.WindowState, height, workHeight);
    }

    private static bool ClampToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            // No monitor resolved (window mid-teardown, or a display just went away):
            // leave the default sizing alone rather than writing a bogus rect.
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = info.WorkArea;
        var screen = info.Monitor;

        // MaxPosition is relative to the monitor's own origin, not the virtual desktop —
        // so this stays correct on secondary monitors sitting at negative coordinates.
        mmi.MaxPosition.X = work.Left - screen.Left;
        mmi.MaxPosition.Y = work.Top - screen.Top;
        mmi.MaxSize.X = work.Right - work.Left;
        mmi.MaxSize.Y = work.Bottom - work.Top;

        // Also cap the tracking size, otherwise the chrome can still push past the work
        // area while maximized. Re-evaluated per monitor on every move, so dragging to a
        // larger display re-clamps to that one rather than staying stuck at the old size.
        mmi.MaxTrackSize.X = mmi.MaxSize.X;
        mmi.MaxTrackSize.Y = mmi.MaxSize.Y;

        Marshal.StructureToPtr(mmi, lParam, false);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

}
