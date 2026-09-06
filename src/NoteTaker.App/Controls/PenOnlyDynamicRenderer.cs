using System.Windows.Input.StylusPlugIns;
using NoteTaker.App.Services;

namespace NoteTaker.App.Controls;

/// <summary>
/// Wet-ink renderer that ignores fingers. Not calling base for touch is what actually
/// stops the RealTimeStylus path from drawing (Preview* Handled alone cannot).
/// </summary>
internal sealed class PenOnlyDynamicRenderer(RejectTouchPlugIn rejectTouch) : DynamicRenderer
{
    /// <summary>
    /// Skipped for the whole contact, consistently across down/move/up. Calling base for some
    /// of a contact and not the rest leaves this renderer's own state machine half-open.
    /// </summary>
    private bool Skip(int stylusDeviceId) =>
        StylusDeviceClassifier.IsTouchId(stylusDeviceId) || rejectTouch.IsLassoContact;

    protected override void OnStylusDown(RawStylusInput rawStylusInput)
    {
        var id = rawStylusInput.StylusDeviceId;
        // Device 0 is the mouse digitizer. On Surface, finger is often routed here while a
        // touch contact is still down — never start wet ink for that path.
        var mouseWhileTouch = id == 0 && StylusDeviceClassifier.HasActiveTouch;

        if (Skip(id) || mouseWhileTouch)
        {
            return;
        }

        base.OnStylusDown(rawStylusInput);
    }

    protected override void OnStylusMove(RawStylusInput rawStylusInput)
    {
        if (Skip(rawStylusInput.StylusDeviceId))
        {
            // Wet ink stopping mid-stroke is what the student sees as the stroke "cutting
            // out", separately from whether the stroke is later kept or dropped.
            InkTrace.Log(InkEvent.WetInkSkipped, rawStylusInput.StylusDeviceId);
            return;
        }

        base.OnStylusMove(rawStylusInput);
    }

    protected override void OnStylusUp(RawStylusInput rawStylusInput)
    {
        if (Skip(rawStylusInput.StylusDeviceId))
        {
            return;
        }

        base.OnStylusUp(rawStylusInput);
    }
}
