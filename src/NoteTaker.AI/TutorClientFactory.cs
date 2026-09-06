using NoteTaker.AI.Transport;
using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Tutor;

namespace NoteTaker.AI;

/// <summary>Builds transports from settings, pulling keys from the OS credential vault.</summary>
public static class TutorClientFactory
{
    /// <param name="visionHttp">
    /// Short-timeout client for scans — a scan that hasn't returned by the time the next stroke
    /// would want one anyway should fail fast and let that next attempt through, not sit on the
    /// connection until a shared 90s ceiling.
    /// </param>
    /// <param name="chatHttp">
    /// Long-timeout client for chat — a streamed reply trickles in over a real conversation
    /// length, and <see cref="ILlmTransport.StreamAsync"/> holds the connection open for the
    /// whole read, not just until headers arrive.
    /// </param>
    public static ITutorClient Create(
        HttpClient visionHttp,
        HttpClient chatHttp,
        LlmSettings settings,
        TutorOptions options,
        ISecretStore secrets)
    {
        var vision = CreateTransport(
            visionHttp,
            settings.VisionProvider,
            settings.VisionEndpoint ?? LlmSettings.DefaultEndpoint(settings.VisionProvider),
            settings.VisionModel,
            secrets);

        var chat = CreateTransport(
            chatHttp,
            settings.ChatProvider,
            settings.ChatEndpoint ?? LlmSettings.DefaultEndpoint(settings.ChatProvider),
            settings.ChatModel,
            secrets);

        return new TutorClient(vision, chat, settings, options);
    }

    private static ILlmTransport CreateTransport(
        HttpClient http,
        LlmProviderKind kind,
        string endpoint,
        string model,
        ISecretStore secrets)
    {
        var key = () => secrets.Get(LlmSettings.SecretKey(kind));

        return kind switch
        {
            LlmProviderKind.Anthropic => new AnthropicTransport(http, endpoint, key),

            // Gemini speaks its own API here rather than its OpenAI-compatible shim. The shim
            // works, but it cannot express mediaResolution — the only field that reduces the
            // page image's ~1,115 tokens, which are ~39% of a chat turn's input. See
            // GeminiTransport for the measurements.
            LlmProviderKind.Gemini => new GeminiTransport(
                http,
                endpoint,
                key,
                thinkingLevel: ThinkingLevelFor(model)),

            _ => new OpenAiTransport(
                http,
                endpoint,
                key,
                kind.ToString(),
                supportsExplicitCache: true,
                reasoningEffort: null),
        };
    }

    /// <summary>
    /// How hard a Gemini model should think before answering, or null to leave its default.
    /// </summary>
    /// <remarks>
    /// "LOW", not the level below it. Minimal thinking was chosen when the problem was reasoning
    /// tokens eating the output budget, and it does solve that — but it leaves the model no room
    /// to CHECK anything, and checking is most of this job. Measured on the case that exposed
    /// it: asked whether a correct integral evaluation was correct, gemini-3.6-flash with
    /// minimal thinking spent 0 thinking tokens and answered "NO", inventing an error in a
    /// student's right answer; at LOW it spent 281 and answered "YES". It was also FASTER that
    /// way (2.2s vs 3.7s), because a wrong first instinct still has to be written out at length
    /// to justify itself.
    ///
    /// Numeric budgets are deprecated on Gemini 3 — labels only. Null for anything unrecognised
    /// so an unsupported value can never 400 the whole tutor, which is how gemini-3-pro-preview's
    /// retirement presented.
    /// </remarks>
    private static string? ThinkingLevelFor(string model) =>
        model.Contains("flash", StringComparison.OrdinalIgnoreCase) ? "LOW" : null;
}
