namespace NoteTaker.Core.Models;

/// <summary>What the tutor concluded about the student's work on this turn.</summary>
public enum SkillOutcome
{
    Right,
    Wrong,
    Unclear,
}

/// <summary>One judgement the tutor made, extracted from its reply.</summary>
public sealed record SkillVerdict(string Skill, SkillOutcome Outcome, string Reason);

/// <summary>A single flagged region on a page, produced by a vision tutor call.</summary>
public sealed class TutorFeedback
{
    public long Id { get; set; }
    public long PageId { get; set; }
    public long? SnapshotId { get; set; }
    public NormalizedRegion Region { get; set; }
    public FeedbackSeverity Severity { get; set; } = FeedbackSeverity.Minor;

    /// <summary>Short label shown next to the highlight. Never the full solution.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Short model-assigned category ("sign errors", "unit conversion"), tagged at scan
    /// time so Review mode can group a page's mistake history without a separate LLM
    /// call. Null for rows written before this existed, or if a model omits it.
    /// </summary>
    public string? Topic { get; set; }

    /// <summary>
    /// The literal ink the model was objecting to, transcribed before it judged anything.
    /// Persisted (schema v6) so a wrong flag is diagnosable by querying what the model
    /// actually thought it was reading, rather than guessing from a screenshot. Null for
    /// rows written before this existed, or if a model omits it.
    /// </summary>
    public string? Reading { get; set; }

    /// <summary>
    /// A coarse vertical position the model reported directly ("top of page", "middle", …),
    /// independent of <see cref="Region"/> — the model's own box is its least reliable output,
    /// so this is a second, independent signal rather than one derived from the first.
    /// </summary>
    public string? PositionHint { get; set; }

    public string Model { get; set; } = string.Empty;
    public TutorMode OriginMode { get; set; } = TutorMode.Live;
    public bool Dismissed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A sidebar conversation, optionally anchored to one highlighted region.</summary>
public sealed class TutorThread
{
    public long Id { get; set; }
    public long PageId { get; set; }
    public long? FeedbackId { get; set; }
    public string Title { get; set; } = "Tutor";
    public ThreadKind Kind { get; set; } = ThreadKind.General;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TutorMessage
{
    public long Id { get; set; }
    public long ThreadId { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A tutor call captured while offline (or over budget) and replayed later.
/// </summary>
public sealed class TutorJob
{
    public long Id { get; set; }
    public long PageId { get; set; }
    public long SnapshotId { get; set; }
    public TutorCallType CallType { get; set; } = TutorCallType.LiveCheck;
    public TutorMode OriginMode { get; set; } = TutorMode.Live;
    public TutorJobState State { get; set; } = TutorJobState.Pending;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One judgement about one skill, at one moment, inside one topic.
/// </summary>
/// <remarks>
/// Deliberately NOT a <see cref="TutorFeedback"/> row. That record is shaped for a vision
/// finding — it requires a Region and carries SnapshotId, Reading and PositionHint, all of
/// which would be null here — and it hangs off a page, whereas a skill belongs to the topic.
/// Keeping them apart also leaves the highlight path intact for whenever page checking returns.
///
/// <see cref="Source"/> is recorded for diagnosis only. It is never weighted and never shown:
/// a grade the student gave themselves counts exactly as much as one the tutor gave, which is
/// a deliberate call — the tutor has a measured habit of inventing errors in correct work.
/// </remarks>
public sealed class SkillEvent
{
    public long Id { get; set; }

    /// <summary>The topic. A section, not a page — the same topic spans many pages.</summary>
    public long SectionId { get; set; }

    /// <summary>Where it happened, so Review can take the student back to the working.</summary>
    public long PageId { get; set; }

    public string Skill { get; set; } = string.Empty;

    public SkillOutcome Outcome { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>Tutor or student. Diagnostic only — see the remarks above.</summary>
    public string Source { get; set; } = "tutor";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One contiguous run of work on one skill: a problem attempted, however many turns it took.
/// </summary>
/// <param name="Corrections">
/// How many times the tutor said the work was wrong during the attempt — the measure of how
/// much help it needed. This, not whether it ended right, is the signal: with a tutor it always
/// ends right eventually.
/// </param>
/// <param name="Turns">Replies spent, kept for diagnosis rather than scoring.</param>
public sealed record SkillAttempt(
    string Skill,
    int Corrections,
    int Turns,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt)
{
    /// <summary>
    /// How independently the attempt was carried out, 0–1. Unaided is 1; each correction needed
    /// cuts it, steeply at first — the difference between no help and one nudge matters far more
    /// than the difference between the fifth and sixth.
    /// </summary>
    public double Independence => 1.0 / (1.0 + Corrections);
}

/// <summary>Which way a skill is moving, comparing earlier attempts against later ones.</summary>
public enum SkillTrend
{
    /// <summary>Fewer than four attempts — not enough to compare halves.</summary>
    Unknown,
    Improving,
    Steady,
    Slipping,
}

/// <summary>
/// One skill's standing inside a topic. <paramref name="Attempts"/> and
/// <paramref name="Corrections"/> are shown beside the meter, because a bar alone cannot tell
/// "three problems, unaided" from "three problems, walked through every line".
/// </summary>
/// <param name="Confidence">0–1, independence weighted toward recent attempts, then decayed.</param>
public sealed record SkillScore(
    string Skill,
    int Attempts,
    int Corrections,
    double Confidence,
    SkillTrend Trend,
    DateTimeOffset LastPractised,
    int DaysSinceLastPractised);

/// <summary>
/// A topic's whole picture. <paramref name="HasEnoughData"/> false means Review says so plainly
/// instead of drawing meters it cannot justify.
/// </summary>
public sealed record TopicConfidence(
    IReadOnlyList<SkillScore> Skills,
    int TotalAttempts,
    bool HasEnoughData,
    double Confidence,
    DateTimeOffset? LastPractised);

/// <summary>
/// The written report for one topic, cached so opening Review is free.
/// </summary>
/// <remarks>
/// <see cref="AttemptsAtGeneration"/> is what makes the cache safe rather than merely fast:
/// it records how much work the report was written from, so <see cref="Tutor.SkillReportGate"/>
/// can tell a report that is merely old from one that is actually out of date.
/// </remarks>
public sealed class SkillReportRecord
{
    /// <summary>The topic. One standing report per section, replaced rather than accumulated.</summary>
    public long SectionId { get; set; }

    public string Content { get; set; } = string.Empty;

    public int AttemptsAtGeneration { get; set; }

    public string Model { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
