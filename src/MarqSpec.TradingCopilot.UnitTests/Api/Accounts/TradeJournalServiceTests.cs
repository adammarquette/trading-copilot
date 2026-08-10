using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

    private TradeJournalService Service() => new(
        Context(),
        Options(),
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = OurCredentialKey }),
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

    private async Task<List<Trade>> TradesAsync() =>
        await Context().Trades.IgnoreQueryFilters().ToListAsync();

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
    public async Task ProcessFlatAsync_ShouldJournalACleanTripAfterARefusedOne_NotWedgeOnItsFillsForever()
    {
        // gh#734 review. The round-trip boundary must advance past a REFUSED flat, not only a journalled one. A
        // stop-and-reverse (Buy 1, Sell 2, Buy 1) ends flat but is refused — it crossed through flat into a short.
        // If its fills stay in the candidate set, the next clean Buy 1 / Sell 1 is recomposed WITH them, reads as
        // another reversal, and is refused too — journaling wedged for this account+contract for the rest of the
        // session, starving DailyRealizedReader and the R-4 throttle, the very readers gh#731 exists to feed.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");

        // A stop-and-reverse that ends flat and is refused: +1, then -1 (short), then 0.
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)], placedMinute: 0);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 2, 5)], placedMinute: 5);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_020m, 1, 10)], placedMinute: 10);
        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);
        (await TradesAsync()).Should().BeEmpty("the stop-and-reverse is refused — the fixture's precondition");

        // A clean round trip afterwards, on the SAME account + contract.
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_030m, 1, 15)], placedMinute: 15);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_040m, 1, 20)], placedMinute: 20);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        Trade trade = (await TradesAsync()).Should().ContainSingle(
            "the boundary advanced past the refused cycle, so the clean trip composes from its OWN fills").Subject;
        trade.EntryPrice.Should().Be(5_030m);
        trade.ExitPrice.Should().Be(5_040m);
        trade.Size.Should().Be(1, "a leaked stop-and-reverse fill would unbalance or resize this");
        trade.RealizedPnL.Should().Be(50m, "10 points * 1 * $5");
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
}
