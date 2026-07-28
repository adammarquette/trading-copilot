namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The LLM provider credentials (R-4 / ADR-0008). Bound from the <c>Llm</c> config section.
/// </summary>
/// <remarks>
/// <see cref="ApiKey"/> is a <b>secret</b> — environment only, never source, never logged. It is <b>unused in this
/// increment</b> (the <c>StubLlmProvider</c> does no I/O); it is reserved for the real Anthropic client (A2). Its
/// mere presence is the single switch <c>AiRegistration</c> reads to decide whether the agent-review route binds the
/// real reviewer or the honest inert one — absent, the route still fires and still tells the operator a setup needs
/// review. Running review-less is allowed; running review-less <i>silently</i> is not. Mirrors <c>PushoverOptions</c>.
/// </remarks>
public sealed class LlmOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Llm";

    /// <summary>
    /// The LLM provider API key. A <b>secret</b>: environment only, never in source, never logged. <b>Unused in this
    /// increment</b> — the stub provider does no I/O — and reserved for the real client (A2).
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>Whether an API key is present — the switch between the real reviewer and the inert one.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
