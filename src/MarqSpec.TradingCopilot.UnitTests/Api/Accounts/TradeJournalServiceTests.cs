using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Accounts;

/// <summary>
/// The production Trade writer (gh#731): a round trip closing at the venue becomes a journaled <see cref="Trade"/>
/// with a signed, tick-value-aware <c>RealizedPnL</c>. The behaviours that matter: the P&amp;L is right for both
/// sides, a replayed flat event never double-writes, mode comes from the order rather than the account's current
/// declaration, and an unbalanced or foreign position writes nothing at all.
/// </summary>
public class TradeJournalServiceTests
{
    private const string OurCredentialKey = "topstep-main";
    private const string Contract = "CON.F.US.MES.U26";
    private static VenueId Projectx { get; } = VenueId.Parse("projectx");
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly DateTimeOffset _now = new(2026, 1, 15, 14, 30, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private DbContextOptions<TradingCopilotDbContext> Options() =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context() => new(Options(), new FixedUser(Guid.Empty));

    private TradeJournalService Service(IExecutionMetrics? metrics = null) => new(
        Context(),
        Options(),
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = OurCredentialKey }),
        metrics ?? NullExecutionMetrics.Instance,
        NullLogger<TradeJournalService>.Instance);

    // The flat event lands at or after the closing fill it reports -- the venue emits it BECAUSE net reached zero.
    // Default it past every fill these tests seed (all within the first hour) so the exit.At bound (gh#734) takes in
    // the whole round trip; a test exercising a LATE flat passes an explicit earlier `at`.
    private PositionEvent Flat(string venueAccountKey, string contract = Contract, DateTimeOffset? at = null) =>
        new(VenueAccountId.Create(Projectx, venueAccountKey), at ?? _now.AddHours(1),
            VenueContractId.Create(Projectx, contract), NetQuantity: 0, new Price(5_300m));

    private async Task<Guid> SeedAccountAsync(
        Guid owner, string venueAccountKey, string credentialKey = OurCredentialKey,
        TradingMode mode = TradingMode.Practice)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "Topstep", Type = FirmType.PropFirm });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = owner,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = credentialKey,
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = owner,
            ConnectionId = connectionId,
            VenueAccountKey = venueAccountKey,
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = mode,
            CanTrade = true,
            IsVisible = true,
            Balance = 50_000m,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    /// <summary>Seeds a filled order and its fills. Returns the order id.</summary>
    private async Task<Guid> SeedFilledOrderAsync(
        Guid owner,
        Guid accountId,
        OrderSide side,
        (decimal Price, int Size, int Minute)[] fills,
        decimal pointValue = 5m,
        TradingMode mode = TradingMode.Practice,
        Guid? suggestionId = null,
        OrderStatus status = OrderStatus.Filled,
        int placedMinute = 0)
    {
        Guid orderId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = owner,
            AccountId = accountId,
            SuggestionId = suggestionId,
            Instrument = Contract,
            Side = side,
            Size = fills.Sum(fill => fill.Size),
            Type = OrderType.Market,
            EntryPrice = fills[0].Price,
            WorkingStopPrice = 5_290m,
            SafetyStopPrice = 5_280m,
            PointValue = pointValue,
            Status = status,
            Mode = mode,
            VenueOrderKey = $"venue-{orderId:N}",
            PlacedAt = _now.AddMinutes(placedMinute),
        });
        foreach ((decimal price, int size, int minute) in fills)
        {
            context.Fills.Add(new Fill
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                OrderId = orderId,
                VenueFillKey = Guid.NewGuid().ToString("N"),
                Price = price,
                Size = size,
                ExecutedAt = _now.AddMinutes(minute),
            });
        }

        await context.SaveChangesAsync();
        return orderId;
    }

    /// <summary>
    /// Seeds an order that produced <b>no fills</b> — a risk-gate rejection, or a resting order cancelled before any
    /// execution. It ends in <paramref name="status"/> with zero <see cref="Fill"/> rows: venue truth is that
    /// nothing traded, so it must never be mistaken for the leg of a round trip. Returns the order id.
    /// </summary>
    private async Task<Guid> SeedZeroFillOrderAsync(
        Guid owner,
        Guid accountId,
        OrderSide side,
        OrderStatus status,
        int placedMinute = 0,
        decimal pointValue = 5m,
        TradingMode mode = TradingMode.Practice,
        Guid? suggestionId = null)
    {
        Guid orderId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = owner,
            AccountId = accountId,
            SuggestionId = suggestionId,
            Instrument = Contract,
            Side = side,
            Size = 1,
            Type = OrderType.Market,
            EntryPrice = 5_000m,
            WorkingStopPrice = 5_290m,
            SafetyStopPrice = 5_280m,
            PointValue = pointValue,
            Status = status,
            Mode = mode,
            VenueOrderKey = $"venue-{orderId:N}",
            PlacedAt = _now.AddMinutes(placedMinute),
        });
        await context.SaveChangesAsync();
        return orderId;
    }

    /// <summary>
    /// Seeds a filled order whose fills carry <b>explicit ids</b>, inserted in array order — so a test can pin the
    /// deterministic (ExecutedAt, Id) tie-break and the DB insertion order that real Postgres leaves unspecified.
    /// </summary>
    private async Task SeedOrderWithFillIdsAsync(
        Guid owner, Guid accountId, OrderSide side, (Guid Id, decimal Price, int Size, int Minute)[] fills,
        OrderStatus status = OrderStatus.Filled, TradingMode mode = TradingMode.Practice, decimal pointValue = 5m)
    {
        Guid orderId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = owner,
            AccountId = accountId,
            Instrument = Contract,
            Side = side,
            Size = fills.Sum(fill => fill.Size),
            Type = OrderType.Market,
            EntryPrice = fills[0].Price,
            WorkingStopPrice = 5_290m,
            SafetyStopPrice = 5_280m,
            PointValue = pointValue,
            Status = status,
            Mode = mode,
            VenueOrderKey = $"venue-{orderId:N}",
            PlacedAt = _now.AddMinutes(fills[0].Minute),
        });
        foreach ((Guid id, decimal price, int size, int minute) in fills)
        {
            context.Fills.Add(new Fill
            {
                Id = id,
                UserId = owner,
                OrderId = orderId,
                VenueFillKey = Guid.NewGuid().ToString("N"),
                Price = price,
                Size = size,
                ExecutedAt = _now.AddMinutes(minute),
            });
        }

        await context.SaveChangesAsync();
    }

    private async Task<List<Trade>> TradesAsync() =>
        await Context().Trades.IgnoreQueryFilters().ToListAsync();

    [Fact]
    public async Task ProcessFlatAsync_ShouldRecordTheJournalledOutcome_WhenATradeIsWritten()
    {
        // gh#734 review. Every flat's journaling outcome must be counted through IExecutionMetrics so an account
        // stuck permanently refusing is visible to an alert, not only to a log line.
        IExecutionMetrics metrics = A.Fake<IExecutionMetrics>();
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        await Service(metrics).ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalWritten))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldRecordTheRefusalOutcome_WhenTheOpeningDirectionIsAmbiguous()
    {
        // A refusal must still be COUNTABLE (gh#759 keeps refuse-don't-guess for GENUINE ambiguity, converting only
        // scale-in and reverse to composition). Two opposite-side fills share the opening instant, so which side
        // opened -- and the sign of the P&L -- is not decidable, and the composer refuses. An account wedged on
        // refusals is what the metric exists to surface, and it must NOT read as a journalled trade.
        IExecutionMetrics metrics = A.Fake<IExecutionMetrics>();
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 0)]); // SAME instant, opposite side

        await Service(metrics).ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalNotComposable))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalWritten))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldPickADeterministicClosingFill_WhenTheExitLegHasFillsAtTheSameInstant()
    {
        // gh#734 adversarial review (BLOCKING). The exit leg has two fills at the SAME instant (a market order
        // sweeping the book). The closing fill is the trade's natural key behind a unique index; picked by arbitrary
        // DB row order, a replay that sees the rows differently picks a DIFFERENT key -- the pre-check and the unique
        // index both miss and the round trip is journalled TWICE, double-counting into the daily governor. The pick
        // must be a deterministic total order (ExecutedAt, then Id). The LESSER-id fill is inserted first, so a naive
        // input-order pick chooses it; the deterministic pick must choose the greater-id fill regardless of row order.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 2, 0)]);

        Guid lesser = new("00000000-0000-0000-0000-000000000001");
        Guid greater = new("00000000-0000-0000-0000-0000000000ff");
        await SeedOrderWithFillIdsAsync(
            owner, accountId, OrderSide.Sell, [(lesser, 5_010m, 1, 5), (greater, 5_010m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001", at: _now.AddMinutes(5)), CancellationToken.None);

        (await TradesAsync()).Should().ContainSingle().Subject.ClosingFillId.Should().Be(
            greater,
            "the closing fill must be chosen by a deterministic total order (ExecutedAt then Id), not by DB row "
            + "order, so a replayed flat recomposes the SAME closing fill and the unique index catches the duplicate");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldRefuse_WhenASameInstantBoundaryMakesThePairingAmbiguous()
    {
        // gh#734's merge hazard, and gh#759's answer. Trip 1 (Buy 1 -> Sell 1) is already journalled. Trip 2 reopens
        // at the SAME instant Trip 1 closed -- so at :10 there is a buy AND a sell with no venue sequence to order
        // them, and whether the window is "close then reopen" or "scale then close" (opposite signs) is undecidable.
        // Refuse-don't-guess: the second flat writes nothing rather than risk a wrong-signed trade, and Trip 1 is
        // never double-counted. (Trip 2's realized P&L is under-reported, the SAFE direction, until the ambiguity clears.)
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");

        Guid tripOneEntry = new("00000000-0000-0000-0000-000000000010");
        Guid tripOneClose = new("00000000-0000-0000-0000-0000000000cc");
        Guid tripTwoOpen = new("00000000-0000-0000-0000-0000000000aa");
        Guid tripTwoClose = new("00000000-0000-0000-0000-0000000000dd");

        // Trip 1 exists and journals first, from only its own two fills.
        await SeedOrderWithFillIdsAsync(owner, accountId, OrderSide.Buy, [(tripOneEntry, 5_000m, 1, 0)]);
        await SeedOrderWithFillIdsAsync(owner, accountId, OrderSide.Sell, [(tripOneClose, 5_010m, 1, 10)]);
        await Service().ProcessFlatAsync(Flat("9001", at: _now.AddMinutes(10)), CancellationToken.None);
        (await TradesAsync()).Should().ContainSingle("trip 1 journals on its own flat -- the fixture precondition");

        // Trip 2 reopens at the SAME :10 instant as trip 1's close (a buy and a sell share the instant) and closes at :15.
        await SeedOrderWithFillIdsAsync(owner, accountId, OrderSide.Buy, [(tripTwoOpen, 5_010m, 1, 10)]);
        await SeedOrderWithFillIdsAsync(owner, accountId, OrderSide.Sell, [(tripTwoClose, 5_020m, 1, 15)]);

        bool journalled = await Service().ProcessFlatAsync(Flat("9001", at: _now.AddMinutes(15)), CancellationToken.None);

        journalled.Should().BeFalse("the same-instant open/close boundary is ambiguous -- refuse rather than guess a sign");
        List<Trade> trades = await TradesAsync();
        trades.Should().ContainSingle("only trip 1 is journalled; the ambiguous second window wrote nothing");
        trades[0].Size.Should().Be(1, "no blended size-2 double-count of trip 1's P&L");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldRefuse_WhenALateInterleavedFillRepairsAnAlreadyJournalledTrade()
    {
        // gh#759 review (BLOCKING). A flat processed before all fills are ingested journals a partial trade; if a
        // LATE, out-of-order fill then lands BEFORE the journalled close and the flat is replayed, FIFO re-pairs the
        // same fills into DIFFERENT legs with different (ClosingFillId, OpeningFillId) keys. The exact-pair dedup can't
        // see the old row is now orphaned, so writing the new legs would DOUBLE-COUNT into the daily governor. The
        // re-pairing guard fails closed: refuse, keep the one (under-reported) row, never double-count.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");

        Guid f1 = new("00000000-0000-0000-0000-000000000f01");
        Guid f2 = new("00000000-0000-0000-0000-000000000f02");

        // Run 1: only f1 (buy @:0) and f2 (sell @:10) are ingested; the flat composes a balanced Buy 1 -> Sell 1.
        await SeedOrderWithFillIdsAsync(owner, accountId, OrderSide.Buy, [(f1, 5_000m, 1, 0)]);
        await SeedOrderWithFillIdsAsync(owner, accountId, OrderSide.Sell, [(f2, 5_010m, 1, 10)]);
        await Service().ProcessFlatAsync(Flat("9001", at: _now.AddMinutes(10)), CancellationToken.None);
        (await TradesAsync()).Should().ContainSingle("run 1 journals the partial trade from the fills it can see");

        // Late, out-of-order fills land INTERLEAVED (between :0 and :10): a scale-in buy @:4 and a sell @:6.
        await SeedOrderWithFillIdsAsync(owner, accountId, OrderSide.Buy, [(Guid.NewGuid(), 5_000m, 1, 4)]);
        await SeedOrderWithFillIdsAsync(owner, accountId, OrderSide.Sell, [(Guid.NewGuid(), 5_005m, 1, 6)]);

        // Replay the SAME flat. FIFO now re-pairs the four fills into legs whose keys don't match the journalled row.
        bool journalled = await Service().ProcessFlatAsync(Flat("9001", at: _now.AddMinutes(10)), CancellationToken.None);

        journalled.Should().BeFalse("the fill set re-paired -- refuse rather than double-count into the daily governor");
        (await TradesAsync()).Should().ContainSingle("no new rows: the guard prevented the re-paired double-count");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldJournalTheRoundTrip_WhenALongClosesForAProfit()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 2, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 2, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        List<Trade> trades = await TradesAsync();
        trades.Should().ContainSingle();
        Trade trade = trades[0];
        trade.Side.Should().Be(OrderSide.Buy);
        trade.EntryPrice.Should().Be(5_000m);
        trade.ExitPrice.Should().Be(5_010m);
        trade.Size.Should().Be(2);
        // 10 points * 2 contracts * $5 point value = $100.
        trade.RealizedPnL.Should().Be(100m);
        trade.ClosedAt.Should().Be(_now.AddMinutes(5));
        trade.AccountId.Should().Be(accountId);
        trade.UserId.Should().Be(owner);
        trade.ClosingFillId.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldJournalTheEntry_WhenItsOrderWasCancelledAfterPartiallyFilling()
    {
        // gh#734 review. Selecting orders by their CURRENT status drops venue-truth fills: ingestion deliberately
        // moves a partially-filled order to Cancelled when the remainder is cancelled or rejected, and its Fill rows
        // still stand — the executions happened and the money is real. Filtering on Filled/PartiallyFilled therefore
        // lost the entry leg entirely, so the round trip could never balance and was never journalled. Presence of
        // FILLS is what proves an order executed, not the status the order ended up in.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");

        // A 1-of-2 entry: one lot filled, the remainder cancelled, so the order rests Cancelled with a real fill.
        await SeedFilledOrderAsync(
            owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)], status: OrderStatus.Cancelled);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        List<Trade> trades = await TradesAsync();
        trades.Should().ContainSingle(
            "the entry executed — a cancelled remainder does not un-fill the lot that traded, and the journal reads "
            + "venue truth (the Fill rows), not the order's final status");
        trades[0].EntryPrice.Should().Be(5_000m);
        trades[0].Size.Should().Be(1, "one lot filled before the remainder was cancelled");
        trades[0].RealizedPnL.Should().Be(50m, "10 points * 1 contract * $5");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldJournalTheSecondRoundTrip_WhenTheContractIsTradedAgainLater()
    {
        // gh#734 review, blocking finding. A journalled trade records only its CLOSING fill, so an "already
        // journalled?" filter keyed on that removes exactly ONE fill per written trade -- the entry fill of every
        // earlier round trip stays in the candidate set forever. Trip 2 is then composed from {A(buy), C(buy),
        // D(sell)}: two entries against one exit, which cannot balance, so it is refused and silently logged as a
        // scale-in. For a day-trading account, re-trading one contract is the COMMON case, and the whole point of
        // gh#731 is that DailyRealizedReader and the R-4 throttle stop reading zero -- which this would restore.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");

        // Round trip 1: in at 5,000, out at 5,010. Journalled on the first flat event.
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);
        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);
        (await TradesAsync()).Should().ContainSingle("the first round trip journals — the fixture's precondition");

        // Round trip 2 on the SAME account and contract, later in the session.
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_020m, 1, 10)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_030m, 1, 15)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        List<Trade> trades = await TradesAsync();
        trades.Should().HaveCount(
            2,
            "a second round trip on the same account+contract is journalled like any other — the first trip's fills "
            + "are spoken for and must not be re-composed into it");

        Trade second = trades.OrderBy(trade => trade.ClosedAt).Last();
        second.EntryPrice.Should().Be(5_020m, "the second trip is composed from ITS OWN fills, not the first's");
        second.ExitPrice.Should().Be(5_030m);
        second.Size.Should().Be(1, "a stale entry fill leaking in would size this at 2");
        second.RealizedPnL.Should().Be(50m, "10 points * 1 contract * $5 point value");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldJournalTheClosedTrip_WhenANewPositionReopenedBeforeTheFlatWasProcessed()
    {
        // gh#734 review. A flat event can be processed LATE -- after a new position has already reopened on the same
        // contract. Trip A (long, closed at :05) must still journal from the fills up to the event's OWN time.
        // Without an exit.At bound, CurrentCycleStart slices to the newer cycle that reopened at :06, hands the
        // composer only the still-open Buy, and trip A -- a real, completed round trip -- is lost forever.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");

        // Trip A: enter long at :00, close it at :05.
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)], placedMinute: 5);
        // A new position reopens at :06 -- BEFORE trip A's :05 flat event is processed.
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_020m, 1, 6)], placedMinute: 6);

        // Trip A's flat carries trip A's close time (:05), not "now".
        bool journalled = await Service().ProcessFlatAsync(
            Flat("9001", at: _now.AddMinutes(5)), CancellationToken.None);

        journalled.Should().BeTrue(
            "the round trip that closed at :05 must journal even though a new position reopened at :06 before this "
            + "flat event was processed -- the candidates are the fills up to the event's own time, not the newest cycle");
        Trade trade = (await TradesAsync()).Should().ContainSingle().Subject;
        trade.Side.Should().Be(OrderSide.Buy);
        trade.EntryPrice.Should().Be(5_000m);
        trade.ExitPrice.Should().Be(5_010m);
        trade.Size.Should().Be(1, "a fill from the reopened position leaking in would resize or unbalance this");
        trade.RealizedPnL.Should().Be(50m, "10 points * 1 contract * $5 point value");
        trade.ClosedAt.Should().Be(_now.AddMinutes(5));
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldJournalACleanTripAfterAComposedReverse_NotLeakItsFills()
    {
        // The round-trip boundary must advance past a COMPOSED cycle, not leak its fills into the next one. gh#759
        // replaces gh#731's refusal of the reverse with composition: a stop-and-reverse (Buy 1, Sell 2, Buy 1) now
        // composes TWO trips, and the later clean Buy 1 / Sell 1 must compose from ITS OWN fills alone -- three trades,
        // none resized by a leaked fill.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");

        // A stop-and-reverse: +1 (long), -2 (retires the long AND opens a short), +1 (retires the short). Two trips.
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)], placedMinute: 0);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 2, 5)], placedMinute: 5);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_020m, 1, 10)], placedMinute: 10);
        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);
        (await TradesAsync()).Should().HaveCount(2, "the reverse composes a long leg and a short leg -- the precondition");

        // A clean round trip afterwards, on the SAME account + contract.
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_030m, 1, 15)], placedMinute: 15);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_040m, 1, 20)], placedMinute: 20);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        List<Trade> trades = await TradesAsync();
        trades.Should().HaveCount(3, "the boundary advanced past the reverse's two legs; the clean trip is the third");
        Trade clean = trades.OrderBy(trade => trade.ClosedAt).Last();
        clean.EntryPrice.Should().Be(5_030m);
        clean.ExitPrice.Should().Be(5_040m);
        clean.Size.Should().Be(1, "a leaked reverse fill would unbalance or resize this");
        clean.RealizedPnL.Should().Be(50m, "10 points * 1 * $5");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldJournalOneTradePerLeg_WhenAScaleInIsExitedByASpanningFill()
    {
        // gh#759 acceptance: a scale-in (5 then 5) exited by a SINGLE 10-lot fill is TWO trades via allocation -- both
        // realized into the daily governor, so a scaled-in trade's P&L is no longer lost to a refusal (the hazard this
        // card closes). The spanning exit splits 5/5 across the two opening legs under FIFO.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 5, 0)], placedMinute: 0);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_010m, 5, 1)], placedMinute: 1);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_030m, 10, 5)], placedMinute: 5);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        List<Trade> trades = await TradesAsync();
        trades.Should().HaveCount(2, "one trade per opening leg -- the spanning 10-lot exit split 5/5 across them");
        trades.Should().OnlyContain(trade => trade.Size == 5 && trade.ExitPrice == 5_030m && trade.Side == OrderSide.Buy);
        trades.Select(trade => trade.EntryPrice).OrderBy(price => price).Should().Equal(5_000m, 5_010m);
        // First leg (5030-5000)*5*$5 = 750; second (5030-5010)*5*$5 = 500 -- the full 1,250 reaches the governor.
        trades.Sum(trade => trade.RealizedPnL).Should().Be(1_250m);
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldJournalBothDirectionsWithSignedPnL_WhenAPositionStopsAndReverses()
    {
        // gh#759 acceptance: a stop-and-reverse journals the closing leg's trade AND the opposite one, each with its
        // OWN signed realized P&L reaching the governor. Long 5 @ 5000, reversed by Sell 10 @ 5010 (closes the long
        // for +$250 and opens a short 5), covered Buy 5 @ 5030 (the short loses -$500). Both hit the daily governor.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 5, 0)], placedMinute: 0);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 10, 5)], placedMinute: 5);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_030m, 5, 10)], placedMinute: 10);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        List<Trade> trades = await TradesAsync();
        trades.Should().HaveCount(2);
        Trade longLeg = trades.Single(trade => trade.Side == OrderSide.Buy);
        longLeg.EntryPrice.Should().Be(5_000m);
        longLeg.ExitPrice.Should().Be(5_010m);
        longLeg.RealizedPnL.Should().Be(250m, "long 5 from 5000 to 5010 = +10 points * 5 * $5");
        Trade shortLeg = trades.Single(trade => trade.Side == OrderSide.Sell);
        shortLeg.EntryPrice.Should().Be(5_010m, "the short opened at the reversing fill's price");
        shortLeg.ExitPrice.Should().Be(5_030m);
        shortLeg.RealizedPnL.Should().Be(-500m, "short 5 from 5010 covered at 5030 = -20 points * 5 * $5");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldStayAtTwoRows_WhenAMultiLegCycleIsReplayed()
    {
        // The composite key's load-bearing case (gh#759 review). A scale-in exited by a SPANNING fill writes TWO legs
        // that SHARE one ClosingFillId. A replayed flat must recompose the same two (ClosingFillId, OpeningFillId)
        // keys and write nothing new -- the per-leg pre-check + the composite unique index dedupe each leg, and the
        // re-pairing guard sees the same keys (a stable replay, not a re-pairing). Single-trip replay is covered
        // elsewhere; this pins the two-legs-one-closing-fill case the composite key exists for.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 5, 0)], placedMinute: 0);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_010m, 5, 1)], placedMinute: 1);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_030m, 10, 5)], placedMinute: 5);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);
        (await TradesAsync()).Should().HaveCount(2, "the scale-in exited by one spanning fill journals two legs");

        bool journalledAgain = await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        journalledAgain.Should().BeFalse("a replay writes nothing new");
        (await TradesAsync()).Should().HaveCount(2, "the two legs are deduped on the composite key -- no duplicate rows");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldSkipIdempotently_WhenReplayingAPreMigrationLegacyRow()
    {
        // gh#759 review (BLOCKING). A Trade journalled BEFORE this PR has OpeningFillId = NULL (the column did not
        // exist). Replaying its flat must be the ORDINARY idempotent skip -- recognized by the old ClosingFillId-alone
        // key -- NOT a false "re-pairing" refusal that pollutes JournalBoundaryMergeRefused, the metric this PR built
        // to be a trustworthy signal for GENUINE re-pairing. The legacy row (and its RealizedPnL) is untouched.
        IExecutionMetrics metrics = A.Fake<IExecutionMetrics>();
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid entryFill = new("00000000-0000-0000-0000-0000000000e1");
        Guid closeFill = new("00000000-0000-0000-0000-0000000000c1");
        await SeedOrderWithFillIdsAsync(owner, accountId, OrderSide.Buy, [(entryFill, 5_000m, 1, 0)]);
        await SeedOrderWithFillIdsAsync(owner, accountId, OrderSide.Sell, [(closeFill, 5_010m, 1, 5)]);

        // The pre-migration row: ClosingFillId set, OpeningFillId NULL -- the old single-key format.
        await SeedLegacyTradeAsync(owner, accountId, closeFill, realized: 50m);

        bool journalled = await Service(metrics)
            .ProcessFlatAsync(Flat("9001", at: _now.AddMinutes(5)), CancellationToken.None);

        journalled.Should().BeFalse("a replay of the legacy trip writes nothing new");
        (await TradesAsync()).Should().ContainSingle("the legacy row is recognized as already journalled, not re-paired");
        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalAlreadyJournalled)).MustHaveHappened();
        // A legacy row is the old-format version of the same trip, not a re-pairing.
        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalBoundaryMergeRefused))
            .MustNotHaveHappened();
    }

    // A pre-#759 Trade row: the natural key is ClosingFillId alone and OpeningFillId is NULL (the column did not exist).
    private async Task SeedLegacyTradeAsync(Guid owner, Guid accountId, Guid closingFillId, decimal realized)
    {
        await using TradingCopilotDbContext context = Context();
        context.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            AccountId = accountId,
            Instrument = Contract,
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_000m,
            ExitPrice = 5_010m,
            RealizedPnL = realized,
            Mode = TradingMode.Practice,
            ClosedAt = _now.AddMinutes(5),
            ClosingFillId = closingFillId,
            // OpeningFillId deliberately left NULL -- the pre-#759 single-key format.
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldSignThePnLByTheEntrySide_WhenAShortClosesForAProfit()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        Trade trade = (await TradesAsync()).Should().ContainSingle().Subject;
        trade.Side.Should().Be(OrderSide.Sell);
        // A short that bought back 10 points lower profits: 10 * 1 * $5 = $50.
        trade.RealizedPnL.Should().Be(50m);
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldRecordALoss_WhenTheLongExitsBelowItsEntry()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(4_990m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().ContainSingle().Subject.RealizedPnL.Should().Be(-50m);
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteExactlyOneTrade_WhenTheFlatEventIsReplayed()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);
        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        // The closing fill is the natural key -- a replay recomposes the same round trip and must not
        // double-count the day's realized P&L into the daily governor.
        (await TradesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldTakeTheModeFromTheOrder_NotTheAccountsCurrentDeclaration()
    {
        Guid owner = Guid.NewGuid();
        // The account now reads Live, but the round trip was placed under Practice -- the journal records the
        // mode of PLACEMENT, so practice results can never blend into live results (R-14).
        Guid accountId = await SeedAccountAsync(owner, "9001", mode: TradingMode.Live);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)], mode: TradingMode.Practice);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)], mode: TradingMode.Practice);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().ContainSingle().Subject.Mode.Should().Be(TradingMode.Practice);
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldCarryTheOriginatingSuggestion_WhenTheEntryCameFromOne()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid suggestionId = Guid.NewGuid();
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.Suggestions.Add(new Suggestion
            {
                Id = suggestionId,
                UserId = owner,
                AccountId = accountId,
                Instrument = Contract,
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_000m,
                StopPrice = 4_990m,
                TargetPrice = 5_010m,
                Mode = TradingMode.Practice,
                State = SuggestionState.Active,
                Rationale = "confluence",
                CitedIndicator = "EMA",
                CitedPeriod = 20,
                CitedResolutionMinutes = 5,
                Confidence = 70,
                CreatedAt = _now,
                ExpiresAt = _now.AddMinutes(15),
            });
            await seed.SaveChangesAsync();
        }

        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)], suggestionId: suggestionId);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().ContainSingle().Subject.SuggestionId.Should().Be(suggestionId);
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldAttributeToTheFillingOrder_NotAnEarlierZeroFillOrderOnTheEntrySide()
    {
        // gh#734 review. `orders` is deliberately unfiltered by status (venue truth), so it also holds zero-fill
        // Rejected/Cancelled orders on the entry side — a risk-gate rejection, or a resting order cancelled before
        // any execution. Deriving the entry order by earliest PlacedAt among ALL entry-side orders can therefore
        // land on one that NEVER filled, inheriting its Mode / SuggestionId / PointValue. The entry order must be the
        // one that actually produced the opening fill.
        //
        // Here: a Practice buy is rejected at the open (zero fills), then a Live buy actually fills and the trip
        // closes. Attributing the trade to the earlier rejected Practice order would journal a LIVE round trip as
        // Practice — invisible to the live account's daily governor (R-14) — and compute its P&L off the wrong point
        // value.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001", mode: TradingMode.Live);
        Guid rejectedSuggestion = Guid.NewGuid();

        // The earlier, zero-fill rejected entry-side order: Practice, its own suggestion, a wrong point value.
        await SeedZeroFillOrderAsync(
            owner, accountId, OrderSide.Buy, OrderStatus.Rejected,
            placedMinute: 0, pointValue: 999m, mode: TradingMode.Practice, suggestionId: rejectedSuggestion);
        // The real entry: a Live buy that actually fills, placed later.
        await SeedFilledOrderAsync(
            owner, accountId, OrderSide.Buy, [(5_000m, 1, 5)], mode: TradingMode.Live, placedMinute: 5);
        await SeedFilledOrderAsync(
            owner, accountId, OrderSide.Sell, [(5_010m, 1, 10)], mode: TradingMode.Live, placedMinute: 10);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        Trade trade = (await TradesAsync()).Should().ContainSingle().Subject;
        trade.Mode.Should().Be(
            TradingMode.Live,
            "the round trip is composed from the Live order that actually filled, not the earlier rejected one");
        trade.SuggestionId.Should().NotBe(
            rejectedSuggestion, "the rejected zero-fill order never entered this round trip");
        trade.RealizedPnL.Should().Be(
            50m, "10 points * 1 * $5 from the FILLING order's point value, not the rejected order's 999");
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteNothing_WhenTheLegsDoNotBalance()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        // 3 in, 2 out -- a partial exit. Not the foundational round trip; refused rather than mis-journalled.
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 3, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 2, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteNothing_WhenThePositionIsNotFlat()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        PositionEvent stillOpen = Flat("9001") with { NetQuantity = 1 };
        await Service().ProcessFlatAsync(stillOpen, CancellationToken.None);

        (await TradesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteNothing_WhenTheAccountBelongsToAnotherCredentialSet()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001", credentialKey: "someone-elses");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteNothing_WhenNoOrderOfOursIsJournalledOnTheContract()
    {
        Guid owner = Guid.NewGuid();
        await SeedAccountAsync(owner, "9001");

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldOnlyConsiderTheFlatContract_WhenTheAccountTradedAnother()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        // A different contract goes flat -- the MES round trip above must not be journalled by it.
        await Service().ProcessFlatAsync(Flat("9001", "CON.F.US.NQ.U26"), CancellationToken.None);

        (await TradesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldThrow_WhenTheEventIsNull()
    {
        Func<Task> act = () => Service().ProcessFlatAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ----- gh#747: the write-fault narrowing predicate -----
    // The idempotent skip must fire for the IX_Trades_ClosingFillId unique violation ONLY. Any other write fault -- a
    // CHECK, an FK, a serialization failure, a lost connection -- is a real failure that must PROPAGATE (the outer host
    // logs it), never be swallowed as a benign replay: a silently-dropped Trade under-reports the day's realized P&L
    // into the daily governor. The in-memory provider never throws a PostgresException, so the narrowed filter is
    // exercised here on the pure predicate against constructed faults; the end-to-end propagation on real Postgres is
    // QA's remit (a container-tier test that induces a genuine non-uniqueness fault and asserts it surfaces).

    // Npgsql surfaces a PostgreSQL error as the inner exception of the DbUpdateException EF wraps its SaveChanges in.
    private static DbUpdateException PostgresWriteFault(string sqlState, string? constraintName) =>
        new("saving the round trip failed", new PostgresException(
            messageText: "write rejected", severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState,
            constraintName: constraintName));

    [Fact]
    public void IsTradeNaturalKeyViolation_ShouldBeTrue_ForTheClosingFillUniqueViolation() =>
        TradeJournalService.IsTradeNaturalKeyViolation(
            PostgresWriteFault(PostgresErrorCodes.UniqueViolation, TradeJournalService.TradeNaturalKeyIndex))
            .Should().BeTrue("the closing-fill unique violation IS the idempotent replay this writer expects");

    [Fact]
    public void IsTradeNaturalKeyViolation_ShouldBeFalse_ForAnotherIndexsUniqueViolation() =>
        // A unique violation on some OTHER index is not this writer's idempotent replay -- it must propagate.
        TradeJournalService.IsTradeNaturalKeyViolation(
            PostgresWriteFault(PostgresErrorCodes.UniqueViolation, "IX_Trades_Some_Other_Unique"))
            .Should().BeFalse("only the ClosingFillId index's violation is the expected dupe");

    [Fact]
    public void IsTradeNaturalKeyViolation_ShouldBeFalse_ForAUniqueViolationWithNoConstraintName() =>
        // A 23505 whose ConstraintName is null cannot be attributed to OUR index -- fail safe: propagate, don't skip.
        TradeJournalService.IsTradeNaturalKeyViolation(
            PostgresWriteFault(PostgresErrorCodes.UniqueViolation, constraintName: null))
            .Should().BeFalse("a unique violation with no constraint name is not identifiably the closing-fill dupe");

    [Fact]
    public void IsTradeNaturalKeyViolation_ShouldBeFalse_ForANonUniqueWriteFault() =>
        // A CHECK violation is a REAL failure, not the idempotent dupe -- it must propagate, not be swallowed.
        TradeJournalService.IsTradeNaturalKeyViolation(
            PostgresWriteFault(PostgresErrorCodes.CheckViolation, "CK_Trades_Size_Positive"))
            .Should().BeFalse("a CHECK violation is a real write failure the writer must not skip");

    [Fact]
    public void IsTradeNaturalKeyViolation_ShouldBeFalse_ForAForeignKeyViolation() =>
        TradeJournalService.IsTradeNaturalKeyViolation(
            PostgresWriteFault(PostgresErrorCodes.ForeignKeyViolation, "FK_Trades_Accounts_AccountId"))
            .Should().BeFalse("an FK violation is a real write failure, not the idempotent dupe");

    [Fact]
    public void IsTradeNaturalKeyViolation_ShouldBeFalse_WhenTheInnerIsNotAPostgresException() =>
        // A provider/transport fault that is not a PostgresException (and the in-memory provider's bare
        // DbUpdateException) is never the dupe.
        TradeJournalService.IsTradeNaturalKeyViolation(
            new DbUpdateException("save failed", new InvalidOperationException("not a postgres error")))
            .Should().BeFalse();

    [Fact]
    public void IsTradeNaturalKeyViolation_ShouldBeFalse_WhenThereIsNoInnerException() =>
        TradeJournalService.IsTradeNaturalKeyViolation(new DbUpdateException("save failed"))
            .Should().BeFalse();

    // ----- gh#747: the narrowed catch, wired end-to-end (in-memory) -----
    // The pure predicate above is only half the fix; these prove the CATCH that consumes it, so a mutation that keeps
    // the predicate but reverts the catch to swallow-all is caught here (gh#747 review). The writer news its own
    // context from the injected options, leaving no SaveChanges seam to mock -- but an EF interceptor on those options
    // can fault the writer's Trade insert (and ONLY that, so seeding still saves), which drives both catch arms
    // in-memory. The real-Postgres equivalent (a genuine venue fault vs a genuine unique race) is QA's remit.

    /// <summary>
    /// Faults the first <c>SaveChanges</c> that stages a new <see cref="Trade"/> with <paramref name="fault"/> — so an
    /// account/order/fill seed still saves, but the writer's own insert throws exactly the fault under test.
    /// <paramref name="beforeThrow"/>, when given, runs immediately before the throw — late enough that any earlier
    /// cancellation check EF performs on the way into this callback has already passed, so a test can cancel the
    /// token from inside it and be certain the catch's <c>when</c> filter is what observes the cancellation, not some
    /// earlier EF Core check aborting the save before the fault-under-test is ever thrown.
    /// </summary>
    private sealed class ThrowOnTradeInsertInterceptor(Exception fault, Action? beforeThrow = null) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            bool insertingTrade = eventData.Context!.ChangeTracker
                .Entries<Trade>().Any(entry => entry.State == EntityState.Added);
            if (!insertingTrade)
            {
                return base.SavingChangesAsync(eventData, result, cancellationToken);
            }

            beforeThrow?.Invoke();
            throw fault;
        }
    }

    private TradeJournalService ServiceWhoseTradeInsertFaultsWith(
        Exception fault, IExecutionMetrics metrics, Action? beforeThrow = null) => new(
        Context(),
        new DbContextOptionsBuilder<TradingCopilotDbContext>()
            .UseInMemoryDatabase(_database)
            .AddInterceptors(new ThrowOnTradeInsertInterceptor(fault, beforeThrow))
            .Options,
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = OurCredentialKey }),
        metrics,
        NullLogger<TradeJournalService>.Instance);

    [Fact]
    public async Task ProcessFlatAsync_ShouldRecordTheWriteFailureAndRethrow_WhenTheInsertFaultsForANonDuplicateReason()
    {
        // The whole safety point of gh#747: a REAL write fault (here a CHECK violation) must NOT be swallowed as a
        // benign idempotent skip. It records JournalWriteFailed and PROPAGATES, so the host logs it and the day's P&L
        // under-reports VISIBLY rather than a Trade vanishing silently. Before gh#747 the bare catch returned false here.
        IExecutionMetrics metrics = A.Fake<IExecutionMetrics>();
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        DbUpdateException realFault = PostgresWriteFault(PostgresErrorCodes.CheckViolation, "CK_Trades_Size_Positive");
        Func<Task> act = () => ServiceWhoseTradeInsertFaultsWith(realFault, metrics)
            .ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await act.Should().ThrowAsync<DbUpdateException>())
            .Which.Should().BeSameAs(realFault, "the real fault propagates unchanged, not swallowed as a skip");
        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalWriteFailed))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalWritten)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldPropagateWithoutRecordingTheWriteFailure_WhenTheTokenIsAlreadyCancelled()
    {
        // The `when (!IsCancellationRequested)` guard on the write-fault catch (gh#747 review): a shutdown-flavored
        // cancellation that Npgsql can surface as a wrapped DbUpdateException (57014) must not inflate the
        // JournalWriteFailed metric on an ordinary graceful stop. The SAME fault type as the sibling test above
        // proves the guard's only variable is cancellation, not the fault itself -- the token is cancelled from
        // INSIDE the interceptor, at the instant the fault is thrown, so SaveChanges itself starts uncancelled and
        // only becomes cancelled exactly where the guard reads it. The fault still propagates uncaught either way;
        // this guard narrows only the metric, not the propagation (the host's own log behavior on this path is a
        // separate, documented gap -- see the guard's comment in TradeJournalService.cs).
        IExecutionMetrics metrics = A.Fake<IExecutionMetrics>();
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        DbUpdateException shutdownFault = PostgresWriteFault(PostgresErrorCodes.CheckViolation, "CK_Trades_Size_Positive");
        using CancellationTokenSource cancelOnFault = new();
        Func<Task> act = () => ServiceWhoseTradeInsertFaultsWith(shutdownFault, metrics, beforeThrow: cancelOnFault.Cancel)
            .ProcessFlatAsync(Flat("9001"), cancelOnFault.Token);

        (await act.Should().ThrowAsync<DbUpdateException>())
            .Which.Should().BeSameAs(shutdownFault, "the fault still propagates -- cancellation only suppresses the metric");
        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalWriteFailed)).MustNotHaveHappened();
        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalWritten)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldSkipIdempotently_WhenTheInsertHitsTheClosingFillUniqueViolation()
    {
        // The other catch arm: a concurrent writer that journalled this exact round trip first makes our insert hit
        // the IX_Trades_ClosingFillId unique index. That -- and only that -- is the benign replay: return false, record
        // JournalDuplicateRejected, do NOT rethrow. This drives the CATCH, which the in-memory alreadyJournalled
        // pre-check cannot (no Trade row exists in this fresh context).
        IExecutionMetrics metrics = A.Fake<IExecutionMetrics>();
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        DbUpdateException dupe = PostgresWriteFault(
            PostgresErrorCodes.UniqueViolation, TradeJournalService.TradeNaturalKeyIndex);
        bool journalled = await ServiceWhoseTradeInsertFaultsWith(dupe, metrics)
            .ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        journalled.Should().BeFalse("the concurrent winner already journalled this round trip");
        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalDuplicateRejected))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => metrics.RecordTradeJournalOutcome(ExecutionMetrics.JournalWriteFailed)).MustNotHaveHappened();
    }

    [Fact]
    public void TradeNaturalKeyIndex_ShouldMatchTheModelsRealIndexName_SoTheNarrowedCatchNeverSilentlyStopsMatching()
    {
        // gh#747 review. The predicate keys its idempotent-skip filter on the literal index name -- a third copy of a
        // name EF derives by CONVENTION (the DbContext sets no HasDatabaseName) and the migration emits, with nothing
        // linking the three at compile time. A rename, or an added HasDatabaseName, would silently stop the filter
        // matching, demoting the catch-side idempotency backstop and firing a false "under-report" alarm on every
        // concurrent replay. Pin the constant to the model's real relational name -- InMemory ignores indexes, so build
        // the model against Npgsql (offline, never connecting; UseVector so the model builds, as the schema tests do).
        using TradingCopilotDbContext relational = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseNpgsql("Host=not-connected;Database=model-only", npgsql => npgsql.UseVector())
                .Options,
            new FixedUser(Guid.Empty));

        string indexName = relational.Model.FindEntityType(typeof(Trade))!
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(Trade.ClosingFillId), nameof(Trade.OpeningFillId)]))
            .GetDatabaseName()!;

        indexName.Should().Be(
            TradeJournalService.TradeNaturalKeyIndex,
            "IsTradeNaturalKeyViolation matches on this exact name; if the model's index name drifts from the "
            + "constant, the narrowed catch stops recognising the idempotent replay");
    }
}
