using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace NoteTaker.App.Services;

/// <summary>
/// Summons the Windows touch keyboard and reports how much of a window it covers.
/// </summary>
/// <remarks>
/// Windows raises the touch keyboard by itself when a NATIVE text control takes focus by
/// touch, and it tells a store app how much room it took via <c>InputPane.OccludedRect</c>.
/// This app gets neither: the composer is a custom element inside a WebView2, which the shell
/// does not recognise as a text field, and reaching InputPane from Win32 means COM interop
/// against IInputPaneInterop whose rectangle units are easy to get wrong — and getting them
/// wrong either does nothing or throws the interface off-screen.
///
/// So the keyboard is located as a window instead. Everything here degrades to "no keyboard
/// found", which leaves the layout exactly as it was, and that is the same outcome as not
/// having the feature at all rather than a broken one.
/// </remarks>
public static class TouchKeyboard
{
    /// <summary>Windows 11 hosts the touch keyboard here.</summary>
    private const string ModernClass = "Windows.UI.Core.CoreWindow";

    private const string ModernTitle = "Microsoft Text Input Application";

    /// <summary>Windows 10 and the legacy TabTip surface.</summary>
    private const string LegacyClass = "IPTip_Main_Window";

    /// <summary>Asks the shell to show the touch keyboard. Best effort, never throws.</summary>
    public static void Show()
    {
        var tabTip = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            "microsoft shared", "ink", "TabTip.exe");

        if (!File.Exists(tabTip))
        {
            return;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(tabTip) { UseShellExecute = true },
            };

            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No keyboard is the same outcome as before; never interrupt the page for it.
        }
    }

    /// <summary>
    /// How many DEVICE pixels of <paramref name="windowHandle"/> the keyboard covers along the
    /// bottom edge, or 0 when it is closed, floating clear of the window, or not found.
    /// </summary>
    public static double OccludedHeight(IntPtr windowHandle)
    {
        var keyboard = FindKeyboardWindow();
        if (keyboard == IntPtr.Zero || !IsWindowVisible(keyboard))
        {
            return 0;
        }

        if (!GetWindowRect(windowHandle, out var window) || !GetWindowRect(keyboard, out var pane))
        {
            return 0;
        }

        // Only the part actually overlapping this window's bottom edge counts. A floating or
        // undocked keyboard sitting elsewhere on the desktop hides nothing and must not shift
        // the layout — the student moved it there precisely to see past it.
        var overlap = window.Bottom - Math.Max(pane.Top, window.Top);
        if (pane.Top >= window.Bottom || pane.Bottom <= window.Top || overlap <= 0)
        {
            return 0;
        }

        // Horizontal check too: a narrow keyboard parked to one side leaves the composer
        // visible where it is.
        if (pane.Right <= window.Left || pane.Left >= window.Right)
        {
            return 0;
        }

        return overlap;
    }

    private static IntPtr FindKeyboardWindow()
    {
        var modern = FindWindow(ModernClass, ModernTitle);
        return modern != IntPtr.Zero ? modern : FindWindow(LegacyClass, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
}
