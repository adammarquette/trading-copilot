namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// The lifecycle state of a persisted <see cref="Suggestion"/> (data dictionary §6: active / stale /
/// expired-void).
/// </summary>
/// <remarks>
/// Lives in the data layer (like <see cref="FirmType"/>) until the suggestion domain (R-4) lands and owns the
/// vocabulary. <see cref="Unknown"/> is the refusable zero — a DB check rejects it (the fail-closed-zero
/// pattern, gh#60).
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
