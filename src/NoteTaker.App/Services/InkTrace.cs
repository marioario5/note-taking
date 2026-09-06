using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Threading;

namespace NoteTaker.App.Services;

internal enum InkEvent : byte
{
    StylusDown,
    StylusMove,
    StylusUp,
    StrokeCollected,
    StrokeDropped,
    PreviewSwallowed,
    PacketParked,
    WetInkSkipped,
    GestureSwallowed,
    TouchDown,
    TouchUp,
    InputStall,
    SlowDispatcherOp,
    MouseDown,
    MouseMove,
    MouseUp,
    TouchContact,
    ImageHit,

    /// <summary>
    /// State of the ink canvas at pen-down: a=EditingMode, b=eraser flags, c=DefaultDrawingAttributes
    /// width. A stroke that is never collected AND never dropped was never built at all, which
    /// only happens when the canvas was not in Ink mode when the tip landed.
    /// </summary>
    CanvasState,

    /// <summary>Raw stroke arrival at the collection layer, before any of our filters judge it.</summary>
    StrokeAdded,

    /// <summary>
    /// A stuck stylus capture was found at pen-down and released: a=the device landing now,
    /// b=the device whose stroke was left unfinished. Each of these is a contact that would
    /// otherwise have inked and vanished.
    /// </summary>
    CaptureRecovered,

    /// <summary>
    /// Ground truth at pen-up, read straight off the canvas: a=Strokes.Count,
    /// b=bitfield(1 Strokes is the instance we subscribed to, 2 IsEnabled, 4 IsHitTestVisible,
    /// 8 IsStylusCaptureWithin), c=StylusPlugIns.Count. If the count climbs while our two
    /// stroke hooks stay silent, the strokes are real and the hooks are detached; if it stays
    /// flat, nothing is being built at all.
    /// </summary>
    CanvasTruth,

    /// <summary>
    /// Window geometry around a resize: a = WindowState (0 Normal, 1 Minimized, 2 Maximized),
    /// b = the window's client height in px, c = the monitor work area height in px. Exists
    /// because the on-screen keyboard shrinks a maximized window and it does not grow back;
    /// these three numbers say whether Windows un-maximized us, whether the work area actually
    /// changed, and whether our re-fill ran.
    /// </summary>
    WindowResize,

    /// <summary>
    /// A WM_SYSCOMMAND reached the window: a = the SC_* code (0xF120 restore, 0xF030 maximize,
    /// 0xF020 minimize), b = whether the window is still meant to be maximized afterwards.
    /// Says whether a drop out of maximized was asked for or done to us.
    /// </summary>
    WindowCommand,

    /// <summary>A packet suppressed because the contact began with the barrel button held.</summary>
    LassoPacket,

    /// <summary>A lasso finished: a=points in the path, b=strokes caught, c=pictures caught.</summary>
    LassoClosed,

    /// <summary>An exception reached a handler. a = its index in the fault list at the end.</summary>
    Fault,

    /// <summary>
    /// A chat turn photographed the page: a=strokes on the page, b=ms spent waiting for the
    /// pen to settle first. Lines the image up against the strokes that existed when it ran.
    /// </summary>
    ChatCapture,

    /// <summary>a/b = requested viewport delta, c = resulting Pan.X. Settles "the page did not follow".</summary>
    PanApplied,
    SaveBegin,
    SaveEnd,
    IndexBegin,
    IndexEnd,
    IndexAbandoned,
    CaptureBegin,
    CaptureEnd,
    HitTestBegin,
    HitTestEnd,
}

/// <summary>
/// Flight recorder for the ink path. Exists because every diagnosis of the dropped-stroke
/// bug so far has been inferred from a description of the symptom, and each round found a
/// real bug without finding the last one. This measures instead.
/// </summary>
/// <remarks>
/// Parts of this are called from WPF's real-time stylus plug-in chain, which has a hard
/// latency budget — instrumentation that allocates, locks, formats a string, or touches a
/// file there would itself cause the stall we are hunting. So the hot path is a
/// preallocated ring of structs and one <see cref="Interlocked.Increment(ref long)"/>:
/// no allocation, no locks, no I/O. Formatting happens only in <see cref="Dump"/>.
/// </remarks>
internal static class InkTrace
{
    private const int Capacity = 1 << 17; // 131,072 entries, ~4 MB. Minutes of writing.

    private struct Entry
    {
        public long Ticks;
        public InkEvent Event;
        public int A;
        public int B;
        public double C;
    }

    private static readonly Entry[] Ring = new Entry[Capacity];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static long _next;
    private static long _startedAt;
    private static volatile bool _enabled;
    private static Thread? _watchdog;
    private static volatile bool _watchdogRunning;
    private static int _probeOutstanding;

    public static bool IsRecording => _enabled;

    // ── Stroke geometry ────────────────────────────────────────────────────────────────
    // Point counts and bounding boxes cannot answer the only question that matters when a
    // glyph renders wrong: was the SHAPE captured? A "3" and an arc can have identical point
    // counts and identical bounds. So keep the actual coordinates of recent strokes and let
    // the path speak for itself — if the captured points already trace an arc the problem is
    // capture, and if they trace a 3 the problem is rendering. Nothing else distinguishes them.

    private const int MaxGeometryStrokes = 400;
    private static readonly List<(double At, double[] Xs, double[] Ys, bool Dropped)> Geometry = [];

    /// <summary>
    /// Writes the exact bytes handed to a vision model next to the trace, so "the model says
    /// it cannot see the page" can be checked instead of argued about. Only while recording.
    /// </summary>
    public static void SaveImage(string tag, byte[]? png)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NoteTaker",
                "trace-images");
            Directory.CreateDirectory(directory);

            var n = Interlocked.Increment(ref _imageSeq);
            if (png is null || png.Length == 0)
            {
                File.WriteAllText(Path.Combine(directory, $"{n:D3}-{tag}-EMPTY.txt"), "no image produced");
                return;
            }

            File.WriteAllBytes(Path.Combine(directory, $"{n:D3}-{tag}-{png.Length}b.png"), png);
        }
        catch (Exception)
        {
            // Diagnostics must never break the feature they are diagnosing.
        }
    }

    private static int _imageSeq;

    /// <param name="dropped">
    /// Whether a filter discarded this stroke. Recorded because where the losses are is the
    /// question — "ink is unreliable at the top of the screen" is answerable from coordinates
    /// and from nothing else.
    /// </param>
    public static void LogGeometry(IReadOnlyList<System.Windows.Point> points, bool dropped = false)
    {
        if (!_enabled || points.Count == 0 || Geometry.Count >= MaxGeometryStrokes)
        {
            return;
        }

        var xs = new double[points.Count];
        var ys = new double[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            xs[i] = points[i].X;
            ys[i] = points[i].Y;
        }

        Geometry.Add(((Clock.ElapsedTicks - _startedAt) * 1000.0 / Stopwatch.Frequency, xs, ys, dropped));
    }

    /// <summary>
    /// Records one event. Safe to call from the real-time stylus thread.
    /// </summary>
    /// <param name="a">Event-specific integer, usually a stylus device id.</param>
    /// <param name="b">Event-specific integer, usually a count.</param>
    /// <param name="c">Event-specific double, usually a duration in ms or a coordinate.</param>
    public static void Log(InkEvent kind, int a = 0, int b = 0, double c = 0)
    {
        if (!_enabled)
        {
            return;
        }

        // Wraps by design: the interesting window is always the last few seconds before the
        // student noticed something went wrong, so the newest entries are the ones to keep.
        var slot = (int)(Interlocked.Increment(ref _next) - 1 & (Capacity - 1));
        Ring[slot].Ticks = Clock.ElapsedTicks;
        Ring[slot].Event = kind;
        Ring[slot].A = a;
        Ring[slot].B = b;
        Ring[slot].C = c;
    }

    /// <summary>Times a block and logs a begin/end pair, so a stall can be attributed.</summary>
    public static Scope Measure(InkEvent begin, InkEvent end, int a = 0)
    {
        Log(begin, a);
        return new Scope(end, a);
    }

    internal readonly struct Scope(InkEvent end, int a) : IDisposable
    {
        private readonly long _startTicks = Clock.ElapsedTicks;

        public void Dispose() =>
            Log(end, a, 0, (Clock.ElapsedTicks - _startTicks) * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>
    /// Turns on event recording. Pure memory: a ring buffer and a stopwatch, nothing that
    /// touches the Dispatcher, so this is safe to call before the window exists.
    /// </summary>
    public static void Enable()
    {
        Geometry.Clear();
        Faults.Clear();
        Interlocked.Exchange(ref _next, 0);
        _startedAt = Clock.ElapsedTicks;
        _enabled = true;
    }

    /// <summary>
    /// Starts the two probes that actively interact with the Dispatcher: the Input-priority
    /// stall watchdog and the <see cref="Dispatcher.Hooks"/> operation profiler.
    /// </summary>
    /// <remarks>
    /// Kept opt-in and separate from <see cref="Enable"/> deliberately. These were made to run
    /// from process start, which put a 15ms Input-priority post loop and a hook on every
    /// dispatcher operation right across the window in which WPF builds its stylus/pointer
    /// stack — and from that build onward, sessions began arriving with the stylus stack dead:
    /// no StylusDown at all, the pen delivered as promoted mouse, no drawing and no panning.
    /// That correlation is not proof, and the probes have earned their keep. But they are
    /// diagnostics, and diagnostics do not get to be a suspect in the bug they are measuring.
    /// </remarks>
    public static void StartProbes(Dispatcher dispatcher)
    {
        StartWatchdog(dispatcher);
        AttachDispatcherProfiler(dispatcher);
    }

    // ── Faults ─────────────────────────────────────────────────────────────────────────
    // A message box saying "Object reference not set to an instance of an object" names the
    // one thing about an exception that cannot be acted on. The stack is the whole diagnosis,
    // and throwing it away meant the next step was always guessing at which handler ran.

    private const int MaxFaults = 40;
    private static readonly List<(double At, string Text)> Faults = [];

    /// <summary>Records an exception, with its stack, against the trace timeline.</summary>
    public static void LogFault(string origin, Exception exception)
    {
        if (!_enabled)
        {
            return;
        }

        lock (Faults)
        {
            if (Faults.Count >= MaxFaults)
            {
                return;
            }

            var at = (Clock.ElapsedTicks - _startedAt) * 1000.0 / Stopwatch.Frequency;
            Faults.Add((at, $"[{origin}] {exception}"));
        }

        Log(InkEvent.Fault, Faults.Count);
    }

    // ── Dispatcher profiler ────────────────────────────────────────────────────────────
    // Hand-placed Measure() calls can only ever attribute stalls to code someone already
    // suspected. A trace showed ~900ms of input starvation with no save, index, capture or
    // hit-test anywhere near it — i.e. the real culprit was something nobody had thought to
    // instrument. This closes that hole generically: it times EVERY dispatcher operation and
    // names any that runs long enough to matter, so an unexplained stall cannot happen twice.

    private static readonly Dictionary<DispatcherOperation, long> InFlight = [];
    private static readonly Dictionary<string, int> NameIds = [];
    private static readonly List<string> Names = [];
    private static FieldInfo? _methodField;
    private static bool _profilerAttached;

    /// <summary>Ops slower than this are worth naming; below it the noise drowns the signal.</summary>
    private const double SlowOperationMs = 25;

    private static void AttachDispatcherProfiler(Dispatcher dispatcher)
    {
        if (_profilerAttached)
        {
            return;
        }

        _profilerAttached = true;
        _methodField = typeof(DispatcherOperation)
            .GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);

        dispatcher.Hooks.OperationStarted += (_, e) => InFlight[e.Operation] = Clock.ElapsedTicks;

        dispatcher.Hooks.OperationCompleted += (_, e) =>
        {
            if (!InFlight.Remove(e.Operation, out var started))
            {
                return;
            }

            var ms = (Clock.ElapsedTicks - started) * 1000.0 / Stopwatch.Frequency;
            if (ms >= SlowOperationMs)
            {
                // Resolve the name only for slow ops — reflection on every operation would
                // itself become the bottleneck at dispatcher rates.
                Log(InkEvent.SlowDispatcherOp, NameId(Describe(e.Operation)), (int)e.Operation.Priority, ms);
            }
        };

        dispatcher.Hooks.OperationAborted += (_, e) => InFlight.Remove(e.Operation);
    }

    /// <summary>Best-effort name of whatever the operation is about to run.</summary>
    private static string Describe(DispatcherOperation operation)
    {
        try
        {
            if (_methodField?.GetValue(operation) is Delegate method)
            {
                var declaring = method.Method.DeclaringType?.Name ?? "?";
                return $"{declaring}.{method.Method.Name}";
            }
        }
        catch (Exception)
        {
            // Reflection into a private field is a diagnostic nicety, never a correctness
            // dependency — an unnamed slow op is still a located slow op.
        }

        return "unknown";
    }

    private static string NameOf(int id) => id >= 0 && id < Names.Count ? Names[id] : $"#{id}";

    private static int NameId(string name)
    {
        if (NameIds.TryGetValue(name, out var id))
        {
            return id;
        }

        id = Names.Count;
        Names.Add(name);
        NameIds[name] = id;
        return id;
    }

    public static void Stop()
    {
        _enabled = false;
        StopProbes();
    }

    /// <summary>Stops the stall watchdog. The profiler hook stays; it is idle once unused.</summary>
    public static void StopProbes()
    {
        _watchdogRunning = false;
        _watchdog = null;
    }

    /// <summary>
    /// The measurement that actually matters. A plain timer cannot see this: it posts a
    /// no-op at <see cref="DispatcherPriority.Input"/> — the exact priority WPF delivers
    /// stylus packets at — and records how late it ran. Latency here IS the time the pen was
    /// starved, whatever caused it, and its timestamp lines up against the stroke events
    /// around it. A quiet trace with fluid strokes and a 900 ms spike next to a broken one
    /// settles the question without anyone having to describe how it felt.
    /// </summary>
    private static void StartWatchdog(Dispatcher dispatcher)
    {
        if (_watchdogRunning)
        {
            return;
        }

        _watchdogRunning = true;
        Volatile.Write(ref _probeOutstanding, 0);
        _watchdog = new Thread(() =>
        {
            while (_watchdogRunning)
            {
                // One probe in flight at a time.
                //
                // This used to post unconditionally every 15ms. While the thread was blocked
                // the probes queued up, and when it freed they all ran together — each
                // reporting its own, decaying lateness. A single 822ms cold start was recorded
                // as 44 separate stalls counting down from 822ms to 32ms, and the footer's
                // stall total was really an entry total: 93 rows for 30 actual episodes. Every
                // "worst stall" figure read off a trace was measuring one block many times.
                if (Interlocked.CompareExchange(ref _probeOutstanding, 1, 0) == 0)
                {
                    var posted = Clock.ElapsedTicks;
                    try
                    {
                        dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                        {
                            Volatile.Write(ref _probeOutstanding, 0);
                            var late = (Clock.ElapsedTicks - posted) * 1000.0 / Stopwatch.Frequency;
                            if (late >= 20)
                            {
                                Log(InkEvent.InputStall, 0, 0, late);
                            }
                        });
                    }
                    catch (TaskCanceledException)
                    {
                        return; // Dispatcher shutting down.
                    }
                }

                Thread.Sleep(15);
            }
        })
        {
            IsBackground = true,
            Name = "InkTrace watchdog",
            Priority = ThreadPriority.AboveNormal,
        };

        _watchdog.Start();
    }

    /// <summary>Writes the ring to a text file and returns its path.</summary>
    public static string Dump()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoteTaker");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"ink-trace-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, Render());
        return path;
    }

    /// <summary>
    /// Formats the buffer as text. Separate from <see cref="Dump"/> so the autosave can write
    /// the same report to its own file without a second copy of the formatting.
    /// </summary>
    private static string Render()
    {
        var total = Interlocked.Read(ref _next);
        var count = (int)Math.Min(total, Capacity);
        var first = (int)Math.Max(0, total - Capacity);

        var builder = new StringBuilder(count * 48);
        builder.AppendLine("ms\tevent\tdevice\tcount\tvalue");

        var stalls = 0;
        var worstStall = 0.0;
        var dropped = 0;
        var blame = new Dictionary<string, (int Count, double Total, double Worst)>();
        var moveGaps = new List<double>();
        var lastMove = -1.0;

        for (var i = 0; i < count; i++)
        {
            ref var entry = ref Ring[(first + i) & (Capacity - 1)];
            var ms = (entry.Ticks - _startedAt) * 1000.0 / Stopwatch.Frequency;

            // Slow dispatcher ops carry a name id rather than a device id; resolve it inline
            // so the log reads as prose instead of needing a lookup table.
            var detail = entry.Event == InkEvent.SlowDispatcherOp
                ? NameOf(entry.A)
                : entry.A.ToString();

            builder.Append(ms.ToString("0.0")).Append('\t')
                .Append(entry.Event).Append('\t')
                .Append(detail).Append('\t')
                .Append(entry.B).Append('\t')
                .Append(entry.C.ToString("0.0"))
                .AppendLine();

            switch (entry.Event)
            {
                case InkEvent.InputStall:
                    stalls++;
                    worstStall = Math.Max(worstStall, entry.C);
                    break;

                case InkEvent.StrokeDropped:
                    dropped++;
                    break;

                case InkEvent.SlowDispatcherOp:
                    var name = NameOf(entry.A);
                    var seen = blame.GetValueOrDefault(name);
                    blame[name] = (seen.Count + 1, seen.Total + entry.C, Math.Max(seen.Worst, entry.C));
                    break;

                case InkEvent.MouseMove:
                    if (lastMove >= 0 && ms - lastMove < 500)
                    {
                        moveGaps.Add(ms - lastMove);
                    }

                    lastMove = ms;
                    break;
            }
        }

        builder.AppendLine();
        builder.AppendLine($"entries: {count}{(total > Capacity ? $" (oldest {total - Capacity} overwritten)" : string.Empty)}");
        builder.AppendLine($"input stalls >=20ms: {stalls}, worst {worstStall:0.0} ms");
        builder.AppendLine($"strokes dropped: {dropped}");

        if (moveGaps.Count > 0)
        {
            moveGaps.Sort();
            builder.AppendLine(
                $"pen sample gap: median {moveGaps[moveGaps.Count / 2]:0.0} ms, " +
                $"p95 {moveGaps[(int)(moveGaps.Count * 0.95)]:0.0} ms, " +
                $"worst {moveGaps[^1]:0.0} ms  ({moveGaps.Count} moves)");
        }

        if (blame.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"what held the UI thread (ops >= {SlowOperationMs:0} ms), worst first:");
            foreach (var (name, stat) in blame.OrderByDescending(kv => kv.Value.Total))
            {
                builder.AppendLine($"  {stat.Total,8:0} ms total  x{stat.Count,-4} worst {stat.Worst,6:0} ms   {name}");
            }
        }

        // Faults first, above the geometry dump: when there is one, it is the only thing
        // anyone opening this file wants to read.
        lock (Faults)
        {
            if (Faults.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"=== exceptions ({Faults.Count}) ===");
                foreach (var (at, text) in Faults)
                {
                    builder.AppendLine($"--- t={at:0.0} ms ---");
                    builder.AppendLine(text);
                }
            }
        }

        if (Geometry.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("=== stroke geometry (page units, one stroke per block) ===");
            foreach (var (at, xs, ys, discarded) in Geometry)
            {
                builder.Append("STROKE t=").Append(at.ToString("0.0"))
                    .Append(" n=").Append(xs.Length)
                    .Append(discarded ? " DROPPED" : string.Empty).AppendLine();
                for (var i = 0; i < xs.Length; i++)
                {
                    builder.Append("  ").Append(xs[i].ToString("0.00"))
                        .Append(' ').Append(ys[i].ToString("0.00")).AppendLine();
                }
            }
        }

        return builder.ToString();
    }

    // ── Autosave ───────────────────────────────────────────────────────────────────────
    // The ring is the only copy of what happened, and until now it reached disk only when
    // someone chose "save the trace" or the process exited cleanly. Neither covers the case
    // the trace exists for: "it was fine, then it randomly started doing this." By the time
    // that is noticed the interesting seconds are minutes back, and at ~1,000 entries a second
    // while erasing, 131,072 entries is a couple of minutes. A crash, or a force-kill during a
    // rebuild, threw the whole buffer away.
    //
    // So a copy goes to disk on its own. It is deliberately NOT a dispatcher timer: the probes
    // that touched the Dispatcher are the prime suspect in a stylus-stack regression (see
    // StartProbes), and a diagnostic must not be able to cause the bug it is watching. A plain
    // background thread that formats and writes, touching no WPF state, cannot.

    private static Thread? _autosave;
    private static volatile bool _autosaveRunning;
    private static long _lastAutosaved = -1;

    /// <summary>How often the buffer is copied to disk.</summary>
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(60);

    /// <summary>The rolling copy. One file, overwritten — not one per interval.</summary>
    public static string AutosavePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteTaker",
        "ink-trace-live.txt");

    /// <summary>Begins copying the buffer to <see cref="AutosavePath"/> in the background.</summary>
    public static void StartAutosave()
    {
        if (_autosave is not null || !_enabled)
        {
            return;
        }

        _autosaveRunning = true;
        _autosave = new Thread(AutosaveLoop)
        {
            IsBackground = true,
            Name = "InkTrace autosave",

            // Below normal: a diagnostic must never compete with the pen for the CPU.
            Priority = ThreadPriority.BelowNormal,
        };
        _autosave.Start();
    }

    private static void AutosaveLoop()
    {
        while (_autosaveRunning)
        {
            // Slept in slices so shutdown does not wait out a whole interval.
            for (var i = 0; i < AutosaveInterval.TotalSeconds && _autosaveRunning; i++)
            {
                Thread.Sleep(1000);
            }

            if (_autosaveRunning)
            {
                WriteAutosave();
            }
        }
    }

    /// <summary>
    /// Writes the buffer to the rolling file, if anything has happened since the last write.
    /// </summary>
    /// <remarks>
    /// Written to a temporary file and moved into place, because the whole point is to survive
    /// an abrupt death — and a process killed halfway through a 4 MB write would otherwise leave
    /// a truncated file where the previous good one was.
    /// </remarks>
    private static void WriteAutosave()
    {
        var total = Interlocked.Read(ref _next);
        if (total == _lastAutosaved)
        {
            return;
        }

        try
        {
            var path = AutosavePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Render());
            File.Move(temporary, path, overwrite: true);
            _lastAutosaved = total;
        }
        catch (IOException)
        {
            // Disk full, file locked, folder gone. Losing a trace copy is not worth taking the
            // app down for; the next interval tries again.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Stops the autosave thread, writing one last copy.</summary>
    public static void StopAutosave()
    {
        _autosaveRunning = false;
        _autosave = null;
        WriteAutosave();
    }
}
