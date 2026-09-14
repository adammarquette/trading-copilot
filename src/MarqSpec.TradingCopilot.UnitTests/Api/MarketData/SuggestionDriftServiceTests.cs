using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The suggestion-drift watcher's core (gh#546, R-4 / R-12): on a batch of quotes, mark every <b>Active</b> suggestion
/// whose price has drifted past the entry tolerance <see cref="SuggestionState.Stale"/>. The behaviours that matter:
/// the neutral-symbol ↔ contract-key bridge (resolve each Active symbol once, match the quote), <b>cannot measure ⇒ no
/// transition</b> (no spec / no venue resolve leaves the row Active), forward-only (only Active is touched), the latest
/// quote per contract decides, and the background pass is cross-owner.
/// </summary>
/// <remarks>
/// The guarded writer is exercised through a faithful in-memory double: production's <see cref="SuggestionDrift"/> is an
/// <c>ExecuteUpdate</c> the in-memory provider cannot run (gh#530), so <see cref="InMemoryDrift"/> reproduces its
/// observable contract — mark <b>Active</b> rows that <see cref="SuggestionLifecycle.HasDrifted"/> (the unit-tested
/// semantic authority) via load-modify-save. The real compare-and-swap's atomicity is proven on Postgres by QA (gh#614).
/// </remarks>
public class SuggestionDriftServiceTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private static readonly DateTimeOffset _now = new(2026, 7, 30, 14, 0, 0, TimeSpan.Zero);

    // The real config-backed spec source: ES = 0.25 tick, so the default 8-tick band is 2.0 points.
    private static readonly IInstrumentSpecSource _specs = new InstrumentSpecSource(Options.Create(new InstrumentSpecOptions()));
    private static readonly IOptions<SuggestionOptions> _options = Options.Create(new SuggestionOptions());

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public SuggestionDriftServiceTests()
    {
        // Resolve any neutral symbol forward to a deterministic front-month key, so the quote's contract key matches.
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId instrument, CancellationToken _) => Task.FromResult(
                new ResolvedContract(VenueContractId.Create(VenueId.Parse("projectx"), ContractKey(instrument.Symbol)), instrument)));
    }

    private static string ContractKey(string symbol) => $"CON.F.US.{symbol}.U26";

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private readonly RecordingLogger _log = new();
    private readonly ISuggestionRealtimeNotifier _notifier = A.Fake<ISuggestionRealtimeNotifier>();

    private SuggestionDriftService Service(TradingCopilotDbContext context) =>
        new(context, _specs, _options, new InMemoryDrift(context), _notifier, _log);

    private async Task<Guid> SeedAsync(
        SuggestionState state = SuggestionState.Active,
        OrderSide side = OrderSide.Buy,
        decimal entry = 5_300m,
        string instrument = "ES",
        Guid? owner = null)
    {
        Guid id = Guid.NewGuid();
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Suggestions.Add(new Suggestion
        {
            Origin = SuggestionOrigin.Scan,
            Id = id,
            UserId = ownerId,
            AccountId = Guid.NewGuid(),
            Instrument = instrument,
            Side = side,
            Size = 2,
            EntryPrice = entry,
            StopPrice = side == OrderSide.Buy ? entry - 5m : entry + 5m,
            TargetPrice = side == OrderSide.Buy ? entry + 10m : entry - 10m,
            Mode = TradingMode.Practice,
            State = state,
            CreatedAt = _now.AddMinutes(-5),
            Rationale = "oversold bounce",
            CitedFactors =
            [
                new CitedFactor
                {
                    Id = Guid.NewGuid(),
                    UserId = ownerId,
                    Kind = CitedFactorKind.Indicator,
                    IsPrimary = true,
                    TimeframeMinutes = 1,
                    Indicator = "rsi",
                    Period = 14,
                },
            ],
            Confidence = 72,
            ExpiresAt = _now.AddMinutes(60),
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static StopPromotionService.DecodedQuote Quote(string symbol, decimal bid, decimal ask) =>
        new(VenueId.Parse("projectx"), ContractKey(symbol), bid, ask);

    // Every neutral symbol resolves to ONE shared front-month key — the collision the by-contract map has to survive.
    private void ResolveEverySymbolTo(string contractKey) =>
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId instrument, CancellationToken _) => Task.FromResult(
                new ResolvedContract(VenueContractId.Create(VenueId.Parse("projectx"), contractKey), instrument)));

    // Back to the constructor's behaviour: each symbol resolves to its own key.
    private void ResolveEachSymbolToItsOwnKey() =>
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId instrument, CancellationToken _) => Task.FromResult(
                new ResolvedContract(
                    VenueContractId.Create(VenueId.Parse("projectx"), ContractKey(instrument.Symbol)), instrument)));

    private async Task<int> RunAsync(params StopPromotionService.DecodedQuote[] quotes)
    {
        await using TradingCopilotDbContext context = Context();
        return await Service(context).ProcessQuotesAsync(quotes, _venue, _now, CancellationToken.None);
    }

    private async Task<SuggestionState> StateOfAsync(Guid id)
    {
        await using TradingCopilotDbContext context = Context();
        return (await context.Suggestions.IgnoreQueryFilters().SingleAsync(suggestion => suggestion.Id == id)).State;
    }

    // ---- the transition ----

    [Fact]
    public async Task ProcessQuotesAsync_ShouldMarkStale_WhenALongDriftsBeyondTolerance()
    {
        Guid id = await SeedAsync(side: OrderSide.Buy, entry: 5_300m);

        int stale = await RunAsync(Quote("ES", bid: 5_304.75m, ask: 5_305m)); // ask 5.0 above entry, band 2.0

        stale.Should().Be(1);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Stale);
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldMarkStale_WhenAShortDriftsBeyondTolerance()
    {
        Guid id = await SeedAsync(side: OrderSide.Sell, entry: 5_300m);

        // A short's achievable entry is the bid; a bid 5.0 below entry has drifted past the 2.0 band.
        (await RunAsync(Quote("ES", bid: 5_295m, ask: 5_295.25m))).Should().Be(1);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Stale);
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldLeaveActive_WhenWithinTolerance()
    {
        Guid id = await SeedAsync(side: OrderSide.Buy, entry: 5_300m);

        (await RunAsync(Quote("ES", bid: 5_300.75m, ask: 5_301m))).Should().Be(0); // ask 1.0 above, within the 2.0 band
        (await StateOfAsync(id)).Should().Be(SuggestionState.Active);
    }

    // ---- cannot measure => do not transition ----

    [Fact]
    public async Task ProcessQuotesAsync_ShouldNotTransition_WhenTheInstrumentHasNoSpec()
    {
        // MES is not among the built-in specs -> no tick size -> no band. Never fabricate one: the row stays Active.
        Guid id = await SeedAsync(instrument: "MES", entry: 5_300m);

        (await RunAsync(Quote("MES", bid: 5_309.75m, ask: 5_310m))).Should().Be(0);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Active);
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldNotTransition_WhenTheVenueCannotResolve()
    {
        Guid id = await SeedAsync(instrument: "ES", entry: 5_300m);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("venue unreachable"));

        // A venue that cannot resolve the front month leaves the row Active (fail-safe); the take-time re-check catches it.
        (await RunAsync(Quote("ES", bid: 5_309.75m, ask: 5_310m))).Should().Be(0);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Active);
    }

    // ---- forward-only + scoping ----

    [Fact]
    public async Task ProcessQuotesAsync_ShouldNotTouchANonActiveSuggestion()
    {
        // Already Stale: not discovered (Active-only) and never un-staled. Forward-only.
        Guid id = await SeedAsync(state: SuggestionState.Stale, entry: 5_300m);

        (await RunAsync(Quote("ES", bid: 5_309.75m, ask: 5_310m))).Should().Be(0);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Stale);
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldOnlyAffectTheQuotedInstrument()
    {
        Guid es = await SeedAsync(instrument: "ES", entry: 5_300m);
        Guid nq = await SeedAsync(instrument: "NQ", entry: 18_000m);

        // Only the ES quote arrives, drifted; NQ has no quote this pass.
        (await RunAsync(Quote("ES", bid: 5_304.75m, ask: 5_305m))).Should().Be(1);

        (await StateOfAsync(es)).Should().Be(SuggestionState.Stale);
        (await StateOfAsync(nq)).Should().Be(SuggestionState.Active, "no NQ quote arrived, so it cannot be measured");
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldUseTheLatestQuotePerContract()
    {
        Guid id = await SeedAsync(side: OrderSide.Buy, entry: 5_300m);

        // Two ES quotes in one batch: the first is within tolerance, the second (latest) has drifted. The latest wins.
        (await RunAsync(
            Quote("ES", bid: 5_300.75m, ask: 5_301m),
            Quote("ES", bid: 5_304.75m, ask: 5_305m))).Should().Be(1);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Stale);
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldMarkAcrossOwners()
    {
        // The pass is background plumbing: it bypasses the R-20 filter, so another operator's Active suggestion is
        // drift-checked too (a suggestion drifts on its own geometry alone).
        Guid id = await SeedAsync(owner: _other, side: OrderSide.Buy, entry: 5_300m);

        (await RunAsync(Quote("ES", bid: 5_304.75m, ask: 5_305m))).Should().Be(1);
        (await StateOfAsync(id)).Should().Be(SuggestionState.Stale);
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldDoNothing_WithNoActiveSuggestions()
    {
        await SeedAsync(state: SuggestionState.ExpiredVoid);

        (await RunAsync(Quote("ES", bid: 5_304.75m, ask: 5_305m))).Should().Be(0);
        // With nothing Active, the venue is never even resolved.
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // ---- contract-key collision: keep the first, skip the loser, drop nobody (PR #633 review) ----

    [Fact]
    public async Task ProcessQuotesAsync_ShouldKeepTheFirstSymbolAndWarn_WhenTwoResolveToTheSameContract()
    {
        // The collision branch exists because a pre-review found the map SILENTLY OVERWRITING: the second symbol
        // replaced the first, so the first's suggestions vanished from the pass with nothing said.
        //
        // Be precise about what the fix changes, because the obvious assertions do NOT pin it. Either way exactly one
        // symbol is measured and the other is left Active -- "one transition, one untouched" is equally true of the
        // bug. What actually changed is (a) WHICH symbol survives -- the first to claim the key, not the last -- and
        // (b) that the skip is now LOUD. Both are asserted below; drop either and this case passes against the bug it
        // exists to catch.
        Guid es = await SeedAsync(instrument: "ES", entry: 5_300m); // seeded first => resolved first => keeps the key
        Guid nq = await SeedAsync(instrument: "NQ", entry: 5_300m);
        ResolveEverySymbolTo(ContractKey("ES"));

        int stale = await RunAsync(Quote("ES", bid: 5_304.75m, ask: 5_305m));

        stale.Should().Be(1, "one symbol owns the key; the collision must swallow neither both nor nothing");
        (await StateOfAsync(es)).Should().Be(SuggestionState.Stale, "the FIRST symbol to claim the contract is kept");
        (await StateOfAsync(nq)).Should().Be(SuggestionState.Active,
            "the loser is skipped, never measured against another product's price");

        _log.Warnings.Should().ContainSingle("the whole point of the fix is that the skip stops being silent")
            .Which.Should().Contain("kept ES").And.Contain("skipped NQ",
                "a warning that does not name what it dropped cannot be acted on");
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldStillProcessTheSkippedSymbol_OnceTheKeysDifferAgain()
    {
        // Not a guard for the overwrite bug (it holds either way) -- it pins the neighbouring property that makes the
        // skip acceptable at all: "skipped" means skipped for THIS pass, not evicted from the watcher. A future change
        // that cached the collision, or dropped the loser from discovery, would leave a suggestion whose drift guard is
        // permanently off -- silently, which is the failure this whole feature exists to prevent.
        await SeedAsync(instrument: "ES", entry: 5_300m);
        Guid nq = await SeedAsync(instrument: "NQ", entry: 5_300m);
        ResolveEverySymbolTo(ContractKey("ES"));
        await RunAsync(Quote("ES", bid: 5_304.75m, ask: 5_305m));

        // The catalogue resolves them apart again; NQ must now transition on its own quote.
        ResolveEachSymbolToItsOwnKey();
        int stale = await RunAsync(Quote("NQ", bid: 5_304.75m, ask: 5_305m));

        stale.Should().Be(1);
        (await StateOfAsync(nq)).Should().Be(SuggestionState.Stale,
            "a collision defers a symbol by one pass; it does not retire the suggestion");
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldStampStateChangedAt_OnTheTransition()
    {
        Guid id = await SeedAsync(side: OrderSide.Buy, entry: 5_300m);

        await RunAsync(Quote("ES", bid: 5_304.75m, ask: 5_305m));

        await using TradingCopilotDbContext context = Context();
        (await context.Suggestions.IgnoreQueryFilters().SingleAsync(s => s.Id == id)).StateChangedAt.Should().Be(_now);
    }

    // ---- realtime push (gh#718) ----

    [Fact]
    public async Task ProcessQuotesAsync_ShouldPushEachDriftedSuggestionToItsOwner()
    {
        // gh#718: the drift transition is set-based, so it recovers the (SuggestionId, UserId) it moved and the
        // service pushes one per-owner realtime signal (Clients.User, R-20 -- never a broadcast). Two owners drift on
        // the same quote; each gets exactly their own suggestion's Stale signal, and neither the other's.
        Guid mine = await SeedAsync(owner: _operator, side: OrderSide.Buy, entry: 5_300m);
        Guid theirs = await SeedAsync(owner: _other, side: OrderSide.Buy, entry: 5_300m);

        (await RunAsync(Quote("ES", bid: 5_304.75m, ask: 5_305m))).Should().Be(2);

        A.CallTo(() => _notifier.SuggestionChangedAsync(
                _operator,
                A<RealtimeSuggestion>.That.Matches(push => push.SuggestionId == mine && push.State == "Stale" && push.At == _now),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifier.SuggestionChangedAsync(
                _other,
                A<RealtimeSuggestion>.That.Matches(push => push.SuggestionId == theirs && push.State == "Stale" && push.At == _now),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldNotPush_WhenNothingDrifted()
    {
        await SeedAsync(side: OrderSide.Buy, entry: 5_300m);

        (await RunAsync(Quote("ES", bid: 5_300.75m, ask: 5_301m))).Should().Be(0); // within the band

        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessQuotesAsync_ShouldReturnTheCountAndKeepTheTransition_WhenAPushFails()
    {
        // The push is best-effort and AFTER the commit: a hub fault must never fail the pass or roll back the Stale
        // write. The transition stays committed, the count is returned, and nothing throws out of the service.
        Guid id = await SeedAsync(side: OrderSide.Buy, entry: 5_300m);
        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        (await RunAsync(Quote("ES", bid: 5_304.75m, ask: 5_305m))).Should().Be(1);

        (await StateOfAsync(id)).Should().Be(SuggestionState.Stale, "the transition committed before the best-effort push");
    }

    /// <summary>
    /// Captures what the service logged. The collision branch's whole contribution is that a skip becomes <b>visible</b>
    /// — a `NullLogger` would swallow exactly the signal under test, so the suite would pass against the silent bug.
    /// </summary>
    private sealed class RecordingLogger : ILogger<SuggestionDriftService>
    {
        private readonly List<string> _warnings = [];

        /// <summary>The formatted messages logged at <see cref="LogLevel.Warning"/>.</summary>
        public IReadOnlyList<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.Warning)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>
    /// A faithful in-memory stand-in for the guarded <see cref="ISuggestionDrift"/> update: production runs it as an
    /// <c>ExecuteUpdate</c> the in-memory provider cannot (gh#530), so this reproduces its observable contract — mark
    /// the <b>Active</b> rows on the instrument that <see cref="SuggestionLifecycle.HasDrifted"/> — via load-modify-save
    /// over the same store. Atomicity under real concurrency is QA's, on Postgres (gh#614).
    /// </summary>
    private sealed class InMemoryDrift(TradingCopilotDbContext database) : ISuggestionDrift
    {
        public async Task<IReadOnlyList<SuggestionTransition>> MarkDriftedStaleAsync(
            string instrument, decimal bid, decimal ask, decimal band, DateTimeOffset now, CancellationToken cancellationToken)
        {
            List<Suggestion> rows = await database.Suggestions
                .IgnoreQueryFilters()
                .Where(suggestion => suggestion.State == SuggestionState.Active && suggestion.Instrument == instrument)
                .ToListAsync(cancellationToken);

            List<SuggestionTransition> transitioned = [];
            foreach (Suggestion suggestion in rows.Where(suggestion =>
                SuggestionLifecycle.HasDrifted(suggestion.Side, suggestion.EntryPrice, bid, ask, band)))
            {
                suggestion.State = SuggestionState.Stale;
                suggestion.StateChangedAt = now;
                transitioned.Add(new SuggestionTransition(suggestion.Id, suggestion.UserId));
            }

            await database.SaveChangesAsync(cancellationToken);
            return transitioned;
        }
    }
}
