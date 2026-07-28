namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// Where a fired trigger goes (gh#385; the mechanical-alert vs agent-review split of #15). A <b>refusable zero</b>
/// (refused at construction and by a DB check). Inc-1 honors only <see cref="MechanicalAlert"/>;
/// <see cref="AgentReview"/> is refused at the authoring boundary until the agent-review route and suggestion
/// generation land (ADR-0008 — the LLM only at the edges, deliberately deferred).
/// </summary>
public enum TriggerRoute
{
    /// <summary>Unset — never a real route; refused at construction and by a DB check.</summary>
    Unknown = 0,

    /// <summary>Fires a deterministic operator alert — no LLM anywhere in the path.</summary>
    MechanicalAlert = 1,

    /// <summary>Hands the firing to agent review (suggestion generation). Deferred — refused for now.</summary>
    AgentReview = 2,
}
