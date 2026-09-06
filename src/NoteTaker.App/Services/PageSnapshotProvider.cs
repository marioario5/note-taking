using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.App.Services;

/// <summary>Exposes whichever page is currently open to services that need to rasterize it.</summary>
public interface IActivePageSource
{
    long ActivePageId { get; }
    StrokeCollection ActiveStrokes { get; }

    /// <summary>Background for the mistake-flagging scan: the worksheet only.</summary>
    ImageSource? ActiveBackground { get; }

    /// <summary>Placed pictures, so a capture can include the ones in frame.</summary>
    IReadOnlyList<PageImage> ActiveImages { get; }

    /// <summary>The world rectangle currently on screen — what a chat capture should frame.</summary>
    Rect ActiveViewBounds { get; }

    /// <summary>
    /// Background for a chat turn: the worksheet PLUS anything the student stamped on the
    /// page. A pasted picture is usually the question they are asking about, so the chat
    /// tutor has to see it — while the flagging scan must not, or it would start marking up
    /// a textbook screenshot as if the student had written it.
    /// </summary>
    ImageSource? ActiveChatBackground { get; }

    int ActiveStrokeRevision { get; }

    /// <summary>
    /// <see cref="Environment.TickCount64"/> when ink last changed, so a capture can wait for
    /// the hand to stop before photographing the page.
    /// </summary>
    /// <remarks>
    /// Deliberately not the stroke revision: that only advances on save, so it lags the pen by
    /// seconds and cannot answer "is the student still writing right now".
    /// </remarks>
    long ActiveLastInkAt { get; }
}

public sealed class PageSnapshotProvider(Dispatcher dispatcher, IActivePageSource source) : IPageSnapshotProvider
{
    /// <summary>
    /// Every capture here runs on the UI thread, and WPF marshals raw stylus packets to that
    /// same thread at <see cref="DispatcherPriority.Input"/> (5). The default priority for
    /// <c>InvokeAsync(Action)</c> is Normal (9), which outranks Input — and a dispatcher
    /// operation is not preemptible once it starts. So a page rasterization or an ink
    /// hit-test posted at the default priority jumps ahead of queued pen input and blocks it
    /// for its whole duration; the packets that pile up behind it get coalesced, and a short
    /// fast stroke can arrive with a couple of points instead of a dozen (or not at all).
    /// Background (4) sits just below Input, so none of this work can ever cut in front of
    /// the pen. Nothing here is interactive, so waiting for input to settle costs nothing.
    /// </summary>
    private const DispatcherPriority CapturePriority = DispatcherPriority.Background;

    // Practice generation has run at 900px in production without legibility issues, so
    // that's the proven floor. These three widths sit at or just above it — sized by how
    // detail-sensitive each path is, not by habit. Tune here, not at the call sites.
    private const int ScanWidth = 1000;

    // Same content class as ScanWidth (a full page, nothing zoomed in) — no reason this
    // fallback needed 400px more than a scan does.
    private const int ChatPageWidth = 1000;

    // Kept meaningfully above the other two on purpose: this is the fine-detail,
    // digit/sign-sensitive crop chat reasons about turn by turn, and this app has a
    // documented history of hallucinating misread digits/signs from exactly this path.
    // Cut conservatively from 1600, not down to the 1000 floor.
    private const int ChatCropWidth = 1300;

    /// <summary>Floor on how small chat may render the ink relative to how it was written.</summary>
    /// <remarks>
    /// A width alone cannot protect legibility, because it is divided by however wide the
    /// content turns out to be — and the content got wider once pasted figures joined the
    /// crop. This is the floor that makes <see cref="ChatPageWidth"/> mean "aim for this"
    /// rather than "shrink whatever it takes to fit".
    ///
    /// Kept at 1.0 after measuring that lowering it saves nothing. Gemini bills an image by
    /// 768x768 tiles, not by pixel: the same capture at 1.00 and at 0.75 both came back at
    /// exactly 2,852 prompt tokens, because 1345x1084 and 1009x813 are both four tiles. Scale
    /// only pays when it drops a tile — for this page that needs about 0.57 (width under 768),
    /// which is close enough to the 0.44 that produced a misread to not be worth the margin
    /// for roughly 550 tokens. So the legibility floor stays where it protects the ink, and
    /// image cost is reduced by cropping tighter rather than by rendering smaller.
    /// </remarks>
    private const double ChatMinInkScale = 1.0;

    public async Task<PageSnapshotCapture?> CaptureAsync(
        long pageId,
        NormalizedRegion? focus = null,
        CancellationToken ct = default)
    {
        return await dispatcher.InvokeAsync(() =>
        {
            if (source.ActivePageId != pageId)
            {
                // The user moved on; a snapshot of a different page would be misleading.
                InkTrace.SaveImage($"scan-wrongpage-{pageId}-active{source.ActivePageId}", null);
                return null;
            }

            using var scope = InkTrace.Measure(InkEvent.CaptureBegin, InkEvent.CaptureEnd);

            // Framed like chat: the work the student is looking at, wherever on the canvas
            // that is. Scanning was still pinned to the A4 sheet, so working below it got
            // marked up as if nothing had been written. The area travels back with the image
            // because the model answers in coordinates relative to what it was shown.
            var window = focus is { IsEmpty: false } region
                ? FocusWindow(region)
                : WorkingWindow(source);

            var area = PageRenderer.ContentBounds(
                source.ActiveStrokes,
                source.ActiveImages,
                window);

            // Live/review scans also need readable ink (cream-on-white was nearly invisible).
            var png = PageRenderer.RenderAreaPng(
                source.ActiveStrokes,
                source.ActiveBackground,
                area,
                targetWidth: ScanWidth,
                ocrContrast: true,
                drawBackground: false);

            InkTrace.SaveImage($"scan-n{source.ActiveStrokes.Count}", png);
            return new PageSnapshotCapture(
                png,
                source.ActiveStrokeRevision,
                PageGeometry.ToNormalizedUnclamped(area));
        }, CapturePriority);
    }

    public async Task<byte[]?> CaptureRegionAsync(
        long pageId,
        NormalizedRegion region,
        CancellationToken ct = default)
    {
        return await dispatcher.InvokeAsync(() =>
        {
            if (source.ActivePageId != pageId)
            {
                return null;
            }

            return PageRenderer.RenderRegionPng(
                source.ActiveStrokes,
                source.ActiveBackground,
                region,
                ocrContrast: true);
        }, CapturePriority);
    }

    /// <summary>Ink must have been still this long before the page is worth photographing.</summary>
    /// <remarks>
    /// Sized from a real trace of someone writing a line of set-builder notation: consecutive
    /// strokes landed 195–315 ms apart, so a shorter window would fire between two strokes of
    /// the same expression. Comfortably above that, and still under the gap that follows
    /// finishing a thought.
    /// </remarks>
    private const int InkQuietMs = 400;

    /// <summary>Ceiling on the wait, so continuous scribbling cannot stall a question forever.</summary>
    private const int InkSettleTimeoutMs = 2500;

    /// <summary>
    /// Waits for the pen to stop before capturing.
    /// </summary>
    /// <remarks>
    /// Without this the snapshot was taken the instant Send was pressed, which is routinely
    /// mid-sentence — a saved capture showed a line ending on a trailing comma, and the trace
    /// showed four more strokes landing in the 1.3 s after the image was built. The picture
    /// was honest and the answer was still about half a question. Costs nothing when the
    /// student has already stopped: the check is against when ink last changed, not a
    /// blanket delay.
    /// </remarks>
    private async Task WaitForInkToSettleAsync(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + InkSettleTimeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var quietFor = Environment.TickCount64 - source.ActiveLastInkAt;
            if (quietFor >= InkQuietMs)
            {
                return;
            }

            try
            {
                await Task.Delay((int)Math.Max(1, InkQuietMs - quietFor), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    public async Task<byte[]?> CaptureChatContextAsync(
        long pageId,
        NormalizedRegion? focus = null,
        CancellationToken ct = default)
    {
        var settleFrom = Environment.TickCount64;
        await WaitForInkToSettleAsync(ct).ConfigureAwait(false);
        var settled = (int)(Environment.TickCount64 - settleFrom);

        return await dispatcher.InvokeAsync(() =>
        {
            if (source.ActivePageId != pageId)
            {
                // The chat is asking about a page that is no longer open, so there is nothing
                // truthful to send. Worth recording: from the model's side this is
                // indistinguishable from a blank page, and it will say it cannot see anything.
                InkTrace.SaveImage($"chat-wrongpage-{pageId}-active{source.ActivePageId}", null);
                return null;
            }

            var strokes = source.ActiveStrokes;

            // Chat sees the stamp layer; the flagging scan above does not.
            var background = source.ActiveChatBackground;

            byte[] png;
            string tag;

            // Only take the normalized-crop path when there is a finding to crop TO.
            //
            // Without a focus this used to ask InkContentRegion for the whole content, which
            // answers in coordinates normalized against the A4 sheet — and NormalizedRegion
            // clamps to 0..1. Work below the sheet therefore came back clamped to the sheet's
            // edge: a non-empty but meaningless rectangle, which the crop path then rendered.
            // A saved capture showed the result — a mostly blank page with the student's
            // writing squeezed off the bottom-right corner. The content path below stays in
            // world coordinates and has no such ceiling.
            var content = focus is { IsEmpty: false }
                ? PageRenderer.InkContentRegion(strokes, focus, source.ActiveImages)
                : null;

            if (content is { IsEmpty: false } region)
            {
                png = PageRenderer.RenderRegionPng(
                    strokes,
                    background,
                    region,
                    targetWidth: ChatCropWidth,
                    ocrContrast: true,
                    drawBackground: true);
                tag = $"chat-crop-n{strokes.Count}";
            }
            else
            {
                // Follow the work, wherever on the canvas it is. Clipping to the A4 sheet
                // meant that panning somewhere roomy and writing there produced a picture of
                // the empty sheet, and the tutor would answer about whatever was at the
                // origin instead of what the student had just done.
                png = PageRenderer.RenderAreaPng(
                    strokes,
                    background,
                    PageRenderer.ContentBounds(strokes, source.ActiveImages, WorkingWindow(source)),
                    targetWidth: ChatPageWidth,
                    ocrContrast: true,
                    drawBackground: true,
                    minScale: ChatMinInkScale);
                tag = $"chat-view-n{strokes.Count}";
            }

            InkTrace.Log(InkEvent.ChatCapture, strokes.Count, settled);
            InkTrace.SaveImage(tag, png);
            return png;
        }, CapturePriority);
    }

    /// <summary>
    /// The screen, generously padded: which marks count as "the work being asked about".
    /// </summary>
    /// <remarks>
    /// Framing exactly on the viewport was too strict — a page of working that ran a little
    /// past the bottom edge had its last lines cut off, and those were the answer. Including
    /// everything on the page was too loose — one stray mark far away stretched the frame
    /// until the real work rendered as hairlines. A window around what the student can see
    /// picks up the rest of what they are working on without reaching the other side of a
    /// 200,000-unit canvas.
    /// </remarks>
    private static Rect WorkingWindow(IActivePageSource source)
    {
        var window = source.ActiveViewBounds;
        window.Inflate(window.Width, window.Height);
        return window;
    }

    /// <summary>Sheet-relative fraction the focus window is padded by, each side.</summary>
    /// <remarks>
    /// Deliberately smaller than <see cref="WorkingWindow"/>'s screen-sized padding: the
    /// caller already knows roughly where the new writing is (a live check's focus, widened
    /// to include neighbouring marks), so this only needs enough slack to keep the whole
    /// expression in frame, not enough to cover a screen's worth of uncertainty.
    /// </remarks>
    private const double FocusPadding = 0.08;

    /// <summary>A tight window around known recent writing, in world coordinates.</summary>
    private static Rect FocusWindow(NormalizedRegion region) =>
        PageGeometry.ToPageRect(region.Inflate(FocusPadding));

    public async Task<bool> RegionContainsInkAsync(
        long pageId,
        NormalizedRegion region,
        InkHitTolerance tolerance = InkHitTolerance.Loose,
        CancellationToken ct = default)
    {
        return await dispatcher.InvokeAsync(() =>
        {
            if (source.ActivePageId != pageId || region.IsEmpty)
            {
                return false;
            }

            using var scope = InkTrace.Measure(InkEvent.HitTestBegin, InkEvent.HitTestEnd);

            // Strict: prune blank/stale red marks. Loose: accept slightly-off vision boxes
            // (logs showed misses at ~39px with a 40px pad — use a clearer margin).
            return tolerance == InkHitTolerance.Strict
                ? HitTestInk(region, padX: 10, padY: 10)
                : HitTestInk(region, padX: 28, padY: 24) || HitTestInk(region, padX: 56, padY: 48);
        }, CapturePriority);
    }

    public async Task<NormalizedRegion?> SnapToInkAsync(
        long pageId,
        NormalizedRegion modelBox,
        string? whereHint,
        CancellationToken ct = default)
    {
        return await dispatcher.InvokeAsync(
            () =>
            {
                if (source.ActivePageId != pageId)
                {
                    return null;
                }

                // Sheet-normalized to match the model's box, which PersistScanAsync has
                // already mapped out of image space by the time this runs. Unclamped, because
                // work below the sheet is genuinely at y > 1 and clamping would pile every
                // off-sheet line onto the bottom edge as one.
                var bounds = new List<NormalizedRegion>();
                foreach (Stroke stroke in source.ActiveStrokes)
                {
                    bounds.Add(PageGeometry.ToNormalizedUnclamped(stroke.GetBounds()));
                }

                return InkLineSnapper.Snap(bounds, modelBox, whereHint);
            },
            CapturePriority);
    }

    private bool HitTestInk(NormalizedRegion region, double padX, double padY)
    {
        var bounds = PageGeometry.ToPageRect(region);
        bounds.Inflate(padX, padY);

        foreach (Stroke stroke in source.ActiveStrokes)
        {
            if (!stroke.GetBounds().IntersectsWith(bounds))
            {
                continue;
            }

            foreach (StylusPoint point in stroke.StylusPoints)
            {
                if (bounds.Contains(point.ToPoint()))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
