using System.Diagnostics;
using MarqSpec.Client.Tradovate;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>
/// The Tradovate venue adapter behind <see cref="ITradingVenue"/> (R-17, gh#41 / gh#977). This increment is the
/// <b>read-only</b> slice: contract resolution and account / position reads. Market-data streaming and execution are
/// ungranted and refuse loudly through the capability seam (or as <see cref="NotSupportedException"/>) until their
/// slices land (gh#977). Every Tradovate-specific detail — integer ids, an already-signed net position, demo-vs-live
/// as the mode source (a brokerage, gh#780) — stops here so the core sees only the venue-neutral model.
/// </summary>
public sealed class TradovateVenue : ITradingVenue
{
    private readonly ITradovateApiClient _api;
    private readonly FirmConventions _conventions;

    /// <summary>Creates the adapter over a configured Tradovate client and a firm's conventions.</summary>
    /// <param name="api">The Tradovate REST client (credentials and host from configuration).</param>
    /// <param name="conventions">
    /// What the firm behind this login has declared (R-14, gh#60). Tradovate is a brokerage, so these are
    /// <see cref="FirmConventions.ForBrokerage"/> and mode follows the venue's own host.
    /// <see cref="FirmConventions.None"/> resolves every account to <see cref="TradingMode.Undeclared"/> — the
    /// intended failure direction (tradeable nowhere), not an accident.
    /// </param>
    public TradovateVenue(ITradovateApiClient api, FirmConventions conventions)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(conventions);

        _api = api;
        _conventions = conventions;
    }

    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("tradovate");

    /// <summary>
    /// What this adapter delivers through <see cref="ITradingVenue"/> today: nothing yet. The read-only slice
    /// (gh#977) exposes contract resolution and account / position reads — none of which are capability-gated — and
    /// grants no market-data or execution capability, so those paths refuse at the seam rather than doing something
    /// partial.
    /// </summary>
    public VenueCapabilities Capabilities { get; } = VenueCapabilities.None;

    /// <summary>
    /// The Tradovate derivation-logic version (ADR-0009). <b>1</b> — the initial read slice: mode from the configured
    /// host through the firm's brokerage conventions (gh#780), a signed net position, and integer ids. Bump on any
    /// change to how raw Tradovate facts become derived values (mode, stage).
    /// </summary>
    public int AdapterLogicVersion => 1;

    /// <inheritdoc />
    public async Task<ResolvedContract> ResolveContractAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken = default)
    {
        // Exact-name resolution for now: FindContract matches a full contract name (e.g. ESM24). Resolving a bare
        // product root (ES) to its front month belongs to the market-data slice (gh#977), where bars and quotes need
        // the same contract handling — so this maps what the client returns and fails loudly on no match.
        ClientModels.Contract? contract = await _api.FindContractAsync(instrument.Symbol, cancellationToken);

        return contract is null
            ? throw new TradovateVenueException($"No Tradovate contract matches instrument '{instrument}'.")
            : TradovateMapping.ToResolvedContract(contract, instrument, Id);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        VenueContractId contract,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan barSize,
        CancellationToken cancellationToken = default)
    {
        // Bars ride the Tradovate WebSocket (no REST bar endpoint) — the gh#977 market-data slice. Ungranted here, so
        // the capability seam refuses rather than silently returning nothing.
        Capabilities.Require(VenueCapability.HistoricalBars);
        throw new UnreachableException();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Quote> StreamQuotesAsync(
        VenueContractId contract,
        CancellationToken cancellationToken = default)
    {
        // Eager refusal (like the ProjectX adapter): a missing capability fails at the call, not on the first read.
        Capabilities.Require(VenueCapability.Quotes);
        throw new UnreachableException();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        // Tradovate is a brokerage (gh#780): mode follows the venue's own host, and there is one host per process, so
        // every discovered account takes the same demo/live flag. Resolve it once and REFUSE the whole discovery on an
        // unrecognised host — we cannot know whether such a host is practice or live, and mapping an account off an
        // unknown flag would let it persist as live-tradeable downstream (the fail-open the read-time Undeclared alone
        // does not prevent, because the raw flag is recomputed at write points).
        if (TradovateMapping.IsSimulatedHost(_api.ConfiguredHost) is not { } venueReportsSimulated)
        {
            throw new TradovateVenueException(
                "The configured Tradovate host is neither the demo nor the live host, so an account's practice-vs-live "
                + "mode cannot be classified. Configure a recognised Tradovate host.");
        }

        IReadOnlyList<ClientModels.Account> accounts = await _api.GetAccountsAsync(cancellationToken);
        IReadOnlyList<ClientModels.CashBalance> balances = await _api.GetCashBalancesAsync(cancellationToken);

        // Balance is a separate cash-balance read, not on the account, so join by account id. A single (settlement)
        // cash-balance row per account is assumed — the futures case; when several exist (multi-currency) the lowest
        // currency id is taken deterministically, pending fuller multi-currency support (gh#977). An account with none
        // reads a zero balance rather than failing the whole discovery.
        Dictionary<long, decimal> balanceByAccount = balances
            .GroupBy(balance => balance.AccountId)
            .ToDictionary(group => group.Key, group => group.OrderBy(balance => balance.CurrencyId).First().Amount);

        return
        [
            .. accounts.Select(account => TradovateMapping.ToVenueAccount(
                account,
                account.Id is { } id && balanceByAccount.TryGetValue(id, out decimal amount) ? amount : 0m,
                Id,
                _conventions,
                venueReportsSimulated)),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(
        VenueAccountId account,
        CancellationToken cancellationToken = default)
    {
        long accountId = TradovateMapping.ToAccountId(account, Id);

        // The client lists positions for the whole login, so filter to this account. A flat position (netPos 0) is
        // not an open position — skip it, matching the "one snapshot per contract with an open position" contract.
        IReadOnlyList<ClientModels.Position> positions = await _api.GetPositionsAsync(cancellationToken);

        return
        [
            .. positions
                .Where(position => position.AccountId == accountId && position.NetPos != 0)
                .Select(position => TradovateMapping.ToPositionSnapshot(position, Id)),
        ];
    }

    /// <inheritdoc />
    public Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The Tradovate venue is read-only in this increment; execution lands with gh#977 (slice 3).");

    /// <inheritdoc />
    public Task CancelOrderAsync(
        VenueAccountId account,
        string venueOrderId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The Tradovate venue is read-only in this increment; execution lands with gh#977 (slice 3).");

    /// <inheritdoc />
    public Task<PositionSnapshot> ClosePositionAsync(
        VenueAccountId account,
        VenueContractId contract,
        CancellationToken cancellationToken = default)
    {
        // Closing a position is the auto-flatten primitive (R-13); ungranted until the execution slice, so refuse at
        // the seam rather than leave a caller believing a flatten path exists here.
        Capabilities.Require(VenueCapability.ClosePosition);
        throw new UnreachableException();
    }
}
