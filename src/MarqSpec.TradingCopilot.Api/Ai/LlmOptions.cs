using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The LLM provider configuration (R-4 / ADR-0008). Bound from the <c>Llm</c> config section.
/// </summary>
/// <remarks>
/// <see cref="ApiKey"/> is a <b>secret</b> — environment only, never source, never logged. Its mere presence is the
/// single switch <c>AiRegistration</c> reads: with a key, the agent-review route binds the real
/// <c>AnthropicLlmProvider</c> + <c>LlmTriggerReviewer</c> (A2, gh#423); without one, the honest inert reviewer —
/// the route still fires and still tells the operator a setup needs review. Running review-less is allowed; running
/// review-less <i>silently</i> is not. The model ids are <b>not secret</b> and default to a cheap triage model and a
/// stronger deep one (ADR-0008 tiering), so the operator can retune models without a code change. Mirrors
/// <c>PushoverOptions</c>.
/// </remarks>
public sealed class LlmOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Llm";

    /// <summary>
    /// The LLM provider API key. A <b>secret</b>: environment only, never in source, never logged. Read by the real
    /// <c>AnthropicLlmProvider</c> and sent on a request header only. Its presence gates the real vs. inert binding.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>The model for the cheap <see cref="LlmModelTier.Triage"/> tier ("is this worth surfacing?").</summary>
    public string TriageModel { get; init; } = "claude-haiku-4-5";

    /// <summary>The model for the <see cref="LlmModelTier.Deep"/> tier (genuinely hard synthesis).</summary>
    public string DeepModel { get; init; } = "claude-sonnet-5";

    /// <summary>How long one completion may take before the client aborts it — bounds a hung provider on the scan.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Whether an API key is present — the switch between the real reviewer and the inert one.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
