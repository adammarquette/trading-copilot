namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A position in one contract on one account, as the venue reports it. The venue — not a local cache — is the
/// source of truth for whether we are flat (ADR-0013), which is what auto-flatten reconciles against (R-13).
/// </summary>
/// <param name="Account">The account holding the position.</param>
/// <param name="Contract">The contract the position is in.</param>
/// <param name="NetQuantity">Signed net quantity: positive is long, negative is short, zero is flat.</param>
/// <param name="AveragePrice">The position's average entry price.</param>
public sealed record PositionSnapshot(
    VenueAccountId Account,
    VenueContractId Contract,
    int NetQuantity,
    Price AveragePrice)
{
    /// <summary>Whether the position is flat — nothing open in this contract.</summary>
    public bool IsFlat => NetQuantity == 0;

    /// <summary>Whether the position is long.</summary>
    public bool IsLong => NetQuantity > 0;

    /// <summary>Whether the position is short.</summary>
    public bool IsShort => NetQuantity < 0;
}
