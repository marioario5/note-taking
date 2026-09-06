using NoteTaker.AI.Transport;
using NoteTaker.Core.Models;

namespace NoteTaker.AI;

public enum LlmProviderKind
{
    /// <summary>OpenAI, or any service exposing the /chat/completions shape.</summary>
    OpenAiCompatible = 0,

    /// <summary>Gemini through its OpenAI-compatible endpoint.</summary>
    Gemini = 1,

    Anthropic = 2,
}

public sealed class LlmSettings
{
    // Vision only needs to catch simple slips and stays on the cheap tier deliberately — see
    // AppSettings for the fuller rationale and the migration that carries existing installs
    // forward. This class's own defaults matter mainly to code that constructs it directly
    // (tests); the running app always goes through AppSettings.ToLlmSettings().
    public LlmProviderKind VisionProvider { get; set; } = LlmProviderKind.Gemini;

    public string VisionModel { get; set; } = "gemini-3.1-flash-lite";

    // Chat was moved to the Pro tier on the theory that a flash model could not explain a
    // correct integral setup. That reply turned out to have been TRUNCATED by our own output
    // cap, not fumbled by the model — see MaxOutputTokensFor below. With the cap bug fixed,
    // the measured trade on this install's key is: gemini-3.6-flash answers in full in ~1.4s,
    // gemini-3.1-pro-preview takes ~5-6s, cannot use "minimal" reasoning effort, and bills
    // its thinking at Pro output rates. Latency was the original complaint, so Flash wins on
    // the evidence. Switch ChatModel to gemini-3.1-pro-preview in Settings to trade it back.
    public LlmProviderKind ChatProvider { get; set; } = LlmProviderKind.Gemini;

    public string ChatModel { get; set; } = "gemini-3.6-flash";

    /// <summary>
    /// How much detail the tutor is asked to resolve in the page image. Native Gemini only.
    /// </summary>
    /// <remarks>
    /// Low by measurement, not by optimism. The image is ~39% of a chat turn's input and Gemini
    /// bills it per level (High 1,115 / Medium 551 / Low 275 tokens), so this is the largest
    /// remaining lever. Legibility was tested rather than assumed: six fine-detail transcription
    /// questions across two saved page captures, three repetitions each, 18/18 correct at Low —
    /// including the "x/2" inner integral limit this app previously misread as "1/2", plus
    /// exponents, signs and both integrals' bounds. It reads cleanly because the app renders its
    /// own captures high-contrast with fattened strokes (PageRenderer.ForOcr) before sending.
    ///
    /// Raise this FIRST if handwriting is ever misread again — it is the setting that trades
    /// directly against that failure.
    /// </remarks>
    public ImageDetail ChatImageDetail { get; set; } = ImageDetail.Low;

    /// <summary>
    /// Includes headroom for GPT-5.x reasoning tokens, which count against
    /// <c>max_completion_tokens</c> alongside the visible reply.
    /// </summary>
    /// <remarks>
    /// Raised from 1600 after chat answers were seen ending mid-sentence ("...serves as the
    /// upper bound and"). Reasoning tokens are billed against the same ceiling as the text
    /// the student reads, so on a turn that thinks hard the visible reply is what gets cut.
    /// A truncated explanation is worse than useless — it reads as the tutor losing its
    /// train of thought — and the tutor prompts already cap replies at a few sentences, so
    /// the extra headroom is spent on reasoning rather than on longer answers.
    /// </remarks>
    public int MaxOutputTokens { get; set; } = 4000;

    /// <summary>
    /// Per-call-type output ceiling. Falls back to <see cref="MaxOutputTokens"/> for anything
    /// not listed, so a future call type added before anyone sizes it individually still gets
    /// a working cap rather than a missing one.
    /// </summary>
    /// <remarks>
    /// One shared 4000-token ceiling used to serve every call type — a two-sentence Socratic
    /// hint and an 8-region Review scan alike. That is a real part of why chat replies were
    /// seen ending mid-formula: nothing stopped a reply that had wandered into unrequested
    /// deliberation from running until it hit the SAME wall a full page scan is sized for.
    /// Sized here per type instead: a scan needs room for several JSON regions (each now
    /// carrying a <c>reading</c> transcription per the transcribe-first change), while chat's
    /// own prompt already caps replies at a few sentences and should be held to that.
    ///
    /// These are ceilings, not budgets: a cap is only "spent" if the model actually emits
    /// the tokens, and every prompt already constrains its own reply length. So each one is
    /// sized to survive a thinking model rather than trimmed to what a good reply needs.
    /// Measured on this install's key, hidden reasoning tokens are charged against the cap
    /// but excluded from the reported <c>completion_tokens</c>, so a tight cap does not
    /// produce a short answer — it produces a truncated one, with nothing in the usage log
    /// to say why. The first sizing pass here set SocraticChat to 500 and by that measurement
    /// left roughly 14 tokens of visible reply. <c>reasoningEffort</c> in
    /// <see cref="Transport.OpenAiTransport"/> is the real fix; this headroom is the safety
    /// net for models that reject it.
    /// </remarks>
    public int MaxOutputTokensFor(TutorCallType callType) => callType switch
    {
        TutorCallType.LiveCheck => 2000, // up to 3 regions, each a small JSON object
        TutorCallType.ReviewScan => 3000, // up to 8 regions
        TutorCallType.SocraticChat => 1500, // prompt: "a few sentences", not a lecture
        TutorCallType.PatternSummary => 1200, // prompt caps this at 120 words
        TutorCallType.PracticeGeneration => 4000, // a worksheet's worth of fresh questions
        TutorCallType.WeaknessReview => 1500, // a short opening message, not a lecture
        // Deliberately generous. This is the one call the student asks for by name and then
        // sits and reads, and it fires at most a few times a topic — a truncated report is a far
        // worse trade here than the tokens.
        TutorCallType.SkillReport => 4000,
        TutorCallType.SkillCheck => 60, // one tag line and nothing else
        _ => MaxOutputTokens,
    };

    /// <summary>Overrides the provider default, for proxies or self-hosted gateways.</summary>
    public string? VisionEndpoint { get; set; }

    public string? ChatEndpoint { get; set; }

    public static string DefaultEndpoint(LlmProviderKind kind) => kind switch
    {
        LlmProviderKind.OpenAiCompatible => "https://api.openai.com/v1",
        // Gemini's NATIVE base, not the /openai compatibility shim it used to point at.
        // GeminiTransport appends "/models/{model}:generateContent". The shim cannot express
        // mediaResolution, which is the only field that reduces this app's image cost.
        LlmProviderKind.Gemini => "https://generativelanguage.googleapis.com/v1beta",
        LlmProviderKind.Anthropic => "https://api.anthropic.com/v1",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Credential-vault key for a provider's API key.</summary>
    public static string SecretKey(LlmProviderKind kind) => $"NoteTaker.ApiKey.{kind}";
}
