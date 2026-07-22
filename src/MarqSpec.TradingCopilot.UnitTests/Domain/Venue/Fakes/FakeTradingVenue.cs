using System.Runtime.CompilerServices;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue.Fakes;

/// <summary>
/// An in-memory <see cref="ITradingVenue"/> — a full venue (all three R-17 slices) with no broker behind it.
/// Its job is to prove the seam is implementable and to stand in for a venue in tests.
/// </summary>
internal sealed class FakeTradingVenue : ITradingVenue
{
    private readonly List<OrderRequest> _placedOrders = [];

    public FakeTradingVenue(VenueCapabilities? capabilities = null)
    {
        Capabilities = capabilities ?? VenueCapabilities.Of(
            VenueCapability.HistoricalBars
            | VenueCapability.Quotes
            | VenueCapability.ClosePosition);
    }

    public VenueId Id { get; } = VenueId.Parse("fake");

    public VenueCapabilities Capabilities { get; }

    public int AdapterLogicVersion => 1;

    public IReadOnlyList<OrderRequest> PlacedOrders => _placedOrders;

    public Task<ResolvedContract> ResolveContractAsync(InstrumentId instrument, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            new ResolvedContract(VenueContractId.Create(Id, $"{instrument.Symbol}M25"), instrument));
    }

    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        VenueContractId contract,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan barSize,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Bar> bars =
        [
            new Bar(from, new Price(5000m), new Price(5010m), new Price(4995m), new Price(5005m), 1_200),
        ];

        return Task.FromResult(bars);
    }

    public async IAsyncEnumerable<Quote> StreamQuotesAsync(
        VenueContractId contract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new Quote(DateTimeOffset.UnixEpoch, new Price(5000m), new Price(5000.25m), 10, 12);
    }

    public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<VenueAccount> accounts =
        [
            new VenueAccount(
                VenueAccountId.Create(Id, "9001"),
                "PRAC-50K-1234",
                Balance: 50_000m,
                CanTrade: true,
                IsVisible: true,
                Mode: TradingMode.Practice),
        ];

        return Task.FromResult(accounts);
    }

    public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(
        VenueAccountId account,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PositionSnapshot> positions =
        [
            new PositionSnapshot(account, VenueContractId.Create(Id, "ESM25"), 2, new Price(5000.25m)),
        ];

        return Task.FromResult(positions);
    }

    public Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        _placedOrders.Add(request);

        return Task.FromResult(new PlacedOrder(
            request.Account,
            $"fake-order-{_placedOrders.Count}",
            DateTimeOffset.UnixEpoch));
    }

    public Task CancelOrderAsync(VenueAccountId account, string venueOrderId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<PositionSnapshot> ClosePositionAsync(
        VenueAccountId account,
        VenueContractId contract,
        CancellationToken cancellationToken = default)
    {
        // The venue's own post-close view -- what ADR-0013 reconciles against, not our cached belief.
        return Task.FromResult(new PositionSnapshot(account, contract, 0, new Price(0m)));
    }
}
