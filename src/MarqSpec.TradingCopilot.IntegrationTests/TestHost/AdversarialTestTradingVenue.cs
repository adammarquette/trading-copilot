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
    private readonly HashSet<string> _unreadableAccounts = new(StringComparer.Ordinal);
    private bool _venueUnreachable;
    private int _positionReads;
    private bool _bracketsUnsupported;
    // Native working legs resting at the venue + the cancels the OCO-exit path issues against them (gh#184).
    private readonly List<(string AccountKey, WorkingOrder Order)> _workingOrders = [];
    private readonly List<(string AccountKey, string VenueOrderKey)> _cancelCalls = [];
    private readonly HashSet<string> _cancelThrowKeys = new(StringComparer.Ordinal);
    // Reprice dispatches the modify path aims at the resting native leg + the reject the venue can raise (gh#259).
    private readonly List<(string AccountKey, string VenueOrderKey, decimal? LimitPrice, decimal? StopPrice, int? Size)> _modifyCalls = [];
    private readonly HashSet<string> _modifyThrowKeys = new(StringComparer.Ordinal);
    // Stop-promotion concurrency (gh#274): a side-effect fired inside PlaceOrderAsync lets a test commit a
    // concurrent retire at the promotion's exact race window; the ids the stub handed back prove a self-cancel
    // targets the leg the promoter itself just placed; and a global cancel-reject drives the synthetic_risk path.
    private Func<Task>? _onPlaceOrder;
    private readonly List<string> _placedVenueOrderIds = [];
    private bool _allCancelsThrow;
    // Historical-bar backfill (gh#303): the venue FEEDS seeded bars via GetBarsAsync; it can be told to drop the
    // HistoricalBars capability (refuse loudly, R-17) or to throw, but it never decides the merge — it only feeds.
    private readonly List<Bar> _seededBars = [];
    private bool _historicalBarsUnsupported;
    private bool _getBarsThrows;

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

    /// <summary>
    /// Makes venue TRUTH unreadable for one account (gh#533), so a pass that reads several accounts meets a
    /// partial failure. <see cref="MakeVenueUnreachable"/> cannot express this: it is checked only inside
    /// <c>GetAccountsAsync</c>, which the protection census never calls.
    /// </summary>
    /// <param name="accountKey">The account whose position and working-order reads must throw.</param>
    public void MakeAccountUnreadable(string accountKey) => _unreadableAccounts.Add(accountKey);

    /// <summary>
    /// Drops <see cref="VenueCapability.BracketOrders"/> from the advertised capabilities — a venue that cannot
    /// hold an exchange-side protective stop. The send path must then <b>refuse the entry</b> rather than send it
    /// naked: better no trade than an unprotected one (ADR-0007, gh#11 inc 3). Cleared by <see cref="ResetPositions"/>.
    /// </summary>
    public void MakeBracketsUnsupported() => _bracketsUnsupported = true;

    /// <summary>Whether brackets are unsupported (see <see cref="MakeBracketsUnsupported"/>).</summary>
    internal bool BracketsUnsupported => _bracketsUnsupported;

    /// <summary>Whether the venue read path should throw (see <see cref="MakeVenueUnreachable"/>).</summary>
    internal bool VenueUnreachable => _venueUnreachable;

    internal bool IsUnreadable(VenueAccountId account) => _unreadableAccounts.Contains(account.Key);

    /// <summary>Seeds a native working leg resting at the venue (a protective stop / target) for the OCO-exit path to
    /// find via <c>GetWorkingOrdersAsync</c> (gh#184).</summary>
    public void SeedWorkingOrder(string accountKey, string venueOrderKey, string contractKey, decimal? stopPrice = 4_980m) =>
        _workingOrders.Add((accountKey, new WorkingOrder(
            venueOrderKey,
            VenueContractId.Create(VenueId.Parse("projectx"), contractKey),
            stopPrice is null ? null : new Price(stopPrice.Value),
            LimitPrice: null,
            Size: 1)));

    /// <summary>Makes <c>CancelOrderAsync</c> THROW for a venue order key — the "already gone" rejection the OCO-exit
    /// path must swallow without corrupting the record or retry-storming (gh#184).</summary>
    public void MakeCancelThrow(string venueOrderKey) => _cancelThrowKeys.Add(venueOrderKey);

    /// <summary>Every <c>CancelOrderAsync</c> issued, in order (account key + venue order key).</summary>
    public IReadOnlyList<(string AccountKey, string VenueOrderKey)> CancelOrderCalls => _cancelCalls.AsReadOnly();

    /// <summary>Makes <c>ModifyOrderAsync</c> THROW for a venue order key — a hard venue rejection of the reprice
    /// (e.g. the resting leg moved to a terminal state). The modify path must surface it as a 409, never force a
    /// local terminal or record a phantom price change (gh#259).</summary>
    public void MakeModifyThrow(string venueOrderKey) => _modifyThrowKeys.Add(venueOrderKey);

    /// <summary>Every <c>ModifyOrderAsync</c> issued, in order (account key + venue order key + the reprice targets).</summary>
    public IReadOnlyList<(string AccountKey, string VenueOrderKey, decimal? LimitPrice, decimal? StopPrice, int? Size)> ModifyOrderCalls =>
        _modifyCalls.AsReadOnly();

    /// <summary>Runs <paramref name="effect"/> inside every <c>PlaceOrderAsync</c>, before it returns — the seam a
    /// gh#274 test uses to commit a <b>concurrent retire</b> at the promotion's race window (a genuine second-connection
    /// write against real Postgres). Passing <see langword="null"/> clears it.</summary>
    public void OnPlaceOrder(Func<Task>? effect) => _onPlaceOrder = effect;

    /// <summary>The venue order ids <c>PlaceOrderAsync</c> has handed back — to prove a self-cancel targets the
    /// leg the promoter itself just placed, never some other order (gh#274).</summary>
    public IReadOnlyList<string> PlacedVenueOrderIds => _placedVenueOrderIds.AsReadOnly();

    /// <summary>Makes EVERY <c>CancelOrderAsync</c> throw — drives the self-cancel-fails → <c>synthetic_risk</c>
    /// path when the just-placed dangling leg cannot be pulled (gh#274).</summary>
    public void MakeCancelsThrow() => _allCancelsThrow = true;

    /// <summary>Feeds one closed/forming OHLCV bar that <c>GetBarsAsync</c> will return within its window (gh#303).
    /// The stub only feeds inputs — the backfill decides which are final and how to merge.</summary>
    public void SeedBar(DateTimeOffset openTime, decimal open, decimal high, decimal low, decimal close, long volume) =>
        _seededBars.Add(new Bar(openTime, new Price(open), new Price(high), new Price(low), new Price(close), volume));

    /// <summary>Clears the fed bars — e.g. to restate the same bucket with a revised value across passes (gh#303).</summary>
    public void ClearBars() => _seededBars.Clear();

    /// <summary>Drops <see cref="VenueCapability.HistoricalBars"/>, so <c>GetBarsAsync</c> refuses loudly with
    /// <see cref="VenueCapabilityNotSupportedException"/> (R-17) — a venue with no history to give (gh#303).</summary>
    public void MakeHistoricalBarsUnsupported() => _historicalBarsUnsupported = true;

    /// <summary>Makes <c>GetBarsAsync</c> THROW — a venue/gateway failure the backfill must ride out and retry next
    /// pass, never crashing the host (gh#303).</summary>
    public void MakeGetBarsThrow() => _getBarsThrows = true;

    /// <summary>Whether the venue has dropped historical-bar support (see <see cref="MakeHistoricalBarsUnsupported"/>).</summary>
    internal bool HistoricalBarsUnsupported => _historicalBarsUnsupported;

    /// <summary>Whether <c>GetBarsAsync</c> should throw (see <see cref="MakeGetBarsThrow"/>).</summary>
    internal bool GetBarsThrows => _getBarsThrows;

    /// <summary>The fed bars whose open falls in the requested window — the stub echoes inputs, nothing computed.</summary>
    internal IReadOnlyList<Bar> BarsIn(DateTimeOffset from, DateTimeOffset to) =>
        [.. _seededBars.Where(bar => bar.OpenTime >= from && bar.OpenTime <= to)];

    /// <summary>Clears seeded positions, close behaviour, and recorded close calls — call at the start of each test.</summary>
    public void ResetPositions()
    {
        _positions.Clear();
        _positionReads = 0;
        _unreadableAccounts.Clear();
        _survivingContracts.Clear();
        _throwingContracts.Clear();
        _closeCalls.Clear();
        _venueUnreachable = false;
        _bracketsUnsupported = false;
        _workingOrders.Clear();
        _cancelCalls.Clear();
        _cancelThrowKeys.Clear();
        _modifyCalls.Clear();
        _modifyThrowKeys.Clear();
        _onPlaceOrder = null;
        _placedVenueOrderIds.Clear();
        _allCancelsThrow = false;
        _seededBars.Clear();
        _historicalBarsUnsupported = false;
        _getBarsThrows = false;
    }

    internal IReadOnlyList<WorkingOrder> WorkingOrdersFor(VenueAccountId account) =>
        [.. _workingOrders.Where(entry => entry.AccountKey == account.Key).Select(entry => entry.Order)];

    internal void RecordCancel(VenueAccountId account, string venueOrderKey)
    {
        _cancelCalls.Add((account.Key, venueOrderKey));
        if (_allCancelsThrow || _cancelThrowKeys.Contains(venueOrderKey))
        {
            throw new InvalidOperationException($"Venue rejected cancel of {venueOrderKey} — order already gone.");
        }
    }

    internal string RecordPlaced(string venueOrderId)
    {
        _placedVenueOrderIds.Add(venueOrderId);
        return venueOrderId;
    }

    internal Task InvokePlaceSideEffectAsync() => _onPlaceOrder?.Invoke() ?? Task.CompletedTask;

    internal void RecordModify(VenueAccountId account, string venueOrderKey, Price? limitPrice, Price? stopPrice, int? size)
    {
        _modifyCalls.Add((account.Key, venueOrderKey, limitPrice?.Value, stopPrice?.Value, size));
        if (_modifyThrowKeys.Contains(venueOrderKey))
        {
            throw new InvalidOperationException($"Venue rejected modify of {venueOrderKey} — order gone or filled.");
        }
    }

    /// <summary>
    /// How many times venue position truth has been read (gh#408). Lets a test witness that a pass did <b>no venue
    /// work at all</b> — an assertion an empty result cannot make, since "nothing reported" is equally true when the
    /// pass read the venue and withheld.
    /// </summary>
    public int PositionReadCount => _positionReads;

    internal IReadOnlyList<PositionSnapshot> PositionsFor(VenueAccountId account)
    {
        _positionReads++;
        return [.. _positions.Where(position => position.Account == account)];
    }

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

    public VenueCapabilities Capabilities
    {
        get
        {
            VenueCapability capabilities = VenueCapability.Quotes;
            if (!_factory.HistoricalBarsUnsupported)
            {
                capabilities |= VenueCapability.HistoricalBars;
            }

            if (!_factory.BracketsUnsupported)
            {
                capabilities |= VenueCapability.BracketOrders;
            }

            return VenueCapabilities.Of(capabilities);
        }
    }

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
        CancellationToken cancellationToken = default)
    {
        // Refuse loudly when the capability is absent (R-17) — the same seam the real adapter enforces at the call.
        Capabilities.Require(VenueCapability.HistoricalBars);
        if (_factory.GetBarsThrows)
        {
            throw new InvalidOperationException("Venue history fetch failed (test).");
        }

        return Task.FromResult(_factory.BarsIn(from, to));
    }

    public async IAsyncEnumerable<Quote> StreamQuotesAsync(
        VenueContractId contract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield break;
    }

    public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(VenueAccountId account, CancellationToken cancellationToken = default) =>
        _factory.IsUnreadable(account)
            ? throw new InvalidOperationException($"Venue truth unreadable for {account.Key} (test).")
            : Task.FromResult(_factory.PositionsFor(account));

    public async Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        _placedOrders.Add(request);
        string venueOrderId = _factory.RecordPlaced($"STUB-ORDER-{Guid.NewGuid():N}");
        // The seam gh#274 uses to interleave a concurrent retire at the exact race window; a no-op otherwise.
        await _factory.InvokePlaceSideEffectAsync();
        return new PlacedOrder(request.Account, venueOrderId, DateTimeOffset.UtcNow);
    }

    public Task<IReadOnlyList<WorkingOrder>> GetWorkingOrdersAsync(VenueAccountId account, CancellationToken cancellationToken = default) =>
        _factory.IsUnreadable(account)
            ? throw new InvalidOperationException($"Venue truth unreadable for {account.Key} (test).")
            : Task.FromResult(_factory.WorkingOrdersFor(account));

    public Task CancelOrderAsync(VenueAccountId account, string venueOrderId, CancellationToken cancellationToken = default)
    {
        _factory.RecordCancel(account, venueOrderId);
        return Task.CompletedTask;
    }

    public Task ModifyOrderAsync(
        VenueAccountId account,
        string venueOrderId,
        Price? limitPrice,
        Price? stopPrice,
        int? size,
        CancellationToken cancellationToken = default)
    {
        _factory.RecordModify(account, venueOrderId, limitPrice, stopPrice, size);
        return Task.CompletedTask;
    }

    public Task<PositionSnapshot> ClosePositionAsync(VenueAccountId account, VenueContractId contract, CancellationToken cancellationToken = default) =>
        Task.FromResult(_factory.RecordCloseAndResult(account, contract));
}
