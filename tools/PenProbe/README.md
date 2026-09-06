# PenProbe — is the pen broken, or is NoteTaker broken?

Two throwaway ink surfaces with **none of NoteTaker's code in them**. They exist to answer one
question in about fifteen seconds, before anyone edits the app:

> Does a bare WPF `InkCanvas` on this machine receive stylus events right now?

If the answer is no, nothing you change in NoteTaker can help, and you should stop.

## Run them

```bash
dotnet run --project tools/PenProbe/PenProbe.csproj
```

Draw in the window, then close it. Results: `%LocalAppData%\NoteTaker\pen-probe8.txt`.

This is **.NET 8 WPF with `EnablePointerSupport` on** — the same runtime and same switch the
app uses. It is the one that matters.

```bash
powershell -STA -ExecutionPolicy Bypass -File tools/PenProbe/pen-probe-netfx.ps1
```

Same idea on **.NET Framework WPF (legacy WISP stack)**. Results:
`%LocalAppData%\NoteTaker\pen-probe.txt`. Useful as a control: it exercises a completely
different stylus implementation against the same hardware.

## Reading the result

| `StylusDown` / `StylusMove` | `MouseDown` / `MouseMove` | Meaning |
|---|---|---|
| non-zero | zero | Pen stack healthy. A NoteTaker fault is a NoteTaker bug — go debug it. |
| **zero** | non-zero | **Pen stack is down.** Windows is delivering the pen as promoted mouse. Not an app bug. |

`tablet devices: 0` is **normal** under `EnablePointerSupport` — WPF reads WM_POINTER directly
and leaves that legacy collection empty. Do not read it as a fault on its own.

## When the .NET 8 probe shows zero stylus events

In order:

1. **Reboot.** This has already happened once (2026-08-10) and a reboot fixed it. The pen stack
   degraded mid-day, survived every app restart, and could not be reached from application
   code.
2. Re-run the .NET Framework probe. If *it* still works while .NET 8 does not, the digitizer is
   fine and the fault is in .NET 8's pen stack specifically — repair the .NET Desktop Runtime.
3. Only then look at NoteTaker.

## Why this exists

On 2026-08-10 the pen stopped working and roughly six speculative fixes went into the app
before anyone checked whether the app was at fault. It was not. A 70-line `InkCanvas` with no
NoteTaker code reproduced the crash exactly:

```
System.ArgumentException: StylusPointCollection cannot be empty when attached to a Stroke.
   at MS.Internal.Ink.InkCollectionBehavior.StylusInputEnd(Boolean commit)
   at MS.Internal.Ink.EditingCoordinator.OnInkCanvasDeviceUp(...)
   at System.Windows.Interop.HwndMouseInputProvider.ReportInput(...)
```

That is a WPF defect, reachable whenever Windows hands WPF a pen as promoted mouse. There is no
app-side cause and no app-side place to catch it — it is raised from a class handler on the
event route. `App.OnDispatcherUnhandledException` recognises it and suppresses the dialog.

Run the probe first. It is cheaper than a day.
