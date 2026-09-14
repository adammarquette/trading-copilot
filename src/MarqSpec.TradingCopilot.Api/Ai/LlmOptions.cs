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

    // Per-tier cost rates (gh#431) — USD per MILLION tokens, input and output priced separately (LLM output bills
    // higher than input). Pinned defaults in ONE env-overridable place (the CohereOptions.EstimateCost pattern), so a
    // price change is a config line, not a code hunt. Defaults match Anthropic's list price at the time of writing
    // (Haiku 4.5 $1/$5, Sonnet 5 $3/$15) -- REVISIT THEM WHENEVER TriageModel / DeepModel CHANGES; a wrong rate
    // silently mis-estimates every ledger row's cost with no error.

    /// <summary>Triage-tier input price, USD per million tokens (<see cref="TriageModel"/>).</summary>
    public decimal TriageInputUsdPerMillion { get; init; } = 1.00m;

    /// <summary>Triage-tier output price, USD per million tokens.</summary>
    public decimal TriageOutputUsdPerMillion { get; init; } = 5.00m;

    /// <summary>Deep-tier input price, USD per million tokens (<see cref="DeepModel"/>).</summary>
    public decimal DeepInputUsdPerMillion { get; init; } = 3.00m;

    /// <summary>Deep-tier output price, USD per million tokens.</summary>
    public decimal DeepOutputUsdPerMillion { get; init; } = 15.00m;

    /// <summary>Whether an API key is present — the switch between the real reviewer and the inert one.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>The configured model id for a tier — the single source of truth the provider and the ledger share.</summary>
    /// <param name="tier">The model tier.</param>
    /// <returns>The model id.</returns>
    public string ModelFor(LlmModelTier tier) => tier switch
    {
        LlmModelTier.Triage => TriageModel,
        LlmModelTier.Deep => DeepModel,
        _ => TriageModel, // an undeclared tier triages -- the cheap, safe default
    };

    /// <summary>Estimates the USD cost of a call at the tier's pinned input / output rates.</summary>
    /// <param name="tier">The model tier the call ran at.</param>
    /// <param name="inputTokens">Prompt tokens billed.</param>
    /// <param name="outputTokens">Completion tokens billed.</param>
    /// <returns>The estimated dollar cost, never negative.</returns>
    public decimal EstimateCost(LlmModelTier tier, int inputTokens, int outputTokens)
    {
        (decimal inputRate, decimal outputRate) = tier switch
        {
            LlmModelTier.Deep => (DeepInputUsdPerMillion, DeepOutputUsdPerMillion),
            _ => (TriageInputUsdPerMillion, TriageOutputUsdPerMillion),
        };

        decimal input = Math.Max(0, inputTokens);
        decimal output = Math.Max(0, outputTokens);
        return (input * inputRate + output * outputRate) / 1_000_000m;
    }
}
