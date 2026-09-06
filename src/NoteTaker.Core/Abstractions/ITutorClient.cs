using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.Core.Abstractions;

/// <summary>
/// Transport-agnostic view of the tutor models. Vision calls take a page image and
/// return region-anchored findings; chat calls are text-only and much cheaper.
/// </summary>
public interface ITutorClient
{
    /// <summary>Scans a page image and returns regions worth flagging.</summary>
    Task<TutorScanResult> ScanPageAsync(TutorScanRequest request, CancellationToken ct = default);

    /// <summary>
    /// Socratic follow-up for one highlighted region. Guides, never solves outright.
    /// </summary>
    /// <param name="onDelta">
    /// Optional observer called with each piece of the reply as it arrives, so the sidebar can
    /// show text within about a second instead of a blank panel until the whole answer lands.
    /// The returned result is still the complete reply either way — callers persist and cost-log
    /// from that, not from the deltas. Invoked off the UI thread; marshal before touching UI.
    /// </param>
    Task<TutorChatResult> ContinueThreadAsync(
        TutorChatRequest request,
        Action<string>? onDelta = null,
        CancellationToken ct = default);

    /// <summary>Aggregates a session's findings into recurring error patterns.</summary>
    Task<TutorChatResult> SummarizePatternsAsync(
        IReadOnlyList<TutorFeedback> feedback,
        CancellationToken ct = default);

    /// <summary>Generates practice problems in the style of the supplied pages.</summary>
    Task<TutorChatResult> GeneratePracticeAsync(
        PracticeGenerationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Text-only: turns everything Review just found on a page into one holistic opening
    /// Socratic message ending in a single targeting question. <paramref name="findings"/>
    /// is this pass's fresh results; <paramref name="recurringTopics"/> is optional extra
    /// context (topics that have come up before on this same page) used to enrich the
    /// message, not to gate whether it's sent. No images sent — this is the cheap step
    /// Review mode uses instead of resending page pictures to talk through the mistakes.
    /// </summary>
    /// <summary>
    /// Writes the standing study report for one topic.
    /// </summary>
    /// <remarks>
    /// Sent the topic's aggregates and nothing else — no chat history, no page image. That is
    /// what makes it cheap enough to generate unattended, and it uses its own prompt so the
    /// Socratic tutor never carries the report's instructions in its context.
    /// </remarks>
    Task<TutorChatResult> GenerateSkillReportAsync(
        string topicName,
        TopicConfidence topic,
        CancellationToken ct = default);

    Task<TutorChatResult> GenerateWeaknessReviewAsync(
        IReadOnlyList<TutorFeedback> findings,
        IReadOnlyList<WeaknessTopic> recurringTopics,
        CancellationToken ct = default);

    /// <summary>Marks one attempt from the page alone, for Review, with no tutoring.</summary>
    /// <remarks>
    /// The reply is a verdict tag and nothing else, so <see cref="TutorChatResult.Content"/>
    /// comes back empty by design and only the verdict is meaningful.
    /// </remarks>
    Task<TutorChatResult> CheckSkillAsync(byte[]? pagePng, CancellationToken ct = default);
}

/// <summary>Produces CLIP-style image embeddings for the related-pages graph.</summary>
public interface IEmbeddingModel
{
    int Dimensions { get; }
    Task<float[]> EmbedImageAsync(byte[] png, CancellationToken ct = default);
    bool IsAvailable { get; }
}
