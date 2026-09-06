using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>
/// Turns a vision model's rough guess about where a mistake is into a rectangle that actually
/// sits on the student's ink.
/// </summary>
/// <remarks>
/// Localization is the one thing these models are measurably worst at. arXiv 2501.07244
/// benchmarks the three jobs this app asks for and finds detection ~0.65, correction
/// ~0.66-0.77, but localization only 0.43-0.45 — a ~30 point collapse, at the frontier. Every
/// highlight in this app is a box drawn over ink, so the product was built on the weakest
/// capability, and no model swap fixes that.
///
/// What the app has that the model does not is the ink itself: exact coordinates for every
/// stroke. So the division of labour is inverted. The model says WHAT is wrong (its strongest
/// output) and roughly where; this decides exactly where, by finding the line of real writing
/// that best matches. The model's own box is kept only as a tie-breaker between candidate
/// lines — a signal that is right about 45% of the time is a weak prior, not an answer.
///
/// Deliberately geometric, with no attempt to match the model's transcription against
/// recognised text. That would need handwriting recognition in the scan path, which this does
/// not budget for, and a wrong text match would be more confidently wrong than no match at
/// all. <c>Reading</c> is persisted for diagnosis and could feed a future recognition-based
/// pass; it is not used here, and the tests say so.
/// </remarks>
public static class InkLineSnapper
{
    /// <summary>
    /// Coarse vertical bands, in the exact words the scan prompts ask the model to use.
    /// Shared so the prompt, the snapper and the sidebar's own wording cannot drift apart.
    /// </summary>
    public static readonly string[] Bands =
    [
        "top of page",
        "upper third",
        "middle",
        "lower third",
        "foot of page",
    ];

    /// <summary>Which band a normalized y-centre falls in, as an index into <see cref="Bands"/>.</summary>
    public static int BandIndexOf(double centreY) => centreY switch
    {
        < 0.2 => 0,
        < 0.4 => 1,
        < 0.6 => 2,
        < 0.8 => 3,
        _ => 4,
    };

    /// <summary>The band's name, for display and for asking the model to echo it back.</summary>
    public static string DescribeBand(NormalizedRegion region) =>
        Bands[BandIndexOf(region.Y + (region.Height / 2))];

    /// <summary>Parses a model-supplied hint back to a band index; null when unrecognised.</summary>
    public static int? ParseBand(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        var trimmed = hint.Trim();
        for (var i = 0; i < Bands.Length; i++)
        {
            if (string.Equals(Bands[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Two strokes belong to the same line if their vertical spans overlap, or sit within this
    /// much of each other. In normalized page units — roughly a third of a line of handwriting,
    /// which is enough to keep a superscript with its base but not enough to swallow the line
    /// below.
    /// </summary>
    private const double LineGap = 0.012;

    /// <summary>Breathing room around the snapped result so the highlight does not clip the ink.</summary>
    private const double Padding = 0.008;

    /// <summary>
    /// Best rectangle over real ink for a finding, or null when nothing plausible is nearby and
    /// the caller should keep whatever the model said.
    /// </summary>
    /// <param name="strokeBounds">Every stroke's bounds, in sheet-normalized coordinates.</param>
    /// <param name="modelBox">The model's own box. Used only to break ties.</param>
    /// <param name="whereHint">The model's coarse band, e.g. "middle".</param>
    public static NormalizedRegion? Snap(
        IReadOnlyList<NormalizedRegion> strokeBounds,
        NormalizedRegion modelBox,
        string? whereHint)
    {
        if (strokeBounds.Count == 0)
        {
            return null;
        }

        var lines = GroupIntoLines(strokeBounds);
        if (lines.Count == 0)
        {
            return null;
        }

        var band = ParseBand(whereHint);

        NormalizedRegion? best = null;
        var bestScore = double.NegativeInfinity;

        foreach (var line in lines)
        {
            var score = Score(line, modelBox, band);
            if (score > bestScore)
            {
                bestScore = score;
                best = line;
            }
        }

        // Every candidate was implausible — no band agreement and no overlap with the model's
        // box. Saying "I don't know" beats moving the highlight somewhere confidently wrong.
        if (best is not { } chosen || bestScore <= 0)
        {
            return null;
        }

        // Narrow horizontally to the part of the line the model was pointing at, when its box
        // genuinely overlaps: a mistake is usually one term, not the whole line. Ignored when
        // the box misses the line entirely, which is the case this exists to survive.
        var result = chosen;
        var overlapLeft = Math.Max(chosen.X, modelBox.X);
        var overlapRight = Math.Min(chosen.X + chosen.Width, modelBox.X + modelBox.Width);
        if (overlapRight - overlapLeft > 0.02)
        {
            result = new NormalizedRegion(
                overlapLeft,
                chosen.Y,
                overlapRight - overlapLeft,
                chosen.Height);
        }

        return result.Inflate(Padding);
    }

    /// <summary>
    /// Clusters stroke bounds into lines of writing by vertical proximity.
    /// </summary>
    private static List<NormalizedRegion> GroupIntoLines(IReadOnlyList<NormalizedRegion> strokeBounds)
    {
        var ordered = strokeBounds
            .Where(b => !b.IsEmpty)
            .OrderBy(b => b.Y + (b.Height / 2))
            .ToList();

        var lines = new List<NormalizedRegion>();
        foreach (var stroke in ordered)
        {
            if (lines.Count == 0)
            {
                lines.Add(stroke);
                continue;
            }

            var current = lines[^1];
            var gap = stroke.Y - (current.Y + current.Height);

            // Overlapping vertically, or near enough to be the same line of writing.
            if (gap <= LineGap)
            {
                lines[^1] = current.Union(stroke);
            }
            else
            {
                lines.Add(stroke);
            }
        }

        return lines;
    }

    private static double Score(NormalizedRegion line, NormalizedRegion modelBox, int? band)
    {
        var score = 0.0;

        // The band the model named directly. Weighted above the box because a coarse
        // five-way position is something these models get right far more often than a
        // precise rectangle.
        if (band is { } wanted)
        {
            var distance = Math.Abs(BandIndexOf(line.Y + (line.Height / 2)) - wanted);
            score += distance switch
            {
                0 => 3.0,
                1 => 1.0, // adjacent band: plausible, the boundaries are arbitrary
                _ => -1.0,
            };
        }

        // Vertical overlap with the model's box, as a fraction of the line's height. The
        // tie-breaker: enough to choose between two lines in the same band, not enough to
        // overrule the band itself.
        var top = Math.Max(line.Y, modelBox.Y);
        var bottom = Math.Min(line.Y + line.Height, modelBox.Y + modelBox.Height);
        var overlap = bottom - top;
        if (overlap > 0 && line.Height > 0)
        {
            score += 2.0 * Math.Min(1.0, overlap / line.Height);
        }

        return score;
    }
}
