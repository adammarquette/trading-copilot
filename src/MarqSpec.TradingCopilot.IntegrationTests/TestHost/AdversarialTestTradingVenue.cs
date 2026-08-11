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
    // Suggestion-drift's symbol->contract bridge (gh#632, gh#546): a symbol the venue cannot resolve this pass —
    // the "cannot measure" half of ResolveContractAsync, distinct from a spec miss (which never reaches the venue).
    private readonly HashSet<string> _resolveContractThrowSymbols = new(StringComparer.OrdinalIgnoreCase);
    // The venue-refusal seam (gh#663, of #629): the fault a transmit meets AT the venue. The double only FEEDS the
    // exception the test names — it never decides what the catch site does with it, and a refused transmit mints NO
    // venue order id, so "nothing rests" is a property of the double rather than an answer it hands the system.
    private readonly Dictionary<string, Func<Exception>> _placeFaultsByCustomTag = new(StringComparer.Ordinal);
    private Func<Exception>? _placeFault;
    private readonly List<(long Entered, long Left)> _placeSpans = [];
    private readonly object _placeSpanGate = new();
    private long _placeSequence;
    // Venue FILL HISTORY (gh#769, the seam gh#723/gh#631 coverage needs): what GetWorkingOrdersAsync structurally
    // cannot see, because a filled entry is no longer a working order. The stub FEEDS the venue's report -- a seeded
    // execution, a reachable-but-empty history, or a read that faults -- and never the reconcile's decision about it.
    private readonly List<(string AccountKey, string Tag, TaggedFillEvidence Evidence)> _taggedFills = [];
    private readonly HashSet<string> _readableFillHistoryAccounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadableFillHistoryAccounts = new(StringComparer.Ordinal);
    private readonly List<(string AccountKey, string CustomTag, DateTimeOffset Since)> _fillHistoryQueries = [];
    private Func<Task>? _onFillHistoryRead;

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

    /// <summary>
    /// Seeds a native working leg resting at the venue (a protective stop / target) for the OCO-exit path and the
    /// venue-truth read to find via <c>GetWorkingOrdersAsync</c> (gh#184, gh#534).
    /// </summary>
    /// <remarks>
    /// <paramref name="limitPrice"/> and <paramref name="size"/> exist so the read's <b>size</b> and its
    /// <b>protective / not-protective</b> classification can be driven from the venue side. The stub feeds those
    /// inputs and never the computed answer — whether a leg counts as protection is production's rule, so there is
    /// deliberately no <c>isProtective</c> parameter here.
    /// </remarks>
    /// <param name="accountKey">The venue account the leg rests on.</param>
    /// <param name="venueOrderKey">The venue's own order handle.</param>
    /// <param name="contractKey">The contract the leg rests on.</param>
    /// <param name="stopPrice">The stop trigger, or <see langword="null"/> for a limit-only leg.</param>
    /// <param name="limitPrice">The limit price, or <see langword="null"/>.</param>
    /// <param name="size">How much the leg covers.</param>
    /// <param name="customTag">
    /// The venue's echo of the tag the placing call stamped. <c>POST /orders/{id}/reconcile</c> matches a resting
    /// order back to its stranded row by <b>exactly this</b> (<c>CustomTag == orderId</c>, gh#589), so a seeded
    /// order without one can never be adopted — which is the whole subject of gh#736's adopt case. Left null by
    /// default so every existing caller keeps the untagged order it was seeding.
    /// </param>
    public void SeedWorkingOrder(
        string accountKey,
        string venueOrderKey,
        string contractKey,
        decimal? stopPrice = 4_980m,
        decimal? limitPrice = null,
        int size = 1,
        string? customTag = null) =>
        _workingOrders.Add((accountKey, new WorkingOrder(
            venueOrderKey,
            VenueContractId.Create(VenueId.Parse("projectx"), contractKey),
            stopPrice is null ? null : new Price(stopPrice.Value),
            limitPrice is null ? null : new Price(limitPrice.Value),
            size)
        {
            CustomTag = customTag,
        }));

    /// <summary>
    /// Seeds an <b>executed</b> venue record carrying <paramref name="tag"/> in the account's fill history (gh#769,
    /// for gh#723 / gh#631), so <c>FindFilledOrderByTagAsync</c> reports <see cref="TaggedFillStatus.Filled"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this seam has to exist.</b> <see cref="SeedWorkingOrder"/> can only express an order still <i>resting</i>,
    /// and <see cref="SeedPosition"/> only an open position with no handle on it. Neither can express "this tag
    /// executed" — which is the single fact that separates a take that filled from one that never reached the market,
    /// and therefore the only thing that lets the reconcile adopt rather than refuse. Without it the pre-merge tier
    /// reaches only the refusal arms, and the adopt is provable nowhere but staging.
    /// </para>
    /// <para>
    /// <b>Still adversarial.</b> It feeds the venue's <i>report</i> — an executed size, a price, the venue's own order
    /// handle — and never the reconcile's answer. It deliberately does not filter on the caller's <c>since</c> window:
    /// deciding what window covers the attempt is production's rule, so the stub records the window it was asked for
    /// (<see cref="FillHistoryQueries"/>) and lets the test assert it, rather than silently enforcing it and hiding a
    /// window that starts too late.
    /// </para>
    /// </remarks>
    /// <param name="accountKey">The venue account whose history carries the execution.</param>
    /// <param name="tag">The <c>customTag</c> the placing call stamped — the stranded row's own id (gh#589).</param>
    /// <param name="filledSize">The executed quantity; must be greater than zero (a partial fill is a fill).</param>
    /// <param name="filledPrice">The average fill price the venue reports, or <see langword="null"/>.</param>
    /// <param name="venueOrderKey">The venue's own handle for the executed order — what an open position cannot carry.</param>
    /// <param name="matchCount">
    /// How many executed records carry this tag. Greater than one is the re-transmit ambiguity of PR #637: the
    /// evidence then describes only the earliest and anything journaled from it understates the venue.
    /// </param>
    public void SeedTaggedFill(
        string accountKey,
        string tag,
        decimal filledSize,
        decimal? filledPrice = null,
        string? venueOrderKey = null,
        int matchCount = 1) =>
        _taggedFills.Add((accountKey, tag, TaggedFillEvidence.Filled(
            tag, filledSize, filledPrice, venueOrderKey, matchCount)));

    /// <summary>
    /// Declares the account's fill history <b>readable and empty</b> — the venue is reachable, does carry history, and
    /// positively reports no execution under the tag (<see cref="TaggedFillStatus.NoFillFound"/>, gh#769).
    /// </summary>
    /// <remarks>
    /// Distinct from seeding nothing at all, which leaves the venue answering
    /// <see cref="TaggedFillStatus.Unsupported"/> — "this venue cannot answer that question". Collapsing the two is
    /// exactly the confusion <see cref="TaggedFillEvidence"/> is four-valued to prevent, so the stub keeps them apart.
    /// </remarks>
    /// <param name="accountKey">The venue account whose history is readable.</param>
    public void MakeFillHistoryEmpty(string accountKey) => _readableFillHistoryAccounts.Add(accountKey);

    /// <summary>
    /// Makes the fill-history read <b>fault</b> for an account (gh#769) — a venue that can answer but did not on this
    /// attempt.
    /// </summary>
    /// <remarks>
    /// It throws rather than returning <see cref="TaggedFillStatus.Unavailable"/> on purpose: mapping a failed read to
    /// "unavailable, which is NOT 'did not fill'" is <c>FillReconciliationService</c>'s rule, and a stub that handed
    /// the status back directly would be supplying the production-computed answer instead of the condition that
    /// produces it. Takes precedence over any seeded fill, so a test can prove the veto is withheld on an unknown even
    /// where an execution really exists.
    /// </remarks>
    /// <param name="accountKey">The venue account whose fill-history read should fault.</param>
    public void MakeFillHistoryUnreadable(string accountKey) => _unreadableFillHistoryAccounts.Add(accountKey);

    /// <summary>
    /// Clears every armed fill-history fault, so the read answers again (gh#769) — the counterpart to
    /// <see cref="ClearPlaceOrderFaults"/>, and what lets one case reconcile twice off a single fixture: once with the
    /// read faulting, then once without, so the refusal is shown to have been caused by the fault and by nothing else.
    /// </summary>
    public void ClearFillHistoryFaults() => _unreadableFillHistoryAccounts.Clear();

    /// <summary>
    /// Every fill-history read the system issued — the account, the tag asked about, and the <c>since</c> window it
    /// supplied (gh#769). The window is the load-bearing input: one that starts <b>after</b> the attempt would miss a
    /// real fill and let a release proceed over it, and a stub that quietly filtered on it could never witness that.
    /// </summary>
    public IReadOnlyList<(string AccountKey, string CustomTag, DateTimeOffset Since)> FillHistoryQueries =>
        _fillHistoryQueries.AsReadOnly();

    /// <summary>
    /// Runs <paramref name="effect"/> inside every <c>FindFilledOrderByTagAsync</c>, before it answers — the seam a
    /// gh#769 test uses to launch a concurrent entry <b>while the reconcile is mid-flight and still holding the
    /// per-account entry lock</b>. Passing <see langword="null"/> clears it.
    /// </summary>
    /// <remarks>
    /// The reconcile transmits nothing, so <see cref="OnPlaceOrder"/> cannot reach inside it; the fill-history read is
    /// the one venue call the adopt arm makes while the lock is held. As with <see cref="OnPlaceOrder"/>, the peer must
    /// be <b>sampled, never awaited</b> here — awaiting it would deadlock on the very lock under test.
    /// </remarks>
    /// <param name="effect">The interleave to run, or <see langword="null"/> to clear it.</param>
    public void OnFillHistoryRead(Func<Task>? effect) => _onFillHistoryRead = effect;

    /// <summary>Runs the fill-history interleave (see <see cref="OnFillHistoryRead"/>); a no-op when none is armed.</summary>
    internal Task InvokeFillHistorySideEffectAsync() => _onFillHistoryRead?.Invoke() ?? Task.CompletedTask;

    /// <summary>
    /// The venue's fill-history answer for a tag — resolution order: an unreadable account <b>faults</b> (even over a
    /// seeded execution), then a seeded execution, then a readable-but-empty history, then "this venue cannot answer".
    /// </summary>
    internal TaggedFillEvidence FillHistoryFor(VenueAccountId account, string customTag, DateTimeOffset since)
    {
        _fillHistoryQueries.Add((account.Key, customTag, since));

        if (_unreadableFillHistoryAccounts.Contains(account.Key))
        {
            throw new InvalidOperationException($"Venue fill history unreadable for {account.Key} (test).");
        }

        (string AccountKey, string Tag, TaggedFillEvidence Evidence) seeded = _taggedFills.FirstOrDefault(
            entry => entry.AccountKey == account.Key && string.Equals(entry.Tag, customTag, StringComparison.Ordinal));
        if (seeded.Evidence is not null)
        {
            return seeded.Evidence;
        }

        return _readableFillHistoryAccounts.Contains(account.Key)
            ? TaggedFillEvidence.NoFillFound(customTag)
            : TaggedFillEvidence.Unsupported(customTag);
    }

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

    /// <summary>
    /// Makes <c>ResolveContractAsync</c> THROW for <paramref name="symbol"/> — the venue cannot resolve this
    /// instrument's front-month contract this pass (gh#632, gh#546). Distinct from an unconfigured spec: that
    /// case never reaches the venue at all, so this is the only way to exercise the venue-round-trip failure half
    /// of "cannot measure ⇒ no transition".
    /// </summary>
    public void MakeResolveContractThrow(string symbol) => _resolveContractThrowSymbols.Add(symbol);

    /// <summary>Whether <c>ResolveContractAsync</c> should throw for a symbol (see <see cref="MakeResolveContractThrow"/>).</summary>
    internal bool ResolveContractThrows(string symbol) => _resolveContractThrowSymbols.Contains(symbol);

    /// <summary>
    /// Makes <b>every</b> <c>PlaceOrderAsync</c> refuse with the exception <paramref name="fault"/> builds — the
    /// venue-refusal seam (gh#663, of #629). A refused transmit mints <b>no</b> venue order id and hands back
    /// nothing, so the catch site's decision (release the row, or keep the durable intent) is production's alone:
    /// the double feeds the fault and never the answer.
    /// </summary>
    /// <param name="fault">Builds the exception to throw — a fresh instance per transmit.</param>
    public void MakePlaceOrderThrow(Func<Exception> fault) => _placeFault = fault;

    /// <summary>
    /// Makes only the transmit carrying <paramref name="customTag"/> refuse (gh#663) — the correlation handle the
    /// take stamps with its order id and the conditional fire with its own id (gh#577 / gh#589). Lets one entry on
    /// an account be refused while a concurrent peer transmits normally, which is what the serialization case needs.
    /// </summary>
    /// <param name="customTag">The correlation handle the refused transmit carries.</param>
    /// <param name="fault">Builds the exception to throw — a fresh instance per transmit.</param>
    public void MakePlaceOrderThrowFor(string customTag, Func<Exception> fault) =>
        _placeFaultsByCustomTag[customTag] = fault;

    /// <summary>Clears every armed place-order refusal, so transmits succeed again (gh#663).</summary>
    public void ClearPlaceOrderFaults()
    {
        _placeFault = null;
        _placeFaultsByCustomTag.Clear();
    }

    /// <summary>
    /// Every <c>PlaceOrderAsync</c> call as an <b>ordinal span</b> — the sequence number on entry and on exit, from
    /// one monotonic counter shared by all venues this factory creates (gh#663). Two spans that interleave witness
    /// two entry-transmits in flight on the account at once, which <c>IAccountEntryGuard</c> exists to prevent; a
    /// wall clock could not say it as cheaply or as deterministically.
    /// </summary>
    public IReadOnlyList<(long Entered, long Left)> PlaceOrderCallSpans
    {
        get
        {
            lock (_placeSpanGate)
            {
                return [.. _placeSpans];
            }
        }
    }

    /// <summary>Forgets every recorded transmit span, so a case reads only its own (gh#663).</summary>
    public void ResetPlaceOrderCallSpans()
    {
        lock (_placeSpanGate)
        {
            _placeSpans.Clear();
        }
    }

    /// <summary>The refusal armed for this transmit, if any — the tag-specific one first, then the global.</summary>
    internal Exception? PlaceFaultFor(string? customTag) =>
        customTag is not null && _placeFaultsByCustomTag.TryGetValue(customTag, out Func<Exception>? tagged)
            ? tagged()
            : _placeFault?.Invoke();

    /// <summary>Stamps a transmit's entry ordinal (see <see cref="PlaceOrderCallSpans"/>).</summary>
    internal long EnterPlaceCall() => Interlocked.Increment(ref _placeSequence);

    /// <summary>Closes the span opened at <paramref name="entered"/> (see <see cref="PlaceOrderCallSpans"/>).</summary>
    internal void LeavePlaceCall(long entered)
    {
        long left = Interlocked.Increment(ref _placeSequence);
        lock (_placeSpanGate)
        {
            _placeSpans.Add((entered, left));
        }
    }

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
        _resolveContractThrowSymbols.Clear();
        _taggedFills.Clear();
        _readableFillHistoryAccounts.Clear();
        _unreadableFillHistoryAccounts.Clear();
        _fillHistoryQueries.Clear();
        _onFillHistoryRead = null;
        ClearPlaceOrderFaults();
        lock (_placeSpanGate)
        {
            _placeSpans.Clear();
        }
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
        _factory.ResolveContractThrows(instrument.Symbol)
            ? throw new InvalidOperationException($"Venue could not resolve the front-month contract for {instrument.Symbol} (test).")
            : Task.FromResult(new ResolvedContract(VenueContractId.Create(Id, $"{instrument.Symbol}M25"), instrument));

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
        ArgumentNullException.ThrowIfNull(request);
        _placedOrders.Add(request);
        long entered = _factory.EnterPlaceCall();
        try
        {
            if (_factory.PlaceFaultFor(request.CustomTag) is { } refusal)
            {
                // A REFUSED transmit (gh#663): the side effect runs first, so a test can interleave a peer while
                // this transmit is still in flight; then the venue throws WITHOUT minting a venue order id, so
                // nothing rests and nothing is handed back for the catch site to journal.
                await _factory.InvokePlaceSideEffectAsync();
                throw refusal;
            }

            string venueOrderId = _factory.RecordPlaced($"STUB-ORDER-{Guid.NewGuid():N}");
            // The seam gh#274 uses to interleave a concurrent retire at the exact race window; a no-op otherwise.
            await _factory.InvokePlaceSideEffectAsync();
            return new PlacedOrder(request.Account, venueOrderId, DateTimeOffset.UtcNow);
        }
        finally
        {
            _factory.LeavePlaceCall(entered);
        }
    }

    public Task<IReadOnlyList<WorkingOrder>> GetWorkingOrdersAsync(VenueAccountId account, CancellationToken cancellationToken = default) =>
        _factory.IsUnreadable(account)
            ? throw new InvalidOperationException($"Venue truth unreadable for {account.Key} (test).")
            : Task.FromResult(_factory.WorkingOrdersFor(account));

    /// <summary>
    /// The venue's fill-history read (gh#769) — the seam that makes "this tag executed" expressible at the pre-merge
    /// tier. Overrides the interface's <see cref="TaggedFillStatus.Unsupported"/> default so the reconcile paths that
    /// consult fill history (gh#631's round-tripped branch, gh#723's adopt-still-open branch) can reach a positive
    /// answer here instead of only on staging.
    /// </summary>
    /// <remarks>
    /// The side effect runs <b>before</b> the answer, so a test can interleave a concurrent entry while the reconcile
    /// is still inside the per-account entry lock. A faulting read <b>throws</b> rather than reporting
    /// <see cref="TaggedFillStatus.Unavailable"/>: turning a failed read into that status is the caller's rule, not the
    /// venue's, and the double must never hand back the production-computed answer.
    /// </remarks>
    public async Task<TaggedFillEvidence> FindFilledOrderByTagAsync(
        VenueAccountId account,
        string customTag,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await _factory.InvokeFillHistorySideEffectAsync();
        return _factory.FillHistoryFor(account, customTag, since);
    }

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
