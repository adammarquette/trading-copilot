namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The <b>account</b> slice of the venue abstraction (R-17): discovering the accounts behind a login and reading
/// their state. Separate from <see cref="IOrderExecutor"/> on purpose — the risk layer (R-5) reads positions and
/// balances without holding the right to trade.
/// </summary>
/// <remarks>
/// One login exposes many accounts (practice and funded), and a firm's login is per-firm even when several firms
/// share a platform — so account discovery belongs to the adapter, not to configuration.
/// </remarks>
public interface IAccountSource : IVenue
{
    /// <summary>Discovers the trading accounts reachable with this venue's credentials.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The accounts, including ones that are not <see cref="VenueAccount.IsSelectable"/>.</returns>
    Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the venue's current positions for an account — the source of truth for what is open.</summary>
    /// <param name="account">The account to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One snapshot per contract with an open position.</returns>
    Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(
        VenueAccountId account,
        CancellationToken cancellationToken = default);
}
