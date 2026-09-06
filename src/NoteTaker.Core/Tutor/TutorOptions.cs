namespace NoteTaker.Core.Tutor;

public sealed class TutorOptions
{
    /// <summary>Quiet period after the last stroke before a live check may fire.</summary>
    public TimeSpan LiveDebounce { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Floor between two live checks, independent of how often you pause.</summary>
    public TimeSpan MinLiveInterval { get; set; } = TimeSpan.FromSeconds(20);

    public int MaxLiveCallsPerHour { get; set; } = 12;

    /// <summary>
    /// The month's ceiling. The day's allowance is derived from it and from how much of the
    /// month is left, so a quiet day feeds the ones after it — see <see cref="MonthlyBudget"/>.
    /// </summary>
    public decimal MonthlyCostCapUsd { get; set; } = 8.00m;

    /// <summary>
    /// A floor under the derived daily allowance, so a nearly-spent month still answers a
    /// question or two rather than going silent for a week.
    /// </summary>
    public decimal MinimumDailyAllowanceUsd { get; set; } = 0.05m;

    public int SnapshotRetentionPerPage { get; set; } = 5;

    /// <summary>
    /// When this run of the app started. Chat threads created before it are not resumed, so
    /// every launch opens a clean sidebar rather than continuing a conversation about work
    /// the student may not even remember doing.
    /// </summary>
    public DateTimeOffset SessionStart { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Turns of genuine struggle before the hint ladder allows a partial reveal.</summary>
    public int RevealAfterTurns { get; set; } = 4;

    /// <summary>
    /// Whether the app spends anything on looking at the page: live checks, Review scans, and
    /// replaying queued offline scans. Off.
    /// </summary>
    /// <remarks>
    /// Turned off deliberately, not because the plumbing is broken. Measured over 94 scans it
    /// was 18% of spend, and what it bought was mostly wrong on the material actually being
    /// studied — it repeatedly reported correct integrals as missing their limits or their
    /// differential, and a confident wrong flag costs a student more than no flag at all: it
    /// anchors the chat thread to a defect that isn't there and the tutor then argues for it.
    ///
    /// The chat tutor works from its own capture of the page and does not depend on this, so
    /// nothing the student actually uses is lost. Kept as a switch rather than deleted because
    /// the failure is in what the cheap vision model can reliably see, which a better model or
    /// a narrower prompt could change; the code is worth keeping until that is settled.
    /// </remarks>
    public bool VisionEnabled { get; set; }
}
