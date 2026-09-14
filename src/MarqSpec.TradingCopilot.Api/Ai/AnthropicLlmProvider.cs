using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The real Anthropic-backed <see cref="ILlmProvider"/> (A2, gh#423) — one HTTPS POST to the Messages API per call,
/// behind the same provider-neutral seam the agent-review route already speaks (gh#402).
/// </summary>
/// <remarks>
/// <para>
/// A raw <see cref="HttpClient"/> adapter, deliberately not an SDK: the surface is one endpoint, the repo's licence
/// posture is permissive-only, and a hand-rolled client is trivially faked in a unit test with no network. It mirrors
/// <c>PushoverNotificationChannel</c> — <see cref="System.Text.Json"/>, the Options pattern, and <b>the API key on a
/// header, never in the request body and never in a log</b>.
/// </para>
/// <para>
/// <b>Fail-closed by construction.</b> It returns an <see cref="LlmCompletion"/> only for a well-formed 2xx answer
/// (mapping <c>stop_reason</c>, including a <see cref="LlmStopReason.Refusal"/>); anything else — a non-2xx status, a
/// transport fault, a timeout, or an unparseable body — <b>throws</b> (an <see cref="AnthropicLlmException"/>, or the
/// transport's own exception), which the reviewer maps to <c>Suppress(ReviewerUnavailable)</c>. It never fabricates a
/// completion, so a provider failure can never become a suggestion. A genuine caller cancellation propagates. Nothing
/// here reaches execution — enforcement lives below the model; this only fetches text.
/// </para>
/// </remarks>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    private const string MessagesUrl = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _client;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicLlmProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="client">The typed client; its timeout bounds how long a hung provider can hold the scan.</param>
    /// <param name="options">The provider configuration — the key and the per-tier model ids.</param>
    /// <param name="logger">The logger. The key and the response body are never written to it.</param>
    public AnthropicLlmProvider(HttpClient client, IOptions<LlmOptions> options, ILogger<AnthropicLlmProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string model = ModelFor(request.Tier);
        using StringContent body = new(BuildRequestBody(request, model, stream: false), Encoding.UTF8, "application/json");
        using HttpRequestMessage message = new(HttpMethod.Post, MessagesUrl) { Content = body };

        // The key rides a header -- never the request body, never a log. It is non-null in production: the real
        // provider is only bound when LlmOptions.IsConfigured.
        message.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

        using HttpResponseMessage response = await _client.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // FAIL-CLOSED: a non-2xx (auth, rate-limit, 5xx, ...) is not an answer. Log the status only -- no key, no body.
            _logger.LogWarning(
                "Anthropic returned {Status} for a {Tier} completion ({Model}); treating as unavailable.",
                (int)response.StatusCode,
                request.Tier,
                model);
            throw new AnthropicLlmException($"the model provider returned HTTP {(int)response.StatusCode}");
        }

        string payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return Parse(payload);
    }

    /// <inheritdoc />
    public async Task<LlmCompletion> StreamAsync(
        LlmRequest request, Func<string, CancellationToken, Task> onDelta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onDelta);

        string model = ModelFor(request.Tier);
        using StringContent body = new(BuildRequestBody(request, model, stream: true), Encoding.UTF8, "application/json");
        using HttpRequestMessage message = new(HttpMethod.Post, MessagesUrl) { Content = body };
        message.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

        // ResponseHeadersRead so we begin parsing the event stream as it arrives rather than after the whole body.
        using HttpResponseMessage response =
            await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // FAIL-CLOSED, exactly as CompleteAsync: a non-2xx is not an answer. Log the status only -- no key, no body.
            _logger.LogWarning(
                "Anthropic returned {Status} for a streamed {Tier} completion ({Model}); treating as unavailable.",
                (int)response.StatusCode,
                request.Tier,
                model);
            throw new AnthropicLlmException($"the model provider returned HTTP {(int)response.StatusCode}");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(stream, Encoding.UTF8);

        StringBuilder text = new();
        int inputTokens = 0;
        int outputTokens = 0;
        string? stopReason = null;

        try
        {
            // Server-Sent Events: only `data:` lines carry JSON, one complete object per line (the Messages API never
            // splits one across lines). We dispatch on the JSON's own `type` and ignore the `event:`, blank, and
            // comment lines; a malformed `data:` line fails closed at the parse rather than mis-parsing.
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                string data = line["data:".Length..].Trim();
                if (data.Length == 0)
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(data);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out JsonElement typeElement)
                    || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                switch (typeElement.GetString())
                {
                    case "message_start" when root.TryGetProperty("message", out JsonElement startMessage)
                        && startMessage.TryGetProperty("usage", out JsonElement startUsage):
                        inputTokens = ReadTokenCount(startUsage, "input_tokens");
                        outputTokens = ReadTokenCount(startUsage, "output_tokens"); // the initial count; overwritten below
                        break;

                    case "content_block_delta" when TryReadTextDelta(root, out string chunk):
                        text.Append(chunk);
                        await onDelta(chunk, cancellationToken);
                        break;

                    case "message_delta":
                        if (root.TryGetProperty("delta", out JsonElement messageDelta)
                            && messageDelta.TryGetProperty("stop_reason", out JsonElement sr)
                            && sr.ValueKind == JsonValueKind.String)
                        {
                            stopReason = sr.GetString();
                        }

                        if (root.TryGetProperty("usage", out JsonElement deltaUsage))
                        {
                            outputTokens = ReadTokenCount(deltaUsage, "output_tokens"); // cumulative final -- overwrite
                        }

                        break;

                    case "error":
                        // An in-stream error event is a provider fault, not an answer. Fail closed like a non-2xx.
                        _logger.LogWarning(
                            "Anthropic streamed an error event for a {Tier} completion ({Model}); treating as unavailable.",
                            request.Tier,
                            model);
                        throw new AnthropicLlmException("the model provider streamed an error event");

                    default:
                        break; // message_stop, content_block_start/stop, ping -- nothing to accumulate
                }
            }
        }
        catch (JsonException error)
        {
            // A `data:` line that is not the shape the streaming API promises is a protocol violation, not an answer.
            _logger.LogWarning(error, "Anthropic streamed a body that could not be parsed; treating as unavailable.");
            throw new AnthropicLlmException("the model provider streamed an unexpected body");
        }

        return new LlmCompletion(text.ToString(), MapStopReason(stopReason), new LlmUsage(inputTokens, outputTokens));
    }

    // The incremental text of a content_block_delta, when it is a non-empty text_delta; false for any other block delta.
    private static bool TryReadTextDelta(JsonElement root, out string chunk)
    {
        chunk = string.Empty;
        if (root.TryGetProperty("delta", out JsonElement delta)
            && delta.TryGetProperty("type", out JsonElement deltaType)
            && deltaType.ValueKind == JsonValueKind.String
            && deltaType.GetString() == "text_delta"
            && delta.TryGetProperty("text", out JsonElement deltaText)
            && deltaText.ValueKind == JsonValueKind.String
            && deltaText.GetString() is { Length: > 0 } text)
        {
            chunk = text;
            return true;
        }

        return false;
    }

    // One token count from a usage object, or 0 when absent -- defensive, the same posture as ExtractUsage.
    private static int ReadTokenCount(JsonElement usage, string property) =>
        usage.ValueKind == JsonValueKind.Object
        && usage.TryGetProperty(property, out JsonElement value)
        && value.TryGetInt32(out int count)
            ? count
            : 0;

    // Delegates to LlmOptions so the model the provider POSTs and the model the reviewer ledgers (gh#431) are, by
    // construction, the same string.
    private string ModelFor(LlmModelTier tier) => _options.ModelFor(tier);

    private static string BuildRequestBody(LlmRequest request, string model, bool stream)
    {
        JsonArray messages = [];
        foreach (LlmMessage message in request.Messages)
        {
            messages.Add(ToMessage(message));
        }

        JsonObject body = new()
        {
            ["model"] = model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["system"] = request.SystemPrompt,
            ["messages"] = messages,
        };

        if (stream)
        {
            body["stream"] = true; // ask for the incremental Server-Sent-Events response (gh#906 inc 3b)
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            // The read-only tools the model may call (gh#906 inc 4). Each schema embeds as an OBJECT (like the
            // output_config schema below) so the model reads it; an empty / null list POSTs no `tools` at all.
            JsonArray definitions = [];
            foreach (LlmToolDefinition tool in tools)
            {
                definitions.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(tool.InputSchema),
                });
            }

            body["tools"] = definitions;
        }

        if (request.ResponseFormat.JsonSchema is { } schema)
        {
            // Constrain the model to the caller's schema. Best-effort tightening only -- the caller still owns the
            // parse (the seam's contract), so a provider that ignores the constraint is caught fail-closed downstream.
            body["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["schema"] = JsonNode.Parse(schema),
                },
            };
        }

        return body.ToJsonString();
    }

    private static JsonObject ToMessage(LlmMessage message)
    {
        string role = message.Role == LlmRole.Assistant ? "assistant" : "user";

        // A plain turn carries its text as a string. A tool-use (assistant) or tool-result (user) turn round-trips a
        // content-block ARRAY (gh#906 inc 4) -- the Messages API accepts either shape for `content`.
        if (message.ToolCalls is not { Count: > 0 } && message.ToolResults is not { Count: > 0 })
        {
            return new JsonObject { ["role"] = role, ["content"] = message.Content };
        }

        JsonArray content = [];
        if (message.Content.Length > 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
        }

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            foreach (LlmToolCall call in calls)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    // The input embeds as an object the model reads back; a blank input is the no-arg `{}` object.
                    ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(call.InputJson) ? "{}" : call.InputJson),
                });
            }
        }

        if (message.ToolResults is { Count: > 0 } results)
        {
            foreach (LlmToolResult result in results)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = result.ToolCallId,
                    ["content"] = result.Content,
                    ["is_error"] = result.IsError,
                });
            }
        }

        return new JsonObject { ["role"] = role, ["content"] = content };
    }

    private LlmCompletion Parse(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            // A syntactically-valid but non-object 2xx body (a scalar, an array, an error envelope) is not an answer.
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AnthropicLlmException("the model provider returned a non-object body");
            }

            string text = ExtractText(root);
            string? stopReason = root.TryGetProperty("stop_reason", out JsonElement sr) && sr.ValueKind == JsonValueKind.String
                ? sr.GetString()
                : null;
            return new LlmCompletion(text, MapStopReason(stopReason), ExtractUsage(root), ExtractToolCalls(root));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            // A 2xx whose body is not the shape the Messages API promises is a protocol violation, not an answer. The
            // broad filter also nets any JsonElement accessor that throws on an unexpected shape -- fail closed, total.
            _logger.LogWarning(error, "Anthropic returned a body that could not be parsed; treating as unavailable.");
            throw new AnthropicLlmException("the model provider returned an unexpected body");
        }
    }

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "text"
                && block.TryGetProperty("text", out JsonElement blockText)
                && blockText.ValueKind == JsonValueKind.String)
            {
                builder.Append(blockText.GetString());
            }
        }

        return builder.ToString();
    }

    // The tool_use content blocks the model emitted, or null when it asked for none (gh#906 inc 4). The input is kept
    // as its raw JSON -- the caller (the tool loop) parses it, exactly as the completion Text is the caller's to parse.
    // A malformed block is skipped, never crashing an accessor (the same total-defensive posture as ExtractText).
    private static IReadOnlyList<LlmToolCall>? ExtractToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<LlmToolCall>? calls = null;
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "tool_use"
                && block.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && block.TryGetProperty("name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String)
            {
                // GetRawText preserves the input object exactly; an absent input is the no-arg `{}`.
                string inputJson = block.TryGetProperty("input", out JsonElement input) ? input.GetRawText() : "{}";
                (calls ??= []).Add(new LlmToolCall(id.GetString()!, name.GetString()!, inputJson));
            }
        }

        return calls;
    }

    private static LlmStopReason MapStopReason(string? stopReason) => stopReason switch
    {
        "end_turn" or "stop_sequence" => LlmStopReason.Completed,
        "max_tokens" => LlmStopReason.MaxTokens,
        "refusal" => LlmStopReason.Refusal,
        "tool_use" => LlmStopReason.ToolUse, // the model wants a tool -- the caller runs it and continues (gh#906 inc 4)
        _ => LlmStopReason.Other, // pause_turn, absent, anything unknown -- the caller fails closed
    };

    private static LlmUsage ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return LlmUsage.None;
        }

        int input = usage.TryGetProperty("input_tokens", out JsonElement i) && i.TryGetInt32(out int inputTokens)
            ? inputTokens
            : 0;
        int output = usage.TryGetProperty("output_tokens", out JsonElement o) && o.TryGetInt32(out int outputTokens)
            ? outputTokens
            : 0;
        return new LlmUsage(input, output);
    }
}
