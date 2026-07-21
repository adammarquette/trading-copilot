namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A trading account as the venue reports it. Deliberately thin — the verified ProjectX account shape is just
/// <c>{ id, name, balance, canTrade, isVisible, simulated }</c>, with size, <b>stage</b> and status encoded in
/// the <b>name</b>, so the adapter reads those and the core stays venue-neutral (R-17).
/// <see cref="Mode"/> is <b>not</b> among them: no venue reports whether capital is at risk, so it is resolved
/// from the operator's <see cref="FirmConventions"/> rather than derived here (gh#60).
/// </summary>
/// <param name="Id">The venue-qualified account identifier.</param>
/// <param name="Name">The venue's account name (e.g. <c>PRAC-50K-1234</c>, <c>50KTC-V2-DLL-0000</c>).</param>
/// <param name="Balance">The account balance as reported by the venue.</param>
/// <param name="CanTrade">Whether the venue permits trading this account.</param>
/// <param name="IsVisible">Whether the operator has left the account visible.</param>
/// <param name="Mode">
/// Whether capital is at risk, resolved from the firm's declared conventions. <see cref="TradingMode.Undeclared"/>
/// when the operator has not classified this account's stage — tradeable in no environment.
/// </param>
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
