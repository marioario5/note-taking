using NoteTaker.Core.Tutor;

namespace NoteTaker.AI.Transport;

public sealed record LlmMessage(
    string Role,
    string Text,
    byte[]? ImagePng = null,
    /// <summary>When true, the last content part of this message is an explicit cache breakpoint.</summary>
    bool EndsCachePrefix = false);

/// <summary>
/// How much detail the model is asked to resolve in an attached image.
/// </summary>
/// <remarks>
/// Gemini 3 bills an image by a fixed token allocation per level, NOT by pixel count — which is
/// why rendering the same page smaller changes nothing. Measured on a 1345x1084 page capture:
/// High 1,115 tokens, Medium 551, Low 275. The image is ~39% of a chat turn's input, so this is
/// the only dial that meaningfully moves it.
///
/// Provider-specific by design: only the native Gemini API accepts it. Gemini's
/// OpenAI-compatible shim rejects the field outright (400 "Unknown name") in every spelling
/// tried, including <c>extra_body.google</c>, so <see cref="OpenAiTransport"/> must never emit
/// it. Default is <see cref="High"/> so a transport that ignores this keeps today's behaviour.
/// </remarks>
public enum ImageDetail
{
    Low,
    Medium,
    High,
}

public sealed record LlmRequest(
    string Model,
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    int MaxOutputTokens,
    bool ExpectJson = false,
    /// <summary>
    /// Overrides how hard the model thinks for this one call, or null to take the transport's
    /// default. Only meaningful on providers with a thinking dial.
    /// </summary>
    /// <remarks>
    /// Per call because the answer differs by job. Judging a student's working needs room to
    /// check itself — measured, minimal thinking invented an error in a correct integral — while
    /// a call that only reads back numbers already computed has nothing to check, and thinking
    /// is the larger half of what it costs.
    /// </remarks>
    string? ThinkingLevel = null,
    /// <summary>OpenAI prompt_cache_key — routes repeats to the same cache shard.</summary>
    /// <remarks>
    /// Inert on Gemini, and not for want of wiring: both implicit and explicit caching need a
    /// stable prefix of at least 4,096 tokens, and this app's only stable prefix is the
    /// ~1,632-token system prompt. Measured — two identical 3,646-token requests returned no
    /// cached-token field at all. Padding a request up to the floor to earn a discount on part
    /// of it is a worse trade, and an implicit miss bills the padding at full rate. Kept for
    /// real OpenAI, where it does work.
    /// </remarks>
    string? PromptCacheKey = null,
    /// <summary>When true, only explicit breakpoints are cached (GPT-5.6+). Inert on Gemini.</summary>
    bool ExplicitPromptCache = false,
    /// <summary>Mark the system prompt's last content part as a cache breakpoint. Inert on Gemini.</summary>
    bool CacheSystemPrompt = false,
    /// <summary>Detail level for attached images; honoured only by the native Gemini transport.</summary>
    ImageDetail ImageDetail = ImageDetail.High);

public sealed record LlmResponse(string Content, TokenUsage Usage);

public interface ILlmTransport
{
    Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>
    /// Same call as <see cref="SendAsync"/>, but reports text through
    /// <paramref name="onDelta"/> as it arrives instead of only at the end. Still returns the
    /// complete <see cref="LlmResponse"/>, so callers keep persisting and cost-logging exactly
    /// as before — the callback is an extra, not a replacement.
    /// </summary>
    /// <remarks>
    /// A callback rather than an <c>IAsyncEnumerable</c> on purpose. Callers do real
    /// bookkeeping after a reply lands (save the message, log usage, refresh the panel), and
    /// with an enumerable that work has to happen either mid-enumeration or after a loop the
    /// caller must be careful to always drain — abandon it early and the HTTP response leaks.
    /// This keeps the one-call-one-result shape and treats streaming as an observer.
    ///
    /// <paramref name="onDelta"/> is invoked on a background thread. UI callers must marshal.
    /// Implementations that cannot stream may simply forward to <see cref="SendAsync"/> and
    /// deliver the whole reply as a single delta; that is a degraded experience, not a bug.
    /// </remarks>
    Task<LlmResponse> StreamAsync(
        LlmRequest request,
        Action<string> onDelta,
        CancellationToken ct = default);
}

public sealed class MissingApiKeyException(string provider)
    : InvalidOperationException($"No API key stored for {provider}. Add one in Settings.");
