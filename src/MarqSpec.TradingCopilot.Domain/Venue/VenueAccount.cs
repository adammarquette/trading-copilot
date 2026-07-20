namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A trading account as the venue reports it. Deliberately thin — the verified ProjectX account shape is just
/// <c>{ id, name, balance, canTrade, isVisible }</c>, with size, stage, status, and practice-vs-live encoded in
/// the <b>name</b> — so the adapter derives <see cref="Mode"/> and the core stays venue-neutral (R-17).
/// </summary>
/// <param name="Id">The venue-qualified account identifier.</param>
/// <param name="Name">The venue's account name (e.g. <c>PRAC-50K-1234</c>, <c>50KTC-V2-DLL-7035</c>).</param>
/// <param name="Balance">The account balance as reported by the venue.</param>
/// <param name="CanTrade">Whether the venue permits trading this account.</param>
/// <param name="IsVisible">Whether the operator has left the account visible.</param>
/// <param name="Mode">Practice or live, as derived by the adapter.</param>
public sealed record VenueAccount(
    VenueAccountId Id,
    string Name,
    decimal Balance,
    bool CanTrade,
    bool IsVisible,
    TradingMode Mode)
{
    /// <summary>
    /// Whether the account belongs in the account switcher — tradable and not hidden. The full roster (passed,
    /// failed, hidden) stays available in settings (R-17).
    /// </summary>
    public bool IsSelectable => CanTrade && IsVisible;
}
