namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// Why the operator passed a <see cref="Suggestion"/> — the canonical <b>eight</b> reasons settled by gh#539, taken
/// from the operator-validated pass sheet in the wireframes. A multi-select, so it is a <c>[Flags]</c> value stored
/// in one integer and sliced by R-9 with a bitwise test.
/// </summary>
/// <remarks>
/// A pass is a <b>neutral decline</b> and its reason is explicitly optional (R-4), so <see cref="None"/> (zero) is a
/// legitimate, valid answer — "no reason given". The column therefore carries <b>no</b> refusable-zero check, unlike
/// <see cref="SuggestionDispositionKind"/> where the zero is a sentinel.
/// </remarks>
[Flags]
public enum SuggestionPassReason
{
    /// <summary>No reason given — a valid, neutral pass (R-4).</summary>
    None = 0,

    /// <summary>Already positioned in this instrument.</summary>
    AlreadyPositioned = 1 << 0,

    /// <summary>Elevated news / event risk around the setup.</summary>
    NewsRisk = 1 << 1,

    /// <summary>Wrong time — outside the operator's window for this trade.</summary>
    WrongTime = 1 << 2,

    /// <summary>Waiting for a better level before entering.</summary>
    WaitingBetterLevel = 1 << 3,

    /// <summary>Weak reward-to-risk on the proposed geometry.</summary>
    WeakRewardRisk = 1 << 4,

    /// <summary>Sizing does not fit the operator's plan.</summary>
    Sizing = 1 << 5,

    /// <summary>Against one of the operator's rules.</summary>
    AgainstARule = 1 << 6,

    /// <summary>Low conviction in the setup.</summary>
    LowConviction = 1 << 7,
}
