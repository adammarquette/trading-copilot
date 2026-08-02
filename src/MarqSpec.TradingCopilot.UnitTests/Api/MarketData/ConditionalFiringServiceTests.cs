using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The conditional-order firing watcher's core (gh#198, ADR-0007): on a quote, fire the pending conditionals the
/// trigger crossed — through the authoritative fire-time re-gate — and cancel/expire the stale ones. The
/// safety-relevant behaviours: a fired order goes through the gate and journals its order + stop plan; a
/// gate-refused fire stays pending (never lost); a resolved order never re-fires; and nothing fires without its
/// trigger.
/// </summary>
public class ConditionalFiringServiceTests
{
    private const string Contract = "CON.F.US.MES.U26";
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly VenueAccountId _venueAccount = VenueAccountId.Create(VenueId.Parse("projectx"), "9001");
    private static DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch.AddYears(56);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public ConditionalFiringServiceTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders));
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([]); // flat
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId instrument, CancellationToken _) => Task.FromResult(
                new ResolvedContract(VenueContractId.Create(VenueId.Parse("projectx"), Contract), instrument)));
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));
    }

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context() => new(Options, new FixedUser(_operator));

    private ConditionalFiringService Service(ILogger<ConditionalFiringService>? logger = null) => new(
        Context(),
        Options,
        _factory,
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
        Microsoft.Extensions.Options.Options.Create(new ExecutionOptions()),
        new HostTradingEnvironment(DeploymentEnvironment.Development),
        A.Fake<IKillSwitch>(),
        logger ?? NullLogger<ConditionalFiringService>.Instance);

    private async Task<Guid> SeedAccountAsync()
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm
        {
            Id = firmId,
            UserId = _operator,
            Name = "Topstep",
            Type = FirmType.PropFirm,
            StageConventions =
            [
                new FirmStageConvention { Id = Guid.NewGuid(), UserId = _operator, FirmId = firmId, Stage = AccountStage.Practice, CapitalAtRisk = false },
            ],
        });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = "topstep-main",
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
            Balance = 50_000m,
        });
        context.RiskProfiles.Add(new RiskProfileRecord
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            AccountId = accountId,
            StartingBalance = 50_000m,
            FloorSource = FloorSource.FirmImposed,
            TrailingMode = TrailingMode.EndOfDay,
            TrailingAmount = 2_000m,
            PerTradeRiskFraction = 0.15m,
            TargetRewardRatio = 1.5m,
            MaxDrawdownPerTrade = 300m,
            DailyDrawdownGovernor = 600m,
            SizingBasis = SizingBasis.SafetyStop,
            MaxContractsPerOrder = 3,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    private async Task<Guid> AddConditionalAsync(
        Guid accountId,
        ConditionalCrossDirection direction = ConditionalCrossDirection.RisesTo,
        decimal trigger = 5310m,
        decimal safetyStop = 5290m,
        int size = 1,
        decimal? cancelDrift = null,
        DateTimeOffset? expiresAt = null,
        ConditionalStatus status = ConditionalStatus.Pending)
    {
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.ConditionalOrders.Add(new ConditionalOrderRecord
        {
            Id = id,
            UserId = _operator,
            AccountId = accountId,
            Instrument = Contract,
            Symbol = "MES",
            Side = OrderSide.Buy,
            Size = size,
            Type = OrderType.Market,
            EntryPrice = 5300m,
            WorkingStopPrice = 5295m,
            SafetyStopPrice = safetyStop,
            ReferencePrice = 5300m,
            TickSize = 0.25m,
            PointValue = 5m,
            TriggerPrice = trigger,
            TriggerDirection = direction,
            CancelDriftPrice = cancelDrift,
            ExpiresAt = expiresAt,
            Status = status,
            Mode = TradingMode.Practice,
            CreatedAt = Now,
        });
        await context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ProcessQuote_ShouldFireThroughTheGate_AndJournalTheOrderAndStopPlan_WhenTheTriggerCrossed()
    {
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await AddConditionalAsync(accountId); // RisesTo 5310

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        acted.Should().Be(1);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext reload = Context();
        ConditionalOrderRecord conditional = await reload.ConditionalOrders.SingleAsync(c => c.Id == conditionalId);
        conditional.Status.Should().Be(ConditionalStatus.Fired);
        conditional.FiredOrderId.Should().NotBeNull();

        Order journaled = await reload.Orders.SingleAsync();
        journaled.Id.Should().Be(conditional.FiredOrderId!.Value);
        journaled.Status.Should().Be(OrderStatus.Working);
        journaled.VenueOrderKey.Should().Be("889001");
        journaled.UserId.Should().Be(_operator);                        // ownership preserved on the journaled row
        journaled.EntryMethod.Should().Be(OrderEntryMethod.Conditional); // placed by the on-trigger watcher (gh#181)
        (await reload.StopPlans.CountAsync()).Should().Be(1);           // the fired position is protected (stop staged)
        (await reload.GateDecisions.CountAsync()).Should().Be(1);       // the fire-time decision is audited
    }

    [Fact]
    public async Task ProcessQuote_ShouldNotFire_WhenTheTriggerHasNotCrossed()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId); // RisesTo 5310

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5304m, ask: 5305m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Pending);
    }

    [Fact]
    public async Task ProcessQuote_ShouldCancel_OnAnAdverseDriftPastTheBand()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, cancelDrift: 5290m); // RisesTo 5310; stale below 5290

        // Ask 5290 is at the band and below the 5310 trigger: a drift-cancel, not a fire.
        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5289m, ask: 5290m, Now, CancellationToken.None);

        acted.Should().Be(1);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Cancelled);
    }

    [Fact]
    public async Task ProcessQuote_ShouldExpire_WhenTheValidityWindowHasPassed()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, expiresAt: Now.AddMinutes(-1)); // already expired

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5304m, ask: 5305m, Now, CancellationToken.None);

        acted.Should().Be(1);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Expired);
    }

    [Fact]
    public async Task ProcessQuote_ShouldIgnoreResolvedOrders_SoAFiredOneNeverRefires()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, status: ConditionalStatus.Fired); // already resolved

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5320m, ask: 5321m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Fired);
    }

    [Fact]
    public async Task ProcessQuote_ShouldStayPending_WhenTheFireTimeGateRefuses()
    {
        // The trigger crossed, but at fire time the gate refuses (a $500/contract safety stop against a $300
        // budget leaves room for zero) -- the setup is not lost; it re-decides on the next quote. R-12.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, safetyStop: 5200m); // 100pt = $500/contract vs the $300 MaxDD

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Pending);
        (await reload.Orders.AnyAsync()).Should().BeFalse();            // nothing transmitted or journaled
        (await reload.GateDecisions.CountAsync()).Should().Be(1);       // the blocking decision is audited
    }

    [Fact]
    public async Task ProcessQuote_ShouldJournalAnEarlierFire_WhenALaterConditionalOnTheSameContractThrows()
    {
        // gh#532 — the P1. Two pending conditionals on ONE contract cross on the same quote. The first fires and is
        // transmitted to the venue; transmitting the second then throws (an ordinary venue rejection). Under a
        // per-QUOTE unit of work (one SaveChanges after the whole batch) the second's escape discards the first's
        // already-transmitted order journal AND its Fired transition — leaving a live venue order recorded on no
        // Order row, which the next quote then re-fires. Each conditional must commit its own work before the next
        // is touched, so a peer's fault can never unwind an order the venue has already accepted.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId); // RisesTo 5310
        await AddConditionalAsync(accountId); // same contract, same crossing quote

        // The venue accepts the first placement and rejects the second. A faulted Task (not a synchronous throw) is
        // exactly how the send path surfaces it: `await venue.PlaceOrderAsync(...)` observes the fault.
        int placements = 0;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                placements++;
                return placements == 2
                    ? Task.FromException<PlacedOrder>(new InvalidOperationException("venue rejected the second order (test)"))
                    : Task.FromResult(new PlacedOrder(_venueAccount, $"88900{placements}", DateTimeOffset.UnixEpoch));
            });

        // The pass may surface the second record's fault (the batched-save shape throws it out; the per-record fix
        // contains it) — either way the FIRST fire must be durably journaled, which is the property under test.
        try
        {
            await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The later conditional's venue rejection escaping the pass — tolerated; the DB state is asserted below.
        }

        await using TradingCopilotDbContext reload = Context();
        // The safety property: an order the venue accepted is never discarded because a peer later threw. The first
        // placement succeeded, so exactly one Order row must exist, linked to a Fired conditional, protected by a stop.
        (await reload.Orders.CountAsync()).Should().Be(
            1, "the first fire's transmitted order must be journaled even though a later conditional threw");
        ConditionalOrderRecord fired = await reload.ConditionalOrders.SingleAsync(c => c.Status == ConditionalStatus.Fired);
        fired.FiredOrderId.Should().Be((await reload.Orders.SingleAsync()).Id);
        (await reload.StopPlans.CountAsync()).Should().Be(1, "the fired position's stop plan is journaled with it");
        // The conditional whose transmit threw is untouched — still Pending, so it re-decides on the next quote.
        (await reload.ConditionalOrders.CountAsync(c => c.Status == ConditionalStatus.Pending)).Should().Be(1);
    }

    [Fact]
    public async Task ProcessQuote_ShouldStillFireTheOtherConditionals_WhenOneOnTheSameContractThrows()
    {
        // gh#532 — containment. Three conditionals cross on one quote; the middle transmit throws. A per-quote batch
        // that lets the fault escape processes none of the survivors (and the host then re-reads the same event and
        // retry-storms the poison record). Per-record isolation fires the two good ones and leaves only the failed
        // one Pending, to re-decide on the next quote (ADR-0013's safe "did not fire" direction).
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId);
        await AddConditionalAsync(accountId);
        await AddConditionalAsync(accountId);

        int placements = 0;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                placements++;
                return placements == 2
                    ? Task.FromException<PlacedOrder>(new InvalidOperationException("venue rejected the second order (test)"))
                    : Task.FromResult(new PlacedOrder(_venueAccount, $"88900{placements}", DateTimeOffset.UnixEpoch));
            });

        try
        {
            await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Tolerated — the assertion is on how many survived, not on whether the fault escaped.
        }

        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.CountAsync()).Should().Be(
            2, "the two conditionals whose transmit succeeded are both fired and journaled, despite the middle one throwing");
        (await reload.ConditionalOrders.CountAsync(c => c.Status == ConditionalStatus.Fired)).Should().Be(2);
        (await reload.ConditionalOrders.CountAsync(c => c.Status == ConditionalStatus.Pending)).Should().Be(
            1, "only the conditional whose transmit threw is left pending");
    }

    [Fact]
    public async Task ProcessQuote_ShouldTagTheVenueOrderWithTheConditionalId_SoAReplayCanRecogniseItsOwnOrder()
    {
        // gh#577 — the correlation handle. A fired conditional stamps its own id as the venue order's customTag, so an
        // order the venue accepted but a transmit→journal fault never journaled can be matched back to the conditional
        // that placed it, rather than transmitting a blind duplicate. (Manual paths carry no tag — a human is in the loop.)
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await AddConditionalAsync(accountId); // RisesTo 5310

        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest order, CancellationToken _) => sent = order)
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));

        await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        sent.Should().NotBeNull();
        sent!.CustomTag.Should().Be(
            conditionalId.ToString(), "the venue order carries the firing conditional's id as its correlation handle (gh#577)");
    }

    [Fact]
    public async Task ProcessQuote_ShouldNotRefire_WhenAnEarlierFireWasAcceptedButItsJournalDidNotCommit()
    {
        // gh#577 — the P1 residual #532 left open. A fired conditional transmits the entry (the venue ACCEPTS it) and
        // then journals its order + stop plan on a commit that here FAILS (a DB fault / a shutdown cancellation landing
        // between the accept and the commit). Before this fix the conditional was left Pending with a live venue order
        // on no Order row, so the next crossing quote re-fired it — a duplicate live order. The durable pre-transmit
        // intent leaves it Firing instead, and discovery is Pending-only, so it can never blind-re-fire.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await AddConditionalAsync(accountId); // RisesTo 5310

        // The venue accepts, but the caller's token is cancelled at the moment of acceptance, so the journal SaveChanges
        // that follows the accept throws — reproducing the accepted-but-not-journaled window without an in-memory DB fault.
        using CancellationTokenSource cancelAtAccept = new();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                cancelAtAccept.Cancel();
                return new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch);
            });

        try
        {
            await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, cancelAtAccept.Token);
        }
        catch (OperationCanceledException)
        {
            // The transmit→journal window surfaced as a cancellation (the order may be live and unrecorded, gh#577) —
            // tolerated here; the durable state and the absence of a re-fire below carry the property under test.
        }

        await using (TradingCopilotDbContext afterFault = Context())
        {
            (await afterFault.ConditionalOrders.SingleAsync(c => c.Id == conditionalId)).Status.Should().Be(
                ConditionalStatus.Firing, "an accepted-but-unjournaled fire is left mid-firing, never back at Pending");
            (await afterFault.Orders.AnyAsync()).Should().BeFalse("the journal commit did not land");
        }

        // Second quote on the SAME cross: the mid-firing conditional must NOT re-fire — discovery is Pending-only.
        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        acted.Should().Be(0, "a mid-firing conditional is never re-decided from a quote");
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync(c => c.Id == conditionalId)).Status.Should().Be(ConditionalStatus.Firing);
        (await reload.Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessQuote_ShouldSurfaceLoudly_WhenAShutdownInterruptsTheSendBeforeTheFireIsJournaled()
    {
        // gh#577 review: the most dangerous window — a host shutdown landing WHILE the send is in flight, before the
        // venue's accept is observed — must not be silent. Unlike the accepted-then-journal-failed case, the send here
        // throws before returning Placed, so the old `transmitted` flag was never set and the operator got NO signal
        // until the next full restart. The loud log is now keyed off the durable Firing intent (committed before the
        // send), so this window is surfaced: the record is left Firing AND a LogError is emitted.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await AddConditionalAsync(accountId); // RisesTo 5310

        using CancellationTokenSource cancelDuringSend = new();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes(() => cancelDuringSend.Cancel())                                // the shutdown lands mid-send…
            .Throws(() => new OperationCanceledException(cancelDuringSend.Token));    // …and the send is interrupted, outcome unknown

        ILogger<ConditionalFiringService> logger = A.Fake<ILogger<ConditionalFiringService>>();
        try
        {
            await Service(logger).ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, cancelDuringSend.Token);
        }
        catch (OperationCanceledException)
        {
            // The interruption propagates to stop the pass (as at host shutdown); the loud log fires before it does.
        }

        // Left Firing so it can never blind-re-fire, and nothing journaled (the send never reached the accept)…
        await using TradingCopilotDbContext afterFault = Context();
        (await afterFault.ConditionalOrders.SingleAsync(c => c.Id == conditionalId)).Status.Should().Be(
            ConditionalStatus.Firing, "a send interrupted after the intent committed is left mid-firing, never back at Pending");
        (await afterFault.Orders.AnyAsync()).Should().BeFalse();

        // …and, the point of this fix: the operator got a loud signal, not silence, even though `transmitted` was never set.
        A.CallTo(logger)
            .Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Error)
            .MustHaveHappened();
    }

    [Fact]
    public async Task ProcessQuote_ShouldNotFireAMidFiringConditional_SoADiscardedJournalNeverReplaysAsADuplicate()
    {
        // gh#577 — the guard behind the fix. A conditional durably marked Firing (a fire whose journal did not commit)
        // is inert to the firing pass: like a resolved one it is never picked up by a crossing quote (discovery is
        // Pending-only), so a live-but-unjournaled order is reconciled / surfaced, never re-fired.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, status: ConditionalStatus.Firing);

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5320m, ask: 5321m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Firing);
    }
}
