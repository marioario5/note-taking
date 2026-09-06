using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NoteTaker.Core.Tutor;

namespace NoteTaker.AI.Transport;

/// <summary>Anthropic Messages API, used when chat is pointed at Claude.</summary>
public sealed class AnthropicTransport(HttpClient http, string endpoint, Func<string?> apiKeyProvider)
    : ILlmTransport
{
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>
    /// Not implemented as a real stream: this transport is the secondary path (chat defaults
    /// to Gemini), and Anthropic's SSE shape is a different protocol again — its own event
    /// types and delta envelope, not the OpenAI one. Delivering the finished reply as a single
    /// delta keeps the callback contract honest for callers that render incrementally; they
    /// simply see one large first token instead of many small ones. Worth building out only
    /// if Claude becomes a chat default.
    /// </summary>
    public async Task<LlmResponse> StreamAsync(
        LlmRequest request,
        Action<string> onDelta,
        CancellationToken ct = default)
    {
        var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(response.Content))
        {
            onDelta(response.Content);
        }

        return response;
    }

    public async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken ct = default)
    {
        var apiKey = apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new MissingApiKeyException("Anthropic");
        }

        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var content = new JsonArray();

            // Anthropic reads images better when they precede the instruction text.
            if (message.ImagePng is { Length: > 0 })
            {
                var imageBlock = new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = Convert.ToBase64String(message.ImagePng),
                    },
                };

                if (message.EndsCachePrefix)
                {
                    imageBlock["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                }

                content.Add(imageBlock);
            }

            var textBlock = new JsonObject { ["type"] = "text", ["text"] = message.Text };
            if (message.EndsCachePrefix)
            {
                textBlock["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            }

            content.Add(textBlock);

            messages.Add(new JsonObject
            {
                ["role"] = message.Role == "assistant" ? "assistant" : "user",
                ["content"] = content,
            });
        }

        JsonNode systemNode;
        if (request.CacheSystemPrompt || request.ExplicitPromptCache)
        {
            systemNode = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = request.SystemPrompt,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                },
            };
        }
        else
        {
            systemNode = JsonValue.Create(request.SystemPrompt)!;
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["system"] = systemNode,
            ["messages"] = messages,
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint.TrimEnd('/')}/messages")
        {
            Content = JsonContent.Create(body),
        };
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", AnthropicVersion);

        using var response = await http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Anthropic returned {(int)response.StatusCode}: {Truncate(payload)}");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var builder = new StringBuilder();
        if (root.TryGetProperty("content", out var blocks))
        {
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) &&
                    type.GetString() == "text" &&
                    block.TryGetProperty("text", out var text))
                {
                    builder.Append(text.GetString());
                }
            }
        }

        var usage = TokenUsage.Zero;
        if (root.TryGetProperty("usage", out var usageElement))
        {
            var tokensIn = usageElement.TryGetProperty("input_tokens", out var pin) ? pin.GetInt32() : 0;
            var tokensOut = usageElement.TryGetProperty("output_tokens", out var pout) ? pout.GetInt32() : 0;
            var cached = usageElement.TryGetProperty("cache_read_input_tokens", out var cr)
                ? cr.GetInt32()
                : 0;
            var written = usageElement.TryGetProperty("cache_creation_input_tokens", out var cw)
                ? cw.GetInt32()
                : 0;
            usage = new TokenUsage(tokensIn, tokensOut, cached, written);
        }

        return new LlmResponse(builder.ToString(), usage);
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400] + "…";
}
