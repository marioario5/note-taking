using System.Text;
using NoteTaker.AI.Transport;
using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.AI;

/// <summary>
/// Maps tutor intents onto the two transports: a cheap vision model for spotting
/// mistakes, and a chat model for explaining them. Chat threads cache a page setup
/// prefix so follow-ups reuse it.
/// </summary>
public sealed class TutorClient(
    ILlmTransport visionTransport,
    ILlmTransport chatTransport,
    LlmSettings settings,
    TutorOptions options) : ITutorClient
{
    public async Task<TutorScanResult> ScanPageAsync(TutorScanRequest request, CancellationToken ct = default)
    {
        // System prompt stays a byte-identical prefix so Gemini's implicit caching can hit.
        // Focus / page context go only in the user text after the image.
        var systemPrompt = request.Mode == TutorMode.Review
            ? PromptLibrary.ReviewScan
            : PromptLibrary.LiveScan;
        var callType = request.Mode == TutorMode.Review
            ? TutorCallType.ReviewScan
            : TutorCallType.LiveCheck;

        var instruction = new StringBuilder("Here is the current page. Check the work.");
        if (request.FocusRegion is { } focus && !focus.IsEmpty)
        {
            instruction.Append(
                $" The student just wrote near x={focus.X:0.00}–{focus.X + focus.Width:0.00}, " +
                $"y={focus.Y:0.00}–{focus.Y + focus.Height:0.00}. " +
                "ONLY report mistakes that intersect that recent writing (or are clearly caused by it). " +
                "Do NOT re-flag older settled work elsewhere on the page.");
        }

        if (!string.IsNullOrWhiteSpace(request.PageContext))
        {
            instruction.Append(" Context: ").Append(request.PageContext);
        }

        var response = await visionTransport.SendAsync(
            new LlmRequest(
                settings.VisionModel,
                systemPrompt,
                [new LlmMessage("user", instruction.ToString(), request.PagePng)],
                settings.MaxOutputTokensFor(callType),
                ExpectJson: true),
            ct).ConfigureAwait(false);

        var (findings, summary) = ScanResponseParser.Parse(response.Content);
        return new TutorScanResult(findings, summary, response.Usage, settings.VisionModel);
    }

    public async Task<TutorChatResult> ContinueThreadAsync(
        TutorChatRequest request,
        Action<string>? onDelta = null,
        CancellationToken ct = default)
    {
        // Keep the system string stable across turns so the cache prefix never shifts.
        var systemPrompt = PromptLibrary.Socratic;

        var messages = new List<LlmMessage>();

        // See ChatHistoryWindow: resending the whole thread every turn was 82% of measured
        // spend, almost all of it input tokens. Trimmed in blocks, not one message at a time,
        // so the cache breakpoint set below still lands on an unchanged prefix between blocks.
        foreach (var message in ChatHistoryWindow.Select(request.History))
        {
            if (message.Role == MessageRole.System)
            {
                continue;
            }

            messages.Add(new LlmMessage(
                message.Role == MessageRole.Assistant ? "assistant" : "user",
                message.Content));
        }

        var question = request.UserMessage;
        if (request.Socratic)
        {
            // This rider is appended to the newest user message, which makes it the least
            // cached and most salient text in the request — so it has to agree with the
            // system prompt rather than quietly override it. Two earlier versions did
            // override it: one asserted the student "is genuinely stuck" purely because a
            // message counter had passed a threshold, and one demanded every reply "end with
            // exactly one question", which turned confirming correct work into an
            // interrogation. The count gates the reveal. It is not evidence about the
            // student, and it is not a ladder position.
            if (request.UserTurnCount > options.RevealAfterTurns)
            {
                question =
                    $"{request.UserMessage}\n\n(The reveal lock is lifted — {request.UserTurnCount} messages this session. " +
                    "You MAY now show the corrected step and explain why the original failed, but only if the student has " +
                    "genuinely attempted the step that is wrong. Messages spent answering your questions correctly are not " +
                    "attempts at it; if they have not tried it yet, stay on the hint ladder.)";
            }
            else
            {
                question =
                    $"{request.UserMessage}\n\n(Reveal is NOT allowed in this reply. Forbidden: any correct numerical result, " +
                    "any corrected equation, 'it should be', 'the answer is', 'gives N not M', 'you meant', or rewriting " +
                    "their line correctly. If the work is right, say so plainly and stop — that is not a reveal. Otherwise " +
                    "aim at the FIRST step that is actually wrong, and do not quiz them on steps they already got right. " +
                    "Do not say the image is faint.)";
            }

            // No picture rides with this turn because the page is byte-identical to the one
            // already sent. Said plainly, or the model announces it cannot see the work and
            // asks the student to describe what is already on its screen.
            if (request.PageUnchanged)
            {
                question += "\n\n(No new image: the page is unchanged since the last one you"
                    + " were sent. Work from that picture — it is still current.)";
            }
        }

        // The system prompt already gets a cache breakpoint below, but until now the
        // conversation history didn't — every turn resent every prior turn's text from
        // scratch even though it's byte-identical to what the previous call already sent.
        // Marking the last historical message as the end of the cached prefix means only
        // the fresh question (+ fresh image) below is ever new; everything up through the
        // last completed exchange is served from cache on turn 2 onward.
        if (messages.Count > 0)
        {
            messages[^1] = messages[^1] with { EndsCachePrefix = true };
        }

        // Every turn carries its own image and its own framing — a crop of whichever finding
        // is currently under discussion, or a readable crop of the whole page when nothing is
        // flagged. Saying so explicitly, every time, is what keeps the model from answering
        // about a mistake the student has already moved past.
        var image = request.RegionCropPng is { Length: > 0 } ? request.RegionCropPng : null;
        var framing = !string.IsNullOrWhiteSpace(request.FeedbackLabel)
            ? $"[An earlier automated check flagged this as: {request.FeedbackLabel} — " +
              "unverified, re-read the image yourself, sign included] "
            : image is not null
                ? "[Nothing is currently flagged on this page — this is the whole page.] "
                : string.Empty;

        messages.Add(new LlmMessage("user", framing + question, image));

        var llmRequest = new LlmRequest(
            settings.ChatModel,
            systemPrompt,
            messages,
            settings.MaxOutputTokensFor(TutorCallType.SocraticChat),
            PromptCacheKey: $"notetaker-thread-{request.ThreadId}",
            ExplicitPromptCache: true,
            CacheSystemPrompt: true,
            ImageDetail: settings.ChatImageDetail);

        // The verdict tag arrives in the closing frames, and deltas are the only thing rendered
        // live — so it has to be withheld here rather than cleaned up afterwards, or the student
        // watches it type itself out and then disappear. Gating on the opening bracket alone is
        // enough: everything after it is machine text, whether or not it turns out to parse, and
        // the tag can straddle frames so no single delta can be inspected in isolation.
        Action<string>? gated = null;
        if (onDelta is not null)
        {
            var seen = new StringBuilder();
            var shown = 0;
            gated = piece =>
            {
                seen.Append(piece);
                var cut = IndexOfTagStart(seen);
                var visible = cut < 0 ? seen.Length : cut;
                if (visible > shown)
                {
                    onDelta(seen.ToString(shown, visible - shown));
                    shown = visible;
                }
            };
        }

        // Chat is the only call type worth streaming: it is the one a student sits and waits
        // on. Scans parse JSON as a whole and would gain nothing from partial frames.
        var response = onDelta is null
            ? await chatTransport.SendAsync(llmRequest, ct).ConfigureAwait(false)
            : await chatTransport.StreamAsync(llmRequest, gated!, ct).ConfigureAwait(false);

        // Strip here, not at the UI: this return value is both what gets rendered AND what is
        // persisted and resent as history on every later turn, so a tag left in it would be
        // paid for again on each one — the exact cost this feature exists to respect.
        var (content, verdict) = SkillVerdictParser.Parse(response.Content ?? string.Empty);
        return new TutorChatResult(content, response.Usage, settings.ChatModel, verdict);
    }

    /// <summary>
    /// Marks one attempt from the page alone: no thread, no history, no reply to read.
    /// </summary>
    /// <remarks>
    /// The cheapest call the app makes. Everything that makes a chat turn expensive — the
    /// conversation resent each turn, the tutoring prompt, room for a paragraph of reply — is
    /// absent, leaving the image, a short prompt, and one tag line out. It exists because
    /// practising without wanting help is a normal way to work, and doing that through the chat
    /// meant paying for a lesson nobody read to get a data point Review could use.
    /// </remarks>
    public async Task<TutorChatResult> CheckSkillAsync(byte[]? pagePng, CancellationToken ct = default)
    {
        var response = await chatTransport.SendAsync(
            new LlmRequest(
                settings.ChatModel,
                PromptLibrary.SkillCheck,
                [new LlmMessage("user", "Mark this work.", pagePng)],
                settings.MaxOutputTokensFor(TutorCallType.SkillCheck)),
            ct).ConfigureAwait(false);

        // The whole reply is the tag, so what the parser strips out is discarded — but it is the
        // same parser the chat uses, which keeps one definition of the tag format.
        var (_, verdict) = SkillVerdictParser.Parse(response.Content ?? string.Empty);
        return new TutorChatResult(string.Empty, response.Usage, settings.ChatModel, verdict);
    }

    /// <summary>Index of the verdict tag's opening bracket, or -1.</summary>
    private static int IndexOfTagStart(StringBuilder text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '⟦')
            {
                return i;
            }
        }

        return -1;
    }

    public async Task<TutorChatResult> SummarizePatternsAsync(
        IReadOnlyList<TutorFeedback> feedback,
        CancellationToken ct = default)
    {
        var builder = new StringBuilder("Mistakes flagged during this session:\n");
        foreach (var item in feedback)
        {
            builder.Append("- [").Append(item.Severity).Append("] ").AppendLine(item.Label);
        }

        builder.Append(
            "\nSummarize the recurring patterns in plain language a student can act on. " +
            "No LaTeX. Keep it under 120 words.");

        var response = await chatTransport.SendAsync(
            new LlmRequest(
                settings.ChatModel,
                PromptLibrary.PatternSummary,
                [new LlmMessage("user", builder.ToString())],
                settings.MaxOutputTokensFor(TutorCallType.PatternSummary),
                // Fixed (not per-thread) key: this system prompt never changes call to
                // call, so one cache entry serves every pattern-summary call for the app's
                // whole lifetime — this call type simply never asked for the caching its
                // own transport already supports.
                PromptCacheKey: "notetaker-pattern-summary",
                ExplicitPromptCache: true,
                CacheSystemPrompt: true),
            ct).ConfigureAwait(false);

        return new TutorChatResult(response.Content ?? string.Empty, response.Usage, settings.ChatModel);
    }

    public async Task<TutorChatResult> GeneratePracticeAsync(
        PracticeGenerationRequest request,
        CancellationToken ct = default)
    {
        var builder = new StringBuilder()
            .Append("Source: ").AppendLine(request.SourceName)
            .Append("Write ").Append(request.QuestionCount)
            .AppendLine(" practice questions in the same style.")
            .AppendLine("No solutions — questions only.");

        if (!string.IsNullOrWhiteSpace(request.ExtraInstructions))
        {
            builder.AppendLine(request.ExtraInstructions);
        }

        var messages = new List<LlmMessage>();
        foreach (var image in request.PageImages)
        {
            messages.Add(new LlmMessage("user", "Source page image:", image));
        }

        messages.Add(new LlmMessage("user", builder.ToString()));

        var response = await chatTransport.SendAsync(
            new LlmRequest(
                settings.ChatModel,
                PromptLibrary.PracticeGeneration,
                messages,
                settings.MaxOutputTokensFor(TutorCallType.PracticeGeneration),
                PromptCacheKey: "notetaker-practice-generation",
                ExplicitPromptCache: true,
                CacheSystemPrompt: true),
            ct).ConfigureAwait(false);

        return new TutorChatResult(response.Content ?? string.Empty, response.Usage, settings.ChatModel);
    }

    public async Task<TutorChatResult> GenerateSkillReportAsync(
        string topicName,
        TopicConfidence topic,
        CancellationToken ct = default)
    {
        // Aggregates only. The conversation those numbers came from is thousands of tokens and
        // adds nothing the counts do not already carry — sending it would make the report the
        // most expensive call in the app instead of one of the cheapest.
        var builder = new StringBuilder()
            .Append("Topic: ").AppendLine(topicName)
            .Append("Problems worked: ").Append(topic.TotalAttempts)
            .AppendLine(" across " + topic.Skills.Count + " skills")
            .AppendLine()
            .AppendLine("skill | attempts | corrections | independence | trend | last worked");

        foreach (var skill in topic.Skills)
        {
            builder
                .Append(skill.Skill).Append(" | ")
                .Append(skill.Attempts).Append(" | ")
                .Append(skill.Corrections).Append(" | ")
                .Append(skill.Confidence.ToString("0.00")).Append(" | ")
                .Append(skill.Trend.ToString().ToLowerInvariant()).Append(" | ")
                .Append(skill.DaysSinceLastPractised).AppendLine(" days ago");
        }

        var response = await chatTransport.SendAsync(
            new LlmRequest(
                settings.ChatModel,
                PromptLibrary.SkillReport,
                [new LlmMessage("user", builder.ToString())],
                settings.MaxOutputTokensFor(TutorCallType.SkillReport),
                // Fixed key: this system prompt is identical on every report for every topic, so
                // one cache entry serves them all.
                PromptCacheKey: "notetaker-skill-report",
                ExplicitPromptCache: true,
                CacheSystemPrompt: true,

                // The one call that reasons over nothing. Every number in the report was
                // computed by SkillConfidence before the request was built, so there is no
                // judgement to check and no working to redo — the model is reading figures back
                // as prose. Measured, thinking was about three quarters of what this call cost.
                ThinkingLevel: "MINIMAL"),
            ct).ConfigureAwait(false);

        return new TutorChatResult(response.Content ?? string.Empty, response.Usage, settings.ChatModel);
    }

    public async Task<TutorChatResult> GenerateWeaknessReviewAsync(
        IReadOnlyList<TutorFeedback> findings,
        IReadOnlyList<WeaknessTopic> recurringTopics,
        CancellationToken ct = default)
    {
        var builder = new StringBuilder("Mistakes just found on this page:\n");
        foreach (var finding in findings)
        {
            builder.Append("- [").Append(finding.Severity).Append("] ").AppendLine(finding.Label);
        }

        if (recurringTopics.Count > 0)
        {
            builder.AppendLine().AppendLine("Recurring patterns (seen before on this page):");
            foreach (var topic in recurringTopics)
            {
                builder.Append("- ").Append(topic.Topic)
                    .Append(" (×").Append(topic.Count)
                    .Append(", ").Append(topic.MaxSeverity)
                    .Append(", most recent: ").Append(topic.SampleLabel).AppendLine(")");
            }
        }

        var response = await chatTransport.SendAsync(
            new LlmRequest(
                settings.ChatModel,
                PromptLibrary.WeaknessReview,
                [new LlmMessage("user", builder.ToString())],
                settings.MaxOutputTokensFor(TutorCallType.WeaknessReview),
                PromptCacheKey: "notetaker-weakness-review",
                ExplicitPromptCache: true,
                CacheSystemPrompt: true),
            ct).ConfigureAwait(false);

        return new TutorChatResult(response.Content ?? string.Empty, response.Usage, settings.ChatModel);
    }
}
