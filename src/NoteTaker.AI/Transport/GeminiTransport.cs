using System.IO;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NoteTaker.Core.Tutor;

namespace NoteTaker.AI.Transport;

/// <summary>
/// Speaks Gemini's own <c>generateContent</c> API rather than its OpenAI-compatible shim.
/// </summary>
/// <remarks>
/// The shim is simpler and served this app fine, but it cannot express the one field that
/// materially reduces cost here. Chat sends a page image every turn; that image is ~1,115 of
/// ~2,852 input tokens, and Gemini bills it by a flat per-level allocation rather than by
/// pixels — so no amount of rendering it smaller helps (measured: 1.00 and 0.75 scale cost
/// exactly the same). <c>mediaResolution</c> is the only dial that moves it, and the shim
/// rejects it in every spelling, including <c>extra_body.google</c>:
///
///   top-level media_resolution          400 Unknown name "media_resolution"
///   generation_config.media_resolution  400 Unknown name "generation_config"
///   extra_body.google.media_resolution  400 Unknown name "media_resolution"
///
/// Hence a second transport rather than a flag. Two secondary wins come with it: the native
/// API reports <c>thoughtsTokenCount</c> explicitly, which retires the
/// <c>total_tokens - prompt_tokens</c> subtraction <see cref="OpenAiTransport"/> needs to
/// recover thinking tokens the shim hides; and <c>promptTokensDetails</c> breaks input down by
/// modality, so image cost is separable from text cost in the usage log.
///
/// <see cref="OpenAiTransport"/> stays for OpenAI and other compatible gateways. This is an
/// addition, not a replacement.
/// </remarks>
public sealed class GeminiTransport(
    HttpClient http,
    string endpoint,
    Func<string?> apiKeyProvider,
    /// <summary>Thinking level, or null to leave the model's default alone.</summary>
    /// <remarks>
    /// Labels only. Numeric thinking budgets are deprecated on Gemini 3. "LOW" is the working
    /// setting here — measured, the level below it spends zero thinking tokens and answered
    /// "NO" to a correct integral, inventing an error in a student's right answer.
    /// </remarks>
    string? thinkingLevel = "LOW")
    : ILlmTransport
{
    private const string ProviderName = "Gemini";

    /// <summary>Mirrors <see cref="OpenAiTransport"/>: pay a failed stream once, not per turn.</summary>
    private volatile bool _streamingSupported = true;

    public async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken ct = default)
    {
        using var httpRequest = BuildHttpRequest(request, stream: false);

        using var response = await http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{ProviderName} returned {(int)response.StatusCode}: {Truncate(payload)}");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var text = new StringBuilder();
        string? finishReason = null;

        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            AppendText(candidates[0], text);
            finishReason = ReadFinishReason(candidates[0]);
        }

        return new LlmResponse(WithCutOffMarker(text.ToString(), finishReason), ReadUsage(root));
    }

    public async Task<LlmResponse> StreamAsync(
        LlmRequest request,
        Action<string> onDelta,
        CancellationToken ct = default)
    {
        if (!_streamingSupported)
        {
            return await SendWholeAsOneDeltaAsync(request, onDelta, ct).ConfigureAwait(false);
        }

        using var httpRequest = BuildHttpRequest(request, stream: true);

        using var response = await http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                _streamingSupported = false;
                return await SendWholeAsOneDeltaAsync(request, onDelta, ct).ConfigureAwait(false);
            }

            throw new HttpRequestException(
                $"{ProviderName} returned {(int)response.StatusCode}: {Truncate(payload)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var text = new StringBuilder();
        var usage = TokenUsage.Zero;
        string? finishReason = null;

        // By LINE, for the same reason as the OpenAI transport: a frame's JSON routinely
        // straddles two HTTP reads and StreamReader reassembles it. What differs is the frame
        // CONTENTS — each one is a bare GenerateContentResponse, not an OpenAI delta chunk, and
        // there is NO [DONE] sentinel; the stream simply ends.
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            JsonDocument frame;
            try
            {
                frame = JsonDocument.Parse(line["data:".Length..].Trim());
            }
            catch (JsonException)
            {
                continue; // keep-alive or partial garbage: skip the frame, keep the reply
            }

            using (frame)
            {
                var root = frame.RootElement;

                if (root.TryGetProperty("candidates", out var candidates)
                    && candidates.GetArrayLength() > 0)
                {
                    var before = text.Length;
                    AppendText(candidates[0], text);

                    if (text.Length > before)
                    {
                        onDelta(text.ToString(before, text.Length - before));
                    }

                    finishReason = ReadFinishReason(candidates[0]) ?? finishReason;
                }

                // Unlike the shim, usage rides along on frames rather than only a trailing one,
                // and the last one seen is the complete tally — so overwrite rather than
                // keeping the first.
                if (root.TryGetProperty("usageMetadata", out var meta)
                    && meta.ValueKind == JsonValueKind.Object)
                {
                    usage = ReadUsage(root);
                }
            }
        }

        var whole = WithCutOffMarker(text.ToString(), finishReason);
        if (whole.Length > text.Length)
        {
            onDelta(whole[text.Length..]);
        }

        return new LlmResponse(whole, usage);
    }

    private async Task<LlmResponse> SendWholeAsOneDeltaAsync(
        LlmRequest request,
        Action<string> onDelta,
        CancellationToken ct)
    {
        var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(response.Content))
        {
            onDelta(response.Content);
        }

        return response;
    }

    /// <summary>Concatenates every text part, skipping any the model marked as its thoughts.</summary>
    private static void AppendText(JsonElement candidate, StringBuilder into)
    {
        if (!candidate.TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in parts.EnumerateArray())
        {
            // A part flagged "thought" is the model's internal reasoning. It is billed, but it
            // is not the reply — rendering it would put the deliberation the Socratic prompt
            // explicitly forbids straight into the student's transcript.
            if (part.TryGetProperty("thought", out var thought)
                && thought.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (part.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String
                && textElement.GetString() is { Length: > 0 } piece)
            {
                into.Append(piece);
            }
        }
    }

    private static string? ReadFinishReason(JsonElement candidate) =>
        candidate.TryGetProperty("finishReason", out var finish)
        && finish.ValueKind == JsonValueKind.String
            ? finish.GetString()
            : null;

    private static string WithCutOffMarker(string text, string? finishReason) =>
        // Gemini says MAX_TOKENS where OpenAI says "length"; same situation, same honesty.
        string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase)
            ? text.TrimEnd() + "\n\n[cut off — hit the reply length limit]"
            : text;

    private HttpRequestMessage BuildHttpRequest(LlmRequest request, bool stream)
    {
        var apiKey = apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new MissingApiKeyException(ProviderName);
        }

        var contents = new JsonArray();
        foreach (var message in request.Messages)
        {
            contents.Add(BuildContent(message));
        }

        var generationConfig = new JsonObject
        {
            ["maxOutputTokens"] = request.MaxOutputTokens,

            // The reason this transport exists. Flat per-level allocation, not pixel-derived:
            // High 1,115 / Medium 551 / Low 275 tokens on a full page capture.
            ["mediaResolution"] = MediaResolution(request.ImageDetail),
        };

        var level = request.ThinkingLevel ?? thinkingLevel;
        if (!string.IsNullOrWhiteSpace(level))
        {
            generationConfig["thinkingConfig"] = new JsonObject
            {
                ["thinkingLevel"] = level,
            };
        }

        if (request.ExpectJson)
        {
            generationConfig["responseMimeType"] = "application/json";
        }

        var body = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = generationConfig,
        };

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = request.SystemPrompt } },
            };
        }

        var verb = stream ? "streamGenerateContent?alt=sse" : "generateContent";
        var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint.TrimEnd('/')}/models/{request.Model}:{verb}")
        {
            Content = JsonContent.Create(body),
        };

        // Header rather than a ?key= query parameter: a URL carrying the key would end up in
        // logs and exception messages.
        httpRequest.Headers.Add("x-goog-api-key", apiKey);
        return httpRequest;
    }

    private static string MediaResolution(ImageDetail detail) => detail switch
    {
        ImageDetail.Low => "MEDIA_RESOLUTION_LOW",
        ImageDetail.Medium => "MEDIA_RESOLUTION_MEDIUM",
        _ => "MEDIA_RESOLUTION_HIGH",
    };

    private static JsonObject BuildContent(LlmMessage message)
    {
        var parts = new JsonArray();

        if (message.ImagePng is { Length: > 0 } png)
        {
            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject
                {
                    ["mime_type"] = "image/png",
                    ["data"] = Convert.ToBase64String(png),
                },
            });
        }

        parts.Add(new JsonObject { ["text"] = message.Text });

        return new JsonObject
        {
            // Gemini's assistant role is "model". Sending "assistant" is a 400, and it is the
            // single easiest thing to get wrong when porting an OpenAI-shaped message list.
            ["role"] = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "model"
                : "user",
            ["parts"] = parts,
        };
    }

    /// <summary>
    /// Reads <c>usageMetadata</c>, counting thinking tokens as the billed output they are.
    /// </summary>
    /// <remarks>
    /// <c>candidatesTokenCount</c> excludes thinking, and thinking is charged at the output
    /// rate — on a measured call, 94 thinking tokens against 16 visible ones. Billing only the
    /// visible reply would let the daily cap see a fraction of real spend. Native states the
    /// figure outright in <c>thoughtsTokenCount</c>, which is strictly better than the
    /// subtraction the OpenAI transport has to do to recover it.
    /// </remarks>
    private static TokenUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage))
        {
            return TokenUsage.Zero;
        }

        static int Read(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : 0;

        var tokensIn = Read(usage, "promptTokenCount");
        var tokensOut = Read(usage, "candidatesTokenCount") + Read(usage, "thoughtsTokenCount");

        return new TokenUsage(tokensIn, tokensOut, Read(usage, "cachedContentTokenCount"), 0);
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "…";
}
