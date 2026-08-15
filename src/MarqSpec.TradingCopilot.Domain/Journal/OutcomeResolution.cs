namespace MarqSpec.TradingCopilot.Domain.Journal;

/// <summary>
/// How a suggestion or trade finally resolved (data dictionary §7, R-9) — the terminal fact the journal's
/// <c>Outcome</c> records.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the unset zero and is <b>refused by a DB check</b>: a persisted outcome always carries
/// a real resolution, so a defaulted or bad-cast value cannot masquerade as one (the same posture
/// <c>SuggestionState</c> takes).
/// </remarks>
public enum OutcomeResolution
{
    /// <summary>The unset zero — never a resolved outcome; refused by <c>CK_Outcomes_Resolution_NotUnknown</c>.</summary>
    Unknown = 0,

    /// <summary>A closed round trip whose realized P&amp;L was positive.</summary>
    Win = 1,

    /// <summary>A closed round trip whose realized P&amp;L was negative.</summary>
    Loss = 2,

    /// <summary>
    /// A scratch — no net result: an entry that round-tripped to <b>exactly flat</b>, or a suggestion that never
    /// filled and was not left to expire (passed / cancelled first).
    /// </summary>
    NoFillScratch = 3,

    /// <summary>A suggestion whose validity window passed with no fill.</summary>
    Expired = 4,
}
