using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>Collapses overlapping vision boxes so the same slip is not flagged twice.</summary>
public static class FindingDeduper
{
    private const double IoUThreshold = 0.35;
    private const double IoMinThreshold = 0.55;

    public static IReadOnlyList<TutorRegionFinding> Dedupe(IReadOnlyList<TutorRegionFinding> findings)
    {
        if (findings.Count <= 1)
        {
            return findings;
        }

        // Prefer earlier (usually root-cause) and more severe when boxes collide.
        var ordered = findings
            .Select((finding, index) => (finding, index))
            .OrderByDescending(item => item.finding.Severity)
            .ThenBy(item => item.index)
            .ToList();

        var kept = new List<TutorRegionFinding>();
        foreach (var (finding, _) in ordered)
        {
            if (kept.Any(existing => Overlaps(existing.Region, finding.Region)))
            {
                continue;
            }

            // The model sometimes splits one mistake into two adjacent, non-overlapping boxes
            // on the same line — e.g. one over the operands, one over the result — each with
            // the same label. Geometric overlap can never catch that (the boxes genuinely
            // don't intersect), so merge on "same line, same label" too, widening the
            // surviving box to cover both instead of just dropping the second one's coverage.
            var sameLineIndex = kept.FindIndex(existing => SameLineSameLabel(existing, finding));
            if (sameLineIndex >= 0)
            {
                kept[sameLineIndex] = kept[sameLineIndex] with
                {
                    Region = kept[sameLineIndex].Region.Union(finding.Region),
                };
                continue;
            }

            kept.Add(finding);
        }

        return kept;
    }

    public static bool Overlaps(NormalizedRegion a, NormalizedRegion b) =>
        a.IoU(b) >= IoUThreshold || a.IoMin(b) >= IoMinThreshold;

    private static bool SameLineSameLabel(TutorRegionFinding a, TutorRegionFinding b) =>
        string.Equals(a.Label.Trim(), b.Label.Trim(), StringComparison.OrdinalIgnoreCase)
        && SameLine(a.Region, b.Region);

    /// <summary>Same horizontal band of the page, regardless of how far apart on that line.</summary>
    private static bool SameLine(NormalizedRegion a, NormalizedRegion b)
    {
        var centerA = a.Y + (a.Height / 2);
        var centerB = b.Y + (b.Height / 2);
        var tolerance = Math.Max(Math.Max(a.Height, b.Height), 0.02) * 0.75;
        return Math.Abs(centerA - centerB) <= tolerance;
    }
}
