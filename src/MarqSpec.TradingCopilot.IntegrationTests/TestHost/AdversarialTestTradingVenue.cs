using System.Runtime.CompilerServices;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

internal class AdversarialTestProjectXVenueFactory : IProjectXVenueFactory
{
    private readonly List<AdversarialTestTradingVenue> _created = [];

    // Shared, mutable venue-position state for the auto-flatten / kill-switch suites (gh#186/#188/#190). The
    // service builds a FRESH venue per pass (Create() each time), so open positions and close behaviour must live
    // on the FACTORY and be read by every created venue. This keeps the seam adversarial: the stub only ever
    // reports/echoes state the test seeded — it never computes the answer under test.
    private readonly List<PositionSnapshot> _positions = [];
    private readonly HashSet<string> _survivingContracts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _throwingContracts = new(StringComparer.Ordinal);
    private readonly List<(string AccountKey, string ContractKey)> _closeCalls = [];
    private bool _venueUnreachable;
    // Native working legs resting at the venue + the cancels the OCO-exit path issues against them (gh#184).
    private readonly List<(string AccountKey, WorkingOrder Order)> _workingOrders = [];
    private readonly List<(string AccountKey, string VenueOrderKey)> _cancelCalls = [];
    private readonly HashSet<string> _cancelThrowKeys = new(StringComparer.Ordinal);

    public AdversarialTestTradingVenue LastVenueCreated { get; private set; } = null!;

    /// <summary>
    /// Every order this factory's venues have transmitted, across all compositions (gh#157). The ladder builds a
    /// fresh venue per evaluation (<c>ComposeAsync</c> calls <see cref="Create"/> each time), so
    /// <see cref="LastVenueCreated"/> alone cannot answer "did arming transmit?" — the arm's venue is discarded
    /// before the assertion runs. Snapshot this before an action and compare after.
    /// </summary>
    public IReadOnlyList<OrderRequest> AllPlacedOrderRequests => [.. _created.SelectMany(venue => venue.PlacedOrderRequests)];

    /// <summary>The cumulative count behind <see cref="AllPlacedOrderRequests"/>.</summary>
    public int TotalPlacedOrderCount => _created.Sum(venue => venue.PlacedOrdersCount);

    /// <summary>Seeds an OPEN position on a venue account so a flatten / backstop pass has something to close.</summary>
    public void SeedPosition(string accountKey, string contractKey, int netQuantity, decimal averagePrice = 5_000m) =>
        _positions.Add(new PositionSnapshot(
            VenueAccountId.Create(VenueId.Parse("projectx"), accountKey),
            VenueContractId.Create(VenueId.Parse("projectx"), contractKey),
            netQuantity,
            new Price(averagePrice)));

    /// <summary>
    /// Makes <c>ClosePositionAsync</c> keep reporting the contract OPEN (a reject / partial-fill shape) so the
    /// flatten verifier retries to the attempt cap and then escalates — the surviving-position failure mode.
    /// </summary>
    public void MakeCloseIneffective(string contractKey) => _survivingContracts.Add(contractKey);

    /// <summary>
    /// Makes <c>ClosePositionAsync</c> THROW for the contract — a hard venue rejection of the close order, the
    /// rejected-order failure mode the watchdog must journal and retry rather than swallow (gh#188).
    /// </summary>
    public void MakeCloseThrow(string contractKey) => _throwingContracts.Add(contractKey);

    /// <summary>Every <c>ClosePositionAsync</c> the flatten path issued, in order (account key + contract key).</summary>
    public IReadOnlyList<(string AccountKey, string ContractKey)> ClosePositionCalls => _closeCalls.AsReadOnly();

    /// <summary>
    /// Makes the venue read path (<c>GetAccountsAsync</c> / <c>GetPositionsAsync</c>) THROW — the venue is
    /// unreachable, so venue truth cannot be obtained. The settlement reconcile must then declare-unknown rather
    /// than present a stale live-looking number (gh#194, R-19). Set AFTER discovery; cleared by <see cref="ResetPositions"/>.
    /// </summary>
    public void MakeVenueUnreachable() => _venueUnreachable = true;

    /// <summary>Whether the venue read path should throw (see <see cref="MakeVenueUnreachable"/>).</summary>
    internal bool VenueUnreachable => _venueUnreachable;

    /// <summary>Seeds a native working leg resting at the venue (a protective stop / target) for the OCO-exit path to
    /// find via <c>GetWorkingOrdersAsync</c> (gh#184).</summary>
    public void SeedWorkingOrder(string accountKey, string venueOrderKey, string contractKey, decimal? stopPrice = 4_980m) =>
        _workingOrders.Add((accountKey, new WorkingOrder(
            venueOrderKey,
            VenueContractId.Create(VenueId.Parse("projectx"), contractKey),
            stopPrice is null ? null : new Price(stopPrice.Value),
            LimitPrice: null)));

    /// <summary>Makes <c>CancelOrderAsync</c> THROW for a venue order key — the "already gone" rejection the OCO-exit
    /// path must swallow without corrupting the record or retry-storming (gh#184).</summary>
    public void MakeCancelThrow(string venueOrderKey) => _cancelThrowKeys.Add(venueOrderKey);

    /// <summary>Every <c>CancelOrderAsync</c> issued, in order (account key + venue order key).</summary>
    public IReadOnlyList<(string AccountKey, string VenueOrderKey)> CancelOrderCalls => _cancelCalls.AsReadOnly();

    /// <summary>Clears seeded positions, close behaviour, and recorded close calls — call at the start of each test.</summary>
    public void ResetPositions()
    {
        _positions.Clear();
        _survivingContracts.Clear();
        _throwingContracts.Clear();
        _closeCalls.Clear();
        _venueUnreachable = false;
        _workingOrders.Clear();
        _cancelCalls.Clear();
        _cancelThrowKeys.Clear();
    }

    internal IReadOnlyList<WorkingOrder> WorkingOrdersFor(VenueAccountId account) =>
        [.. _workingOrders.Where(entry => entry.AccountKey == account.Key).Select(entry => entry.Order)];

    internal void RecordCancel(VenueAccountId account, string venueOrderKey)
    {
        _cancelCalls.Add((account.Key, venueOrderKey));
        if (_cancelThrowKeys.Contains(venueOrderKey))
        {
            throw new InvalidOperationException($"Venue rejected cancel of {venueOrderKey} — order already gone.");
        }
    }

    internal IReadOnlyList<PositionSnapshot> PositionsFor(VenueAccountId account) =>
        [.. _positions.Where(position => position.Account == account)];

    internal PositionSnapshot RecordCloseAndResult(VenueAccountId account, VenueContractId contract)
    {
        _closeCalls.Add((account.Key, contract.Key));

        if (_throwingContracts.Contains(contract.Key))
        {
            throw new InvalidOperationException($"Venue rejected the close of {contract.Key}.");
        }

        if (_survivingContracts.Contains(contract.Key))
        {
            // Echo the seeded (still-open) position — the venue "did not" flatten it.
            PositionSnapshot? open = _positions.FirstOrDefault(p => p.Account == account && p.Contract == contract);
            return open ?? new PositionSnapshot(account, contract, 1, new Price(5_000m));
        }

        return new PositionSnapshot(account, contract, 0, new Price(0m)); // flat
    }

    public ITradingVenue Create(FirmConventions conventions)
    {
        LastVenueCreated = new AdversarialTestTradingVenue(conventions, this);
        _created.Add(LastVenueCreated);
        return LastVenueCreated;
    }
}

internal class AdversarialTestTradingVenue : ITradingVenue
{
    private readonly FirmConventions _conventions;
    private readonly AdversarialTestProjectXVenueFactory _factory;
    private readonly List<OrderRequest> _placedOrders = [];

    public AdversarialTestTradingVenue(FirmConventions conventions, AdversarialTestProjectXVenueFactory factory)
    {
        _conventions = conventions;
        _factory = factory;
    }

    public VenueId Id => VenueId.Parse("projectx");

    public int AdapterLogicVersion => 2;

    public VenueCapabilities Capabilities => VenueCapabilities.Of(VenueCapability.HistoricalBars | VenueCapability.Quotes | VenueCapability.BracketOrders);

    public int PlacedOrdersCount => _placedOrders.Count;
    public IReadOnlyList<OrderRequest> PlacedOrderRequests => _placedOrders.AsReadOnly();

    public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (_factory.VenueUnreachable)
        {
            throw new InvalidOperationException("Venue unreachable (test): venue truth cannot be obtained.");
        }

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
        Task.FromResult(_factory.PositionsFor(account));

    public Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        _placedOrders.Add(request);
        return Task.FromResult(new PlacedOrder(request.Account, $"STUB-ORDER-{Guid.NewGuid():N}", DateTimeOffset.UtcNow));
    }

    public Task<IReadOnlyList<WorkingOrder>> GetWorkingOrdersAsync(VenueAccountId account, CancellationToken cancellationToken = default) =>
        Task.FromResult(_factory.WorkingOrdersFor(account));

    public Task CancelOrderAsync(VenueAccountId account, string venueOrderId, CancellationToken cancellationToken = default)
    {
        _factory.RecordCancel(account, venueOrderId);
        return Task.CompletedTask;
    }

    public Task<PositionSnapshot> ClosePositionAsync(VenueAccountId account, VenueContractId contract, CancellationToken cancellationToken = default) =>
        Task.FromResult(_factory.RecordCloseAndResult(account, contract));
}
