namespace MarqSpec.TradingCopilot.Domain.Suggestions;

/// <summary>
/// The lifecycle state of an AI trade suggestion (R-4; data dictionary §6): active / stale / expired-void. The
/// <b>suggestion domain owns this vocabulary</b> (gh#545) — it moved here from the data layer once the lifecycle
/// gained a decision (<see cref="SuggestionLifecycle"/>) and writers, mirroring <c>ConditionalStatus</c>.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero — a DB check rejects it (the fail-closed-zero pattern, gh#60). The
/// lifecycle is <b>forward-only</b>: <see cref="Active"/> → <see cref="Stale"/> (drift, gh#546) → <see cref="ExpiredVoid"/>
/// (time, gh#545). A resolved suggestion is never chased back to life; a re-formed setup issues a new superseding
/// suggestion (gh#550).
/// </remarks>
public enum SuggestionState
{
    /// <summary>Not a state — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>Live and actionable within its validity window.</summary>
    Active = 1,

    /// <summary>Conditions have drifted; shown but flagged.</summary>
    Stale = 2,

    /// <summary>Expired without action — void, kept for the journal.</summary>
    ExpiredVoid = 3,
}
