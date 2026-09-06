using NoteTaker.Core.Models;

namespace NoteTaker.Core.Abstractions;

/// <summary>
/// A rasterized page, plus WHICH part of the page it shows.
/// </summary>
/// <param name="Area">
/// The captured rectangle in sheet-relative coordinates. Usually the whole sheet — (0,0,1,1)
/// — but the capture follows the student onto open canvas, so it can extend past the sheet
/// and carry values outside 0..1. A vision model answers in coordinates relative to the
/// IMAGE it was given, so this is what turns those answers back into positions on the page;
/// without it, a scan of work below the sheet would put every highlight on the sheet instead.
/// </param>
public sealed record PageSnapshotCapture(byte[] Png, int StrokeRevision, NormalizedRegion Area)
{
    /// <summary>The whole sheet: the identity mapping, for callers with nothing better.</summary>
    public static readonly NormalizedRegion WholeSheet = new(0, 0, 1, 1);
}

/// <summary>
/// How strictly a finding box must cover ink. Accepting vision boxes is looser;
/// pruning stale highlights is tighter so blank red marks disappear.
/// </summary>
public enum InkHitTolerance
{
    /// <summary>Small pad — used when dismissing empty / erased marks.</summary>
    Strict,

    /// <summary>Larger pad — used when keeping a fresh vision finding.</summary>
    Loose,
}

/// <summary>
/// Rasterizes the live page. Implemented by the UI layer because only it can reach
/// the ink surface and the PDF background.
/// </summary>
public interface IPageSnapshotProvider
{
    /// <summary>
    /// Rasterizes the page the student is working on.
    /// </summary>
    /// <param name="focus">
    /// Sheet-relative region of recent writing, when the caller has one (a live check does; a
    /// full-page Review scan does not). Narrows the captured area to a neighbourhood around it
    /// instead of the generous default window — sending less page is both cheaper and, per
    /// "performance improves consistently as visual complexity is reduced", more accurate.
    /// Null keeps today's behaviour: the screen, generously padded.
    /// </param>
    Task<PageSnapshotCapture?> CaptureAsync(
        long pageId,
        NormalizedRegion? focus = null,
        CancellationToken ct = default);

    /// <summary>Crops a region so follow-up chat sends far fewer image tokens than a full page.</summary>
    Task<byte[]?> CaptureRegionAsync(long pageId, NormalizedRegion region, CancellationToken ct = default);

    /// <summary>
    /// High-contrast, high-res crop of the written area (optionally around a finding) for chat.
    /// </summary>
    Task<byte[]?> CaptureChatContextAsync(
        long pageId,
        NormalizedRegion? focus = null,
        CancellationToken ct = default);

    /// <summary>
    /// True when any ink stroke sample falls inside the region (with tolerance padding).
    /// </summary>
    Task<bool> RegionContainsInkAsync(
        long pageId,
        NormalizedRegion region,
        InkHitTolerance tolerance = InkHitTolerance.Loose,
        CancellationToken ct = default);

    /// <summary>
    /// Moves a vision model's rough box onto the line of ink it was pointing at, returning null
    /// when nothing plausible is nearby and the original box should stand.
    /// </summary>
    /// <remarks>
    /// Lives here, next to the ink hit-test, because only the UI layer can reach the strokes.
    /// The decision itself is <see cref="Tutor.InkLineSnapper"/> — pure geometry, unit-tested;
    /// this interface just supplies it with the page's stroke bounds.
    /// </remarks>
    Task<NormalizedRegion?> SnapToInkAsync(
        long pageId,
        NormalizedRegion modelBox,
        string? whereHint,
        CancellationToken ct = default);
}
