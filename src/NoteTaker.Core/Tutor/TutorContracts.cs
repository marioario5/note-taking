using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

public readonly record struct TokenUsage(
    int TokensIn,
    int TokensOut,
    int TokensCached = 0,
    int TokensCacheWrite = 0)
{
    public static readonly TokenUsage Zero = new(0, 0);

    /// <summary>Uncached input tokens (total in minus cache hits).</summary>
    public int TokensUncachedIn => Math.Max(0, TokensIn - TokensCached);
}

public sealed record TutorScanRequest(
    long PageId,
    byte[] PagePng,
    TutorMode Mode,
    NormalizedRegion? FocusRegion = null,
    string? PageContext = null);

public sealed record TutorRegionFinding(
    NormalizedRegion Region,
    FeedbackSeverity Severity,
    string Label,
    string? Topic = null,
    /// <summary>
    /// The literal ink the model is objecting to, transcribed before it judged anything.
    /// arXiv 2501.07244: an explicit transcription step is worth up to +128% to a model this
    /// size, and it is what stops the model pattern-matching a familiar textbook answer
    /// instead of reading what is actually on the page — the failure mode that flagged a
    /// correct "dy dx" as missing. Also the primary signal ink-snapping matches against real
    /// strokes, since <paramref name="Region"/> is the model's own box and boxes are the one
    /// thing it is measurably worst at (same paper: localization ~30 points below detection,
    /// even at the frontier).
    /// </summary>
    string? Reading = null,
    /// <summary>
    /// A coarse vertical position — "top of page", "upper third", "middle", "lower third", or
    /// "foot of page", the same five bands <c>FeedbackItemViewModel.Describe</c> already
    /// renders — asked for directly rather than derived from <paramref name="Region"/>, so it
    /// is an independent signal rather than an echo of the box being de-emphasized.
    /// </summary>
    string? Where = null);

/// <summary>
/// A recurring mistake topic on one page, ranked by <c>WeaknessAggregator.Rank</c> from
/// that page's full <see cref="TutorFeedback"/> history (active and dismissed alike).
/// </summary>
public sealed record WeaknessTopic(
    string Topic,
    int Count,
    FeedbackSeverity MaxSeverity,
    string SampleLabel);

public sealed record TutorScanResult(
    IReadOnlyList<TutorRegionFinding> Findings,
    string SidebarSummary,
    TokenUsage Usage,
    string Model)
{
    public static TutorScanResult Empty(string model) =>
        new([], string.Empty, TokenUsage.Zero, model);
}

/// <summary>
/// A sidebar turn. <paramref name="Socratic"/> forces the model to guide with questions,
/// and <paramref name="UserTurnCount"/> drives the hint ladder so a full reveal only
/// becomes available after the student has genuinely been stuck for several turns.
/// </summary>
public sealed record TutorChatRequest(
    long PageId,
    long ThreadId,
    IReadOnlyList<TutorMessage> History,
    string UserMessage,
    /// <summary>
    /// Fresh crop for THIS turn: the specific finding currently under discussion, or a
    /// readable crop of the whole page when nothing is flagged. There is no separate
    /// one-time "setup" image — every turn carries whichever image matches its own anchor,
    /// so the image and <see cref="FeedbackLabel"/> can never drift out of sync with each
    /// other mid-conversation.
    /// </summary>
    byte[]? RegionCropPng,
    string? FeedbackLabel,
    bool Socratic,
    int UserTurnCount,
    /// <summary>
    /// The page is byte-identical to the picture already sent this thread, so no image rides
    /// with this turn. The model is told, in one clause, that what it last saw still stands —
    /// otherwise it reports it cannot see the page and asks the student to describe it.
    /// </summary>
    bool PageUnchanged = false);

/// <param name="Verdict">
/// What the tutor judged this turn, if it tagged the reply. Carried here rather than
/// re-derived by a second call: the tutor already decides correct-versus-wrong on every turn,
/// so this is that same judgement made machine-readable. Null whenever the model omitted or
/// malformed the tag, which must degrade to "no data point", never to a failed turn.
/// </param>
public sealed record TutorChatResult(
    string Content,
    TokenUsage Usage,
    string Model,
    SkillVerdict? Verdict = null)
{
    public static TutorChatResult Empty(string model) => new(string.Empty, TokenUsage.Zero, model);
}

public sealed record PracticeGenerationRequest(
    string SourceName,
    IReadOnlyList<byte[]> PageImages,
    int QuestionCount,
    string? ExtraInstructions = null);
