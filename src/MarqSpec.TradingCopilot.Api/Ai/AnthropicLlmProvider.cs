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
        using StringContent body = new(BuildRequestBody(request, model), Encoding.UTF8, "application/json");
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

    private string ModelFor(LlmModelTier tier) => tier switch
    {
        LlmModelTier.Triage => _options.TriageModel,
        LlmModelTier.Deep => _options.DeepModel,
        _ => _options.TriageModel, // an undeclared tier triages -- the cheap, safe default
    };

    private static string BuildRequestBody(LlmRequest request, string model)
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

    private static JsonObject ToMessage(LlmMessage message) => new()
    {
        ["role"] = message.Role == LlmRole.Assistant ? "assistant" : "user",
        ["content"] = message.Content,
    };

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
            return new LlmCompletion(text, MapStopReason(stopReason), ExtractUsage(root));
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

    private static LlmStopReason MapStopReason(string? stopReason) => stopReason switch
    {
        "end_turn" or "stop_sequence" => LlmStopReason.Completed,
        "max_tokens" => LlmStopReason.MaxTokens,
        "refusal" => LlmStopReason.Refusal,
        _ => LlmStopReason.Other, // tool_use, pause_turn, absent, anything unknown -- the caller fails closed
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
