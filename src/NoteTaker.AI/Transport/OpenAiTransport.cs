using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NoteTaker.Core.Tutor;

namespace NoteTaker.AI.Transport;

/// <summary>
/// Speaks the /chat/completions shape, which covers OpenAI itself, Gemini's
/// OpenAI-compatible endpoint, and most local or proxied gateways.
/// </summary>
public sealed class OpenAiTransport(
    HttpClient http,
    string endpoint,
    Func<string?> apiKeyProvider,
    string providerName,
    /// <summary>
    /// Whether this endpoint actually implements OpenAI's explicit-prompt-cache extension
    /// (prompt_cache_key / prompt_cache_options / prompt_cache_breakpoint). "OpenAI-compatible"
    /// only means the endpoint speaks the same /chat/completions shape — Gemini's compat shim
    /// rejects these as unknown fields with a 400 rather than silently ignoring them, so a
    /// caller asking for caching on a shim that doesn't support it must not send it at all.
    /// </summary>
    bool supportsExplicitCache = true,
    /// <summary>
    /// Reasoning effort to send alongside <c>max_tokens</c>, or null to send none.
    /// </summary>
    /// <remarks>
    /// Gemini 3.x thinks before it answers, and those hidden reasoning tokens are charged
    /// against <c>max_tokens</c> while being EXCLUDED from the reported
    /// <c>completion_tokens</c>. Measured against this install's key on gemini-3.6-flash,
    /// same prompt, varying only the cap: 100 → 4 visible tokens, 500 → 14, 1200 → the whole
    /// 115-token answer. So a nominally generous per-call budget silently became a
    /// few-word reply that stopped mid-sentence, and the usage log showed nothing wrong
    /// because the reasoning tokens it spent were never reported.
    ///
    /// "minimal" reclaims that budget and roughly halves latency (4202ms → 1443ms on
    /// gemini-3.6-flash; 1636ms → 822ms on gemini-3.1-flash-lite). Note the vocabulary is
    /// model-specific, not provider-wide: "none" 400s on every Gemini model tested, and
    /// "minimal" 400s on gemini-3.1-pro-preview while working on both Flash models.
    /// </remarks>
    string? reasoningEffort = null)
    : ILlmTransport
{
    /// <summary>
    /// Set false the first time this endpoint rejects a streaming request, so the fallback is
    /// paid once rather than on every turn. Not all OpenAI-shaped gateways implement
    /// <c>stream_options</c>, and some reject unknown fields outright instead of ignoring them
    /// — the same way Gemini's shim does for the explicit-cache fields.
    /// </summary>
    private volatile bool _streamingSupported = true;

    public async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken ct = default)
    {
        using var httpRequest = BuildHttpRequest(request, stream: false);

        using var response = await http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{providerName} returned {(int)response.StatusCode}: {Truncate(payload)}");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var hasChoice = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0;
        var text = hasChoice
            ? choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty
            : string.Empty;

        var finishReason = hasChoice
            && choices[0].TryGetProperty("finish_reason", out var finish)
                ? finish.GetString()
                : null;

        return new LlmResponse(WithCutOffMarker(text, finishReason), ReadUsage(root));
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

        // ResponseHeadersRead is what makes this a stream at all: the default
        // ResponseContentRead buffers the entire body before returning, which is exactly the
        // behaviour being replaced.
        using var response = await http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // A 4xx on a streaming request that would otherwise have worked almost certainly
            // means this endpoint does not accept "stream"/"stream_options". Fall back for
            // this call and stop trying afterwards, rather than failing the student's turn.
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                _streamingSupported = false;
                return await SendWholeAsOneDeltaAsync(request, onDelta, ct).ConfigureAwait(false);
            }

            throw new HttpRequestException(
                $"{providerName} returned {(int)response.StatusCode}: {Truncate(payload)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var text = new StringBuilder();
        var usage = TokenUsage.Zero;
        string? finishReason = null;

        // Read by LINE, not by chunk. An SSE frame is delimited by newlines and a single
        // frame's JSON routinely straddles two HTTP read buffers; StreamReader reassembles
        // that for us, which is the whole reason not to hand-roll the buffering here.
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue; // blank separator, or an SSE comment / non-data field
            }

            var data = line["data:".Length..].Trim();
            if (data is "[DONE]")
            {
                break;
            }

            JsonDocument frame;
            try
            {
                frame = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                continue; // keep-alive or partial garbage: skip the frame, keep the reply
            }

            using (frame)
            {
                var root = frame.RootElement;

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];

                    if (choice.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String
                        && content.GetString() is { Length: > 0 } piece)
                    {
                        text.Append(piece);
                        onDelta(piece);
                    }

                    if (choice.TryGetProperty("finish_reason", out var finish)
                        && finish.ValueKind == JsonValueKind.String)
                    {
                        finishReason = finish.GetString();
                    }
                }

                // Arrives in its own trailing frame (choices empty) when the endpoint honours
                // stream_options.include_usage. Absent on gateways that do not — hence Zero,
                // which the cost log reads as "unknown" rather than inventing a number.
                if (root.TryGetProperty("usage", out var usageElement)
                    && usageElement.ValueKind == JsonValueKind.Object)
                {
                    usage = ReadUsage(root);
                }
            }
        }

        // Appended after the stream, not mid-flight: the marker belongs at the end of the
        // finished text, and the caller has already rendered every delta by now.
        var whole = WithCutOffMarker(text.ToString(), finishReason);
        if (whole.Length > text.Length)
        {
            onDelta(whole[text.Length..]);
        }

        return new LlmResponse(whole, usage);
    }

    /// <summary>
    /// Non-streaming fallback that still honours the callback contract, so a caller rendering
    /// incrementally does not have to special-case a transport that cannot stream.
    /// </summary>
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

    private static string WithCutOffMarker(string text, string? finishReason) =>
        // Say so when the model was cut off mid-sentence rather than presenting half an answer
        // as a whole one. finish_reason was previously read by nobody, so a reply that ran into
        // the output-token limit simply stopped, and the only clue was that it made no sense.
        finishReason is "length"
            ? text.TrimEnd() + "\n\n[cut off — hit the reply length limit]"
            : text;

    private HttpRequestMessage BuildHttpRequest(LlmRequest request, bool stream)
    {
        var apiKey = apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new MissingApiKeyException(providerName);
        }

        var messages = new JsonArray();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(BuildMessage(
                "system",
                request.SystemPrompt,
                imagePng: null,
                endsCachePrefix: supportsExplicitCache && (request.CacheSystemPrompt || request.ExplicitPromptCache)));
        }

        foreach (var message in request.Messages)
        {
            messages.Add(BuildMessage(
                message.Role,
                message.Text,
                message.ImagePng,
                supportsExplicitCache && message.EndsCachePrefix));
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
        };

        // GPT-5 / o-series reject legacy max_tokens; they require max_completion_tokens,
        // which also covers hidden reasoning tokens. Gemini's OpenAI-compatible endpoint
        // and older OpenAI models still expect max_tokens.
        if (UsesMaxCompletionTokens(request.Model))
        {
            body["max_completion_tokens"] = request.MaxOutputTokens;
            body["reasoning_effort"] = "low";
        }
        else
        {
            body["max_tokens"] = request.MaxOutputTokens;

            // See the constructor remarks: without this, reasoning tokens eat the cap and the
            // student gets a sentence fragment.
            if (!string.IsNullOrWhiteSpace(reasoningEffort))
            {
                body["reasoning_effort"] = reasoningEffort;
            }
        }

        if (request.ExpectJson)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        if (supportsExplicitCache && !string.IsNullOrWhiteSpace(request.PromptCacheKey))
        {
            body["prompt_cache_key"] = request.PromptCacheKey;
        }

        if (supportsExplicitCache && request.ExplicitPromptCache)
        {
            body["prompt_cache_options"] = new JsonObject
            {
                ["mode"] = "explicit",
                ["ttl"] = "30m",
            };
        }

        if (stream)
        {
            body["stream"] = true;

            // Without this the usage block never arrives on a streamed reply, and chat — 82%
            // of measured spend — would silently stop being costed at all.
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return httpRequest;
    }

    private static JsonObject BuildMessage(
        string role,
        string text,
        byte[]? imagePng,
        bool endsCachePrefix)
    {
        var content = new JsonArray();

        if (imagePng is { Length: > 0 })
        {
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:image/png;base64,{Convert.ToBase64String(imagePng)}",
                },
            });
        }

        var textPart = new JsonObject
        {
            ["type"] = "text",
            ["text"] = text,
        };

        if (endsCachePrefix)
        {
            textPart["prompt_cache_breakpoint"] = new JsonObject { ["mode"] = "explicit" };
        }

        content.Add(textPart);

        return new JsonObject
        {
            ["role"] = role,
            ["content"] = content,
        };
    }

    private static TokenUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement))
        {
            return TokenUsage.Zero;
        }

        var tokensIn = usageElement.TryGetProperty("prompt_tokens", out var pin) ? pin.GetInt32() : 0;
        var tokensOut = usageElement.TryGetProperty("completion_tokens", out var pout) ? pout.GetInt32() : 0;

        // Thinking tokens are BILLED as output but left out of completion_tokens, and they
        // are not a rounding error: a measured reply reported completion_tokens 58 against
        // total_tokens 832 on a 73-token prompt — 701 tokens, twelve times the visible reply,
        // invisible to the cost estimate and therefore to the daily cap meant to stop
        // spending. total_tokens does include them, so the remainder after the prompt is the
        // honest output figure. Guarded with a max because a provider that reports usage
        // consistently must never be made worse by this.
        if (usageElement.TryGetProperty("total_tokens", out var totalEl))
        {
            tokensOut = Math.Max(tokensOut, totalEl.GetInt32() - tokensIn);
        }

        var cached = 0;
        var cacheWrite = 0;

        if (usageElement.TryGetProperty("prompt_tokens_details", out var details))
        {
            if (details.TryGetProperty("cached_tokens", out var cachedEl))
            {
                cached = cachedEl.GetInt32();
            }

            if (details.TryGetProperty("cache_write_tokens", out var writeEl))
            {
                cacheWrite = writeEl.GetInt32();
            }
        }

        // Some gateways nest cache writes under a sibling object.
        if (cacheWrite == 0 && usageElement.TryGetProperty("cache_write_tokens", out var topWrite))
        {
            cacheWrite = topWrite.GetInt32();
        }

        return new TokenUsage(tokensIn, tokensOut, cached, cacheWrite);
    }

    private static bool UsesMaxCompletionTokens(string model)
    {
        var id = model.Trim();
        return id.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400] + "…";
}
