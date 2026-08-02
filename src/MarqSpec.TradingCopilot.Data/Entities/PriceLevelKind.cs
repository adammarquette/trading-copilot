namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// Which side of price a <see cref="PriceLevel"/> zone is (data dictionary §2, R-10): a floor price has tended to
/// hold above, or a ceiling it has tended to hold below.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (the shipped fail-closed-zero pattern, gh#60) — a DB check rejects
/// it, so a level is never persisted without a side. Role-reversal (a broken support becoming resistance) is the
/// detector's concern (gh#597); this only names the two sides.
/// </remarks>
public enum PriceLevelKind
{
    /// <summary>Not a side — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>A floor: price has tended to hold above this zone.</summary>
    Support = 1,

    /// <summary>A ceiling: price has tended to hold below this zone.</summary>
    Resistance = 2,
}
