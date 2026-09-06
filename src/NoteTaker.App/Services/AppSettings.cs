using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoteTaker.AI;
using NoteTaker.AI.Transport;
using NoteTaker.Core.Tutor;

namespace NoteTaker.App.Services;

/// <summary>
/// Everything that is not a secret. API keys live in the Windows Credential Manager;
/// this file only records which provider and budget you chose.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // Vision only has to catch simple slips, so it stays on the cheap tier deliberately — see
    // the redesign notes on ChatHistoryWindow and TutorClient.ContinueThreadAsync for why the
    // budget instead goes toward the model actually doing the teaching, below.
    public LlmProviderKind VisionProvider { get; set; } = LlmProviderKind.Gemini;
    public string VisionModel { get; set; } = "gemini-3.1-flash-lite";

    // Pro tier: chat is a correction-and-explanation job, not a localization job, and that is
    // exactly where the Pro/Flash gap is widest (arXiv 2501.07244: 0.77 vs 0.66 on error
    // correction). Paid for by trimming input waste (ChatHistoryWindow, PageSnapshotProvider's
    // focus window) rather than by raising the daily cap on its own.
    public LlmProviderKind ChatProvider { get; set; } = LlmProviderKind.Gemini;
    public string ChatModel { get; set; } = "gemini-3.6-flash";

    /// <summary>
    /// Detail the tutor resolves in the page image: Low, Medium or High. Native Gemini only.
    /// </summary>
    /// <remarks>
    /// The largest remaining cost lever — the image is ~39% of a chat turn's input and Gemini
    /// prices it per level, not per pixel. Low tested 18/18 on fine-detail transcription; see
    /// <see cref="LlmSettings.ChatImageDetail"/>. Raise this first if handwriting is misread.
    /// </remarks>
    public ImageDetail ChatImageDetail { get; set; } = ImageDetail.Low;

    /// <summary>
    /// Overrides the provider's default base URL — for a proxy or a self-hosted gateway.
    /// Null uses <see cref="LlmSettings.DefaultEndpoint"/>.
    /// </summary>
    /// <remarks>
    /// <c>TutorClientFactory</c> has always known how to honour this
    /// (<c>settings.VisionEndpoint ?? DefaultEndpoint(...)</c>) — there was simply nowhere to
    /// set it from, since <see cref="ToLlmSettings"/> only ever forwarded provider and model.
    /// </remarks>
    public string? VisionEndpoint { get; set; }

    public string? ChatEndpoint { get; set; }

    public double LiveDebounceSeconds { get; set; } = 2.5;
    public double MinLiveIntervalSeconds { get; set; } = 20;
    public int MaxLiveCallsPerHour { get; set; } = 12;
    /// <summary>
    /// Daily spend ceiling. Raised from 0.25 to give a Pro-tier chat model headroom.
    /// </summary>
    /// <remarks>
    /// Measured usage, not guessed: a heavy day logged 183 calls for $0.299, and chat alone
    /// was 82% of that at 3,600-6,300 input tokens per turn — unbounded history plus a fresh
    /// image every turn (see ChatHistoryWindow). Vision stays on the cheap flash-lite tier by
    /// design (it only needs to catch simple errors); the budget is meant for chat, which is
    /// what actually teaches. This cap is headroom while chat moves up a tier and its input
    /// waste gets trimmed in parallel — it is not licence for the scan to get expensive.
    /// </remarks>
    /// <summary>
    /// The month's ceiling. The daily allowance is derived from this and from how much of the
    /// month is left, so a quiet day feeds the ones after it rather than being lost.
    /// </summary>
    public decimal MonthlyCostCapUsd { get; set; } = 8.00m;
    public int SnapshotRetentionPerPage { get; set; } = 5;
    public int RevealAfterTurns { get; set; } = 4;

    /// <summary>
    /// Whether the app spends anything looking at the page (live checks and Review scans).
    /// Off — see <see cref="TutorOptions.VisionEnabled"/> for why. Flip it here to try again.
    /// </summary>
    public bool VisionEnabled { get; set; }

    /// <summary>Optional path to a CLIP image-encoder ONNX model for richer page similarity.</summary>
    public string? ClipModelPath { get; set; }

    public string? PythonPath { get; set; }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteTaker",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
                loaded.MigrateStockDefaults();
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt settings file should never stop you from taking notes.
        }

        return new AppSettings();
    }

    /// <summary>
    /// Moves installs still on a superseded stock default onto the current one, without
    /// overriding a deliberate custom choice. Steps chain deliberately: an install still on
    /// Claude Haiku falls through Haiku → luna → the current Gemini Pro default in one pass,
    /// each block re-checking the (possibly just-updated) value rather than short-circuiting.
    /// </summary>
    private void MigrateStockDefaults()
    {
        var changed = false;

        if (string.Equals(VisionModel, "gemini-2.5-flash-lite", StringComparison.OrdinalIgnoreCase)
            && VisionProvider == LlmProviderKind.Gemini)
        {
            VisionModel = "gemini-3.1-flash-lite";
            changed = true;
        }

        if (string.Equals(ChatModel, "claude-haiku-4-5", StringComparison.OrdinalIgnoreCase)
            && ChatProvider == LlmProviderKind.Anthropic)
        {
            ChatProvider = LlmProviderKind.OpenAiCompatible;
            ChatModel = "gpt-5.6-luna";
            changed = true;
        }

        // Moves installs off the OpenAI stock default AND off the flash-tier chat model onto
        // the Pro tier. The flash-preview install still failed at explaining a correct
        // integral setup — chat needs the stronger model, not another flash variant.
        if ((string.Equals(ChatModel, "gpt-5.6-luna", StringComparison.OrdinalIgnoreCase)
                && ChatProvider == LlmProviderKind.OpenAiCompatible)
            || (string.Equals(ChatModel, "gemini-3-flash-preview", StringComparison.OrdinalIgnoreCase)
                && ChatProvider == LlmProviderKind.Gemini))
        {
            ChatProvider = LlmProviderKind.Gemini;
            ChatModel = "gemini-3.6-flash";
            changed = true;
        }

        // gemini-3-pro-preview was retired server-side and now 404s on every call, taking the
        // whole tutor down rather than degrading it. Verified against the live model list for
        // this install's key: it is absent, so no install may be left pointing at it.
        if (string.Equals(ChatModel, "gemini-3-pro-preview", StringComparison.OrdinalIgnoreCase)
            && ChatProvider == LlmProviderKind.Gemini)
        {
            ChatModel = "gemini-3.6-flash";
            changed = true;
        }

        // Gemini moved from its OpenAI-compatible shim to its native API, and the two do not
        // share a base URL. An install that had saved the old ".../v1beta/openai" endpoint —
        // including one that merely had the old default written out — would have it appended
        // with "/models/{model}:generateContent" and 404 every call. Clearing it falls back to
        // the correct native default. A genuinely custom proxy is left alone.
        foreach (var stale in new[] { "/v1beta/openai", "/v1beta/openai/" })
        {
            if (ChatEndpoint?.EndsWith(stale, StringComparison.OrdinalIgnoreCase) == true)
            {
                ChatEndpoint = null;
                changed = true;
            }

            if (VisionEndpoint?.EndsWith(stale, StringComparison.OrdinalIgnoreCase) == true)
            {
                VisionEndpoint = null;
                changed = true;
            }
        }

        // A1 raised the stock cap but only in source; installs that had already saved a
        // settings file kept the old stock 0.25 and never saw the headroom. Only the exact
        // superseded stock value moves — a cap you set yourself is left alone.
        // The daily cap became a monthly one. A saved 0.32/day is 9.60 in a 30-day month, but
        // the figure agreed was eight, so the migration lands on eight rather than multiplying
        // an old default the student never chose.
        if (MonthlyCostCapUsd <= 0m)
        {
            MonthlyCostCapUsd = 8.00m;
            changed = true;
        }

        if (changed)
        {
            Save();
        }

        // Older installs used a 3-turn unlock; bump to the current ladder length.
        if (RevealAfterTurns < 4)
        {
            RevealAfterTurns = 4;
            Save();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, SerializerOptions));
    }

    public LlmSettings ToLlmSettings() => new()
    {
        VisionProvider = VisionProvider,
        VisionModel = VisionModel,
        ChatProvider = ChatProvider,
        ChatModel = ChatModel,
        VisionEndpoint = string.IsNullOrWhiteSpace(VisionEndpoint) ? null : VisionEndpoint,
        ChatEndpoint = string.IsNullOrWhiteSpace(ChatEndpoint) ? null : ChatEndpoint,
        ChatImageDetail = ChatImageDetail,
    };

    /// <summary>Pen nib in page units, as last left on the toolbar slider.</summary>
    public double PenWidth { get; set; } = 1.5;

    /// <summary>
    /// Where the camera was when the app last closed. Null until a first clean shutdown, so
    /// a fresh install still opens centred on the sheet rather than at some arbitrary corner.
    /// </summary>
    public double? ViewZoom { get; set; }

    public double? ViewPanX { get; set; }

    public double? ViewPanY { get; set; }

    /// <summary>
    /// Whether the window was maximized when the app last closed.
    /// </summary>
    /// <remarks>
    /// Defaults to true: this is a full-page note-taking app on a tablet, so filling the
    /// screen is what anyone wants on a first run, not a 1500x900 window in the middle of it.
    /// </remarks>
    public bool WindowMaximized { get; set; } = true;

    /// <summary>
    /// When this run of the app began. Fixed for the process lifetime.
    /// </summary>
    /// <remarks>
    /// Distinct from MainWindow's <c>_sessionStart</c>, which marks the start of a STUDY
    /// session for the pattern report and is deliberately reset when the student switches
    /// into Practice mode. Scoping chat threads to that would wipe the sidebar on a mode
    /// change, which is not what "reset on restart" means. Reading the clock inside
    /// <see cref="ToTutorOptions"/> would be just as wrong — settings reload mid-run and the
    /// cut-off would creep forward, resetting the chat while the student was using it.
    /// </remarks>
    public static readonly DateTimeOffset AppStarted = DateTimeOffset.UtcNow;

    // Model + provider are NOT forwarded here on purpose — TutorOptions.LiveModel/ChatModel
    // used to duplicate them, correctly kept in sync by this very method, and then sat unread
    // by anything: TutorClient takes model config from the LlmSettings this class also
    // produces (ToLlmSettings), a completely separate object nobody cross-checked against
    // this one. Two copies that happened to agree today were one missed edit away from
    // silently disagreeing tomorrow. LlmSettings is the one place model config now reaches
    // running code.
    public TutorOptions ToTutorOptions() => new()
    {
        SessionStart = AppStarted,
        LiveDebounce = TimeSpan.FromSeconds(LiveDebounceSeconds),
        MinLiveInterval = TimeSpan.FromSeconds(MinLiveIntervalSeconds),
        MaxLiveCallsPerHour = MaxLiveCallsPerHour,
        MonthlyCostCapUsd = MonthlyCostCapUsd,
        SnapshotRetentionPerPage = SnapshotRetentionPerPage,
        RevealAfterTurns = RevealAfterTurns,
        VisionEnabled = VisionEnabled,
    };
}
