using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Input;

namespace NoteTaker.App.Controls;

/// <summary>
/// Remembers which stylus device IDs are fingers. UI-thread gesture code sees
/// <see cref="TabletDeviceType.Touch"/> reliably; the pen-thread plug-in often does not,
/// so we publish IDs from the UI thread for the DynamicRenderer to consult.
/// </summary>
internal static class StylusDeviceClassifier
{
    /// <summary>
    /// Verdict cache. Holds BOTH answers — a pen id is by definition never a touch id, so
    /// caching only positives meant every pen packet missed and paid a full device
    /// enumeration. See <see cref="IsTouchId"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<int, bool> DeviceIsTouch = new();

    /// <summary>
    /// Device 0 is the mouse/promoted-input fallback, which is also where a real pen lands
    /// when WPF's tablet device list comes up empty (common on Surface — see
    /// PointerInputGuard). Caching a touch verdict against it would misclassify the pen for
    /// the rest of the session, so id 0 is never cached and never treated as touch here;
    /// the live <see cref="HasActiveTouch"/> counter is what guards that path instead.
    /// </summary>
    private const int MousePromotedDeviceId = 0;

    private static int _activeTouches;

    public static bool HasActiveTouch => Volatile.Read(ref _activeTouches) > 0;

    /// <summary>Live contact count, for tracing. A stuck value here disables the pen.</summary>
    public static int ActiveTouches => Volatile.Read(ref _activeTouches);

    public static void TouchContactDown() => Interlocked.Increment(ref _activeTouches);

    public static void TouchContactUp()
    {
        if (Volatile.Read(ref _activeTouches) > 0)
        {
            Interlocked.Decrement(ref _activeTouches);
        }
    }

    /// <summary>
    /// Clears any stuck contacts. Windows cancels touch contacts with
    /// WM_POINTERCAPTURECHANGED instead of a normal up — notably when the pen comes into
    /// range and palm rejection kicks in — so without this the counter can only ever climb,
    /// and a permanently non-zero count makes the mouse-promoted path reject every pen
    /// stroke for the rest of the session.
    /// </summary>
    public static void ResetTouchContacts() => Interlocked.Exchange(ref _activeTouches, 0);

    /// <summary>
    /// Ids proven to belong to a real pen, by asking a device we actually held about its own
    /// tablet. These override any touch verdict for the same number — see
    /// <see cref="RememberDevice"/> for why that collision happens at all.
    /// </summary>
    private static readonly ConcurrentDictionary<int, byte> KnownPenIds = new();

    /// <summary>
    /// Records whichever verdict this device proves about itself.
    /// </summary>
    /// <remarks>
    /// A <see cref="StylusDevice"/>'s Id is only unique WITHIN its own tablet, never across
    /// tablets: a pen digitizer numbers its cursors from 1, and so does the touch digitizer.
    /// So resting a palm routinely produces a contact whose id is numerically the SAME as the
    /// pen's. Recording that against a bare-int key used to mark the pen's own id as "touch"
    /// — permanently, for the rest of the session — and from then on every pen move was
    /// swallowed by the touch guards mid-stroke. Writing a "3" with your hand down produced
    /// its first arc and then nothing: a reversed C.
    ///
    /// A pen verdict therefore wins and is never overwritten. Palm rejection does not depend
    /// on this map anyway — every UI-thread guard asks the device about itself first
    /// (<c>TabletDevice.Type</c>), which cannot collide; this map only exists as a fallback
    /// for the real-time thread, which sees nothing but a bare int.
    /// </remarks>
    public static void RememberDevice(StylusDevice? device)
    {
        if (device is null || device.Id == MousePromotedDeviceId)
        {
            return;
        }

        try
        {
            switch (device.TabletDevice?.Type)
            {
                case TabletDeviceType.Stylus:
                    KnownPenIds[device.Id] = 0;
                    DeviceIsTouch[device.Id] = false;
                    break;

                case TabletDeviceType.Touch when !KnownPenIds.ContainsKey(device.Id):
                    DeviceIsTouch[device.Id] = true;
                    break;
            }
        }
        catch (InvalidOperationException)
        {
            // Device tore down mid-gesture.
        }
    }

    /// <summary>
    /// Whether WPF has ever handed us a real pen device, i.e. the stylus stack is alive.
    /// </summary>
    /// <remarks>
    /// The signal that the mouse-path palm heuristic must stand down: once strokes carry a real
    /// device id, a palm is caught properly by its id and the heuristic can only misfire.
    /// </remarks>
    public static bool StylusStackIsLive => !KnownPenIds.IsEmpty;

    public static bool IsTouchId(int stylusDeviceId)
    {
        if (stylusDeviceId == MousePromotedDeviceId)
        {
            return false;
        }

        // A device we have actually inspected and found to be a pen. Checked before the
        // cache and before any enumeration, because the enumeration below matches on the
        // bare id across ALL touch tablets — and ids collide between tablets (see
        // RememberDevice). Without this, one palm contact sharing the pen's number makes
        // every guard in the app treat the pen as a finger.
        if (KnownPenIds.ContainsKey(stylusDeviceId))
        {
            return false;
        }

        // Both answers are cached. This is called from RejectTouchPlugIn and
        // PenOnlyDynamicRenderer, i.e. from inside WPF's real-time stylus plug-in chain, for
        // every raw packet — at pen report rates that is hundreds of calls a second on a
        // thread with a hard latency budget. Enumerating Tablet.TabletDevices there is doubly
        // wrong: it is O(tablets x styluses), and those statics are Dispatcher-affine, so off
        // the UI thread they throw (caught below) rather than answering. Overrunning the
        // budget makes WPF coalesce or drop packets, which is exactly how a short fast stroke
        // loses most of its samples.
        if (DeviceIsTouch.TryGetValue(stylusDeviceId, out var known))
        {
            return known;
        }

        // Cache miss, and we are off the UI thread — i.e. inside WPF's real-time stylus
        // plug-in chain (RejectTouchPlugIn / PenOnlyDynamicRenderer call this for EVERY raw
        // packet). Tablet.TabletDevices is Dispatcher-affine, so enumerating it here does not
        // merely fail: it THROWS, and the catch below then answers from cache and stores
        // nothing — so the next packet misses and throws again, forever. A throw/catch costs
        // orders of magnitude more than the whole rest of this method, and it was being paid
        // hundreds of times a second on a thread with a hard latency budget. Overrun that
        // budget and WPF coalesces or drops packets, which is exactly how a short fast stroke
        // loses most of its samples. Answer from cache only off-thread; the UI thread
        // populates the cache (see below and RememberDevice) and a pen that is genuinely
        // unknown is correctly treated as "not touch" anyway.
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            return false;
        }

        try
        {
            foreach (TabletDevice tablet in Tablet.TabletDevices)
            {
                if (tablet.Type != TabletDeviceType.Touch)
                {
                    continue;
                }

                foreach (StylusDevice stylus in tablet.StylusDevices)
                {
                    if (stylus.Id == stylusDeviceId)
                    {
                        DeviceIsTouch[stylusDeviceId] = true;
                        return true;
                    }
                }
            }

            var current = Stylus.CurrentStylusDevice;
            if (current is not null
                && current.Id == stylusDeviceId
                && current.TabletDevice?.Type == TabletDeviceType.Touch)
            {
                DeviceIsTouch[stylusDeviceId] = true;
                return true;
            }

            // Only cache "not touch" once the enumeration actually completed and the device
            // list was populated — an empty list means we never had the evidence to decide.
            if (Tablet.TabletDevices.Count > 0)
            {
                DeviceIsTouch[stylusDeviceId] = false;
            }
        }
        catch (InvalidOperationException)
        {
            // Off-thread or torn-down device: answer from cache only, and cache nothing.
            return DeviceIsTouch.TryGetValue(stylusDeviceId, out var cached) && cached;
        }

        return false;
    }

    public static bool IsPenId(int stylusDeviceId)
    {
        if (IsTouchId(stylusDeviceId))
        {
            return false;
        }

        try
        {
            foreach (TabletDevice tablet in Tablet.TabletDevices)
            {
                if (tablet.Type != TabletDeviceType.Stylus)
                {
                    continue;
                }

                foreach (StylusDevice stylus in tablet.StylusDevices)
                {
                    if (stylus.Id == stylusDeviceId)
                    {
                        return true;
                    }
                }
            }

            var current = Stylus.CurrentStylusDevice;
            return current is not null
                && current.Id == stylusDeviceId
                && current.TabletDevice?.Type == TabletDeviceType.Stylus;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
