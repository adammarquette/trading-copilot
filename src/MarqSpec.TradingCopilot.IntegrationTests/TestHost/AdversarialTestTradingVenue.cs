using System.Runtime.CompilerServices;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

internal class AdversarialTestProjectXVenueFactory : IProjectXVenueFactory
{
    public AdversarialTestTradingVenue LastVenueCreated { get; private set; } = null!;

    public ITradingVenue Create(FirmConventions conventions)
    {
        LastVenueCreated = new AdversarialTestTradingVenue(conventions);
        return LastVenueCreated;
    }
}

internal class AdversarialTestTradingVenue : ITradingVenue
{
    private readonly FirmConventions _conventions;
    private readonly List<OrderRequest> _placedOrders = [];

    public AdversarialTestTradingVenue(FirmConventions conventions)
    {
        _conventions = conventions;
    }

    public VenueId Id => VenueId.Parse("projectx");

    public int AdapterLogicVersion => 2;

    public VenueCapabilities Capabilities => VenueCapabilities.Of(VenueCapability.HistoricalBars | VenueCapability.Quotes);

    public int PlacedOrdersCount => _placedOrders.Count;
    public IReadOnlyList<OrderRequest> PlacedOrderRequests => _placedOrders.AsReadOnly();

    public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        // ADVERSARIAL STUB: Deliberately report TradingMode.Live for ALL accounts from the venue.
        // This proves that domain logic (AccountModeMapping.RecomputeMode) recomputes mode from conventions
        // rather than trusting whatever mode the venue reports.
        List<VenueAccount> accounts =
        [
            CreateVenueAccount("PRAC-50K-101", "PRAC-50K-101", true),
            CreateVenueAccount("50KTC-V2-202", "50KTC-V2-202", true),
            CreateVenueAccount("EXPRESS-50K-303", "EXPRESS-50K-303", false),
            CreateVenueAccount("UNKNOWN-NAME-999", "UNKNOWN-NAME-999", false),
        ];

        return Task.FromResult<IReadOnlyList<VenueAccount>>(accounts);
    }

    private VenueAccount CreateVenueAccount(string key, string name, bool canTrade)
    {
        AccountStage stage = ProjectXAccountStage.Resolve(name);

        return new VenueAccount(
            Id: VenueAccountId.Create(Id, key),
            Name: name,
            Balance: 50_000m,
            CanTrade: canTrade,
            IsVisible: true,
            Mode: TradingMode.Live) // Adversarial claim!
        {
            Stage = stage,
        };
    }

    public Task<ResolvedContract> ResolveContractAsync(InstrumentId instrument, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ResolvedContract(VenueContractId.Create(Id, $"{instrument.Symbol}M25"), instrument));

    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        VenueContractId contract,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan barSize,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Bar>>([]);

    public async IAsyncEnumerable<Quote> StreamQuotesAsync(
        VenueContractId contract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield break;
    }

    public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(VenueAccountId account, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PositionSnapshot>>([]);

    public Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        _placedOrders.Add(request);
        return Task.FromResult(new PlacedOrder(request.Account, $"STUB-ORDER-{Guid.NewGuid():N}", DateTimeOffset.UtcNow));
    }

    public Task CancelOrderAsync(VenueAccountId account, string venueOrderId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<PositionSnapshot> ClosePositionAsync(VenueAccountId account, VenueContractId contract, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PositionSnapshot(account, contract, 0, new Price(0m)));
}
