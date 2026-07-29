namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// Where a trigger fire routes (ADR-0008) — the two outcomes a trigger definition chooses between.
/// </summary>
/// <remarks>
/// <para>
/// Both routes are built. <see cref="Mechanical"/> (gh#385): a fully-specified setup fires a deterministic alert
/// with <b>no LLM</b> in the loop. <see cref="AgentReview"/> (gh#402): the fire wakes a reviewer — <b>one LLM call
/// per arming edge</b>, behind the provider-neutral <c>ILlmProvider</c> seam ("LLM at the edges") — which either
/// proposes a <c>Suggestion</c> or suppresses. <see cref="Unknown"/> is refused.
/// </para>
/// <para>
/// The agent-review route <b>proposes; it never places an order</b>. Enforcement stays below the model: the review
/// path reaches no order, venue, or gate type, the size is the operator's (off the trigger) and the mode is read
/// live from the account, and the take-time risk gate remains the only path to execution.
/// </para>
/// </remarks>
public enum TriggerRoute
{
    /// <summary>Not a real route — refused.</summary>
    Unknown = 0,

    /// <summary>Fires a deterministic mechanical alert; no LLM in the loop.</summary>
    Mechanical = 1,

    /// <summary>
    /// Wakes an agent to review on fire — one LLM call per arming edge; it proposes a <c>Suggestion</c> or
    /// suppresses, and never executes (ADR-0008). Requires an account and a size on the trigger.
    /// </summary>
    AgentReview = 2,
}
