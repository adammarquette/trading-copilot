namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>
/// One LLM call (R-4 / ADR-0008) — the provider-neutral seam through which the co-pilot invokes a model at the
/// edges (never in the continuous scan loop). Anthropic sits behind it in v1; nothing here names a provider.
/// </summary>
/// <remarks>
/// Text in, text out. Structured output is <i>requested</i> via <see cref="LlmResponseFormat"/> but <i>parsed</i>
/// by the caller, so serialization never leaks into the seam and a caller can unit-test its own parse behind a
/// fake provider. <see cref="LlmUsage"/> and <see cref="LlmStopReason"/> ride on the completion from the start, so
/// per-invocation cost/latency tracking (AIUsage) and fail-closed refusal handling are a fill-in for the real
/// client, never a reshape of this seam. Like <c>INotificationChannel</c> / <c>IIndicatorSource</c>, the seam is
/// domain-level and the concrete adapter (the Anthropic HTTP client) lands later in the composition root.
/// </remarks>
public interface ILlmProvider
{
    /// <summary>Runs one completion.</summary>
    /// <param name="request">The prompt, model tier, and desired response format.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The completion text, why the model stopped, and token usage.</returns>
    Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>The model tier for a call (ADR-0008): cheap triage vs. deep synthesis. Both tiers are live (gh#449).</summary>
public enum LlmModelTier
{
    /// <summary>A cheaper model for "is this worth surfacing?" triage; it may escalate a genuinely hard setup to <see cref="Deep"/>.</summary>
    Triage = 1,

    /// <summary>The top model for genuinely hard synthesis — the tier a triage escalation calls for a final answer (gh#449).</summary>
    Deep = 2,
}

/// <summary>Who authored a message in the conversation handed to the model.</summary>
public enum LlmRole
{
    /// <summary>The operator / caller turn.</summary>
    User = 1,

    /// <summary>A prior model turn.</summary>
    Assistant = 2,
}

/// <summary>One message in the request.</summary>
/// <param name="Role">Who authored it.</param>
/// <param name="Content">The text.</param>
public sealed record LlmMessage(LlmRole Role, string Content);

/// <summary>
/// The desired response shape: free text, or JSON constrained to a schema. The provider constrains the model; the
/// caller still owns the parse.
/// </summary>
/// <param name="JsonSchema">A JSON schema the response must satisfy, or <see langword="null"/> for free text.</param>
public sealed record LlmResponseFormat(string? JsonSchema)
{
    /// <summary>Free-text output.</summary>
    public static readonly LlmResponseFormat Text = new((string?)null);

    /// <summary>Structured JSON output constrained to <paramref name="schema"/>.</summary>
    /// <param name="schema">The JSON schema.</param>
    /// <returns>A JSON response format.</returns>
    public static LlmResponseFormat Json(string schema) => new(schema);
}

/// <summary>One request to the model.</summary>
/// <param name="Tier">The model tier.</param>
/// <param name="SystemPrompt">The system prompt.</param>
/// <param name="Messages">The conversation, oldest first.</param>
/// <param name="ResponseFormat">The desired response shape.</param>
/// <param name="MaxOutputTokens">A hard output-token ceiling.</param>
public sealed record LlmRequest(
    LlmModelTier Tier,
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    LlmResponseFormat ResponseFormat,
    int MaxOutputTokens = 1024);

/// <summary>Why the model stopped. Anything other than <see cref="Completed"/> is treated fail-closed by callers.</summary>
public enum LlmStopReason
{
    /// <summary>The model produced a complete response.</summary>
    Completed = 1,

    /// <summary>The response hit the output-token ceiling (likely truncated).</summary>
    MaxTokens = 2,

    /// <summary>The model refused to answer.</summary>
    Refusal = 3,

    /// <summary>Any other stop condition.</summary>
    Other = 4,
}

/// <summary>Token usage for one call — the hook the AIUsage cost/latency tracker (A2) fills from the real client.</summary>
/// <param name="InputTokens">Prompt tokens.</param>
/// <param name="OutputTokens">Completion tokens.</param>
public sealed record LlmUsage(int InputTokens, int OutputTokens)
{
    /// <summary>No usage — what a stub/fake returns; the real client reports the actual counts.</summary>
    public static LlmUsage None { get; } = new(0, 0);
}

/// <summary>The result of one completion.</summary>
/// <param name="Text">The completion text (JSON when a JSON format was requested).</param>
/// <param name="StopReason">Why the model stopped.</param>
/// <param name="Usage">Token usage.</param>
public sealed record LlmCompletion(string Text, LlmStopReason StopReason, LlmUsage Usage);
