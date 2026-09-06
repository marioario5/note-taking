using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using NoteTaker.App.Services;

namespace NoteTaker.App.Controls;

/// <summary>
/// Second line of defence before stroke collection: collapse finger packets so the
/// ink-collection behavior cannot build a real stroke even if something else misfires.
/// Also latches barrel-button contacts, which draw a lasso instead of ink.
/// </summary>
internal sealed class RejectTouchPlugIn : StylusPlugIn
{
    private bool _lassoContact;

    /// <summary>
    /// Whether the contact currently on the glass began with the barrel button held.
    /// </summary>
    /// <remarks>
    /// Latched here because the plug-in chain sees a contact before the routed StylusDown
    /// does, so the wet-ink renderer can skip it from the very first packet. And it must
    /// survive the button being released mid-gesture: selection stays active until the pen
    /// lifts, so what matters is the button state at touchdown, not right now.
    ///
    /// Note what this deliberately does NOT do: park the lasso's packets. Rewriting them to a
    /// single off-page sample left WPF's ink collection with nothing to build from, and it
    /// threw "StylusPointCollection cannot be empty when attached to a Stroke" out of
    /// InkCollectionBehavior.StylusInputEnd. Suppression belongs in the routed handler, which
    /// marks the preview handled and stops collection before it starts.
    /// </remarks>
    public bool IsLassoContact => Volatile.Read(ref _lassoContact);

    protected override void OnStylusDown(RawStylusInput rawStylusInput)
    {
        base.OnStylusDown(rawStylusInput);

        Volatile.Write(
            ref _lassoContact,
            !StylusDeviceClassifier.IsTouchId(rawStylusInput.StylusDeviceId)
                && BarrelIsDown(rawStylusInput));

        Neutralize(rawStylusInput);
    }

    protected override void OnStylusMove(RawStylusInput rawStylusInput)
    {
        base.OnStylusMove(rawStylusInput);
        Neutralize(rawStylusInput);
    }

    protected override void OnStylusUp(RawStylusInput rawStylusInput)
    {
        base.OnStylusUp(rawStylusInput);
        Neutralize(rawStylusInput);
        Volatile.Write(ref _lassoContact, false);
    }

    /// <summary>
    /// Reads the barrel button off the packet itself. <see cref="StylusDevice.StylusButtons"/>
    /// is not available here — this runs on the real-time thread, where those statics throw.
    /// </summary>
    private static bool BarrelIsDown(RawStylusInput rawStylusInput)
    {
        var points = rawStylusInput.GetStylusPoints();
        if (points.Count == 0
            || !points.Description.HasProperty(StylusPointProperties.BarrelButton))
        {
            return false;
        }

        return points[0].GetPropertyValue(StylusPointProperties.BarrelButton) != 0;
    }

    private static void Neutralize(RawStylusInput rawStylusInput)
    {
        if (!StylusDeviceClassifier.IsTouchId(rawStylusInput.StylusDeviceId))
        {
            return;
        }

        var points = rawStylusInput.GetStylusPoints();
        if (points.Count == 0)
        {
            return;
        }

        // Real-time thread: InkTrace.Log is allocation- and lock-free by design.
        InkTrace.Log(InkEvent.PacketParked, rawStylusInput.StylusDeviceId, points.Count);

        // Empty collections are rejected by SetStylusPoints — park one sample off-page.
        var parked = points[0];
        parked.X = -4000;
        parked.Y = -4000;
        rawStylusInput.SetStylusPoints(new StylusPointCollection(points.Description) { parked });
    }
}
