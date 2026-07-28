namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// Where a trigger fire routes (ADR-0008) — the two outcomes a trigger definition chooses between.
/// </summary>
/// <remarks>
/// Only <see cref="Mechanical"/> is built: a fully-specified setup fires a deterministic alert with <b>no LLM</b>.
/// <see cref="AgentReview"/> — wake an agent to review / enrich / decide, one LLM call per fire — is deferred to a
/// later increment ("LLM at the edges"); the evaluator refuses it until then. <see cref="Unknown"/> is refused.
/// </remarks>
public enum TriggerRoute
{
    /// <summary>Not a real route — refused.</summary>
    Unknown = 0,

    /// <summary>Fires a deterministic mechanical alert; no LLM in the loop.</summary>
    Mechanical = 1,

    /// <summary>Wakes an agent to review on fire (deferred — one LLM call per fire, ADR-0008).</summary>
    AgentReview = 2,
}
