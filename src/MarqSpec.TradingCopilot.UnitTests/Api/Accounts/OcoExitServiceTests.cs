using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Accounts;

/// <summary>
/// App-level OCO-cancel-on-exit (gh#183): when a position goes flat, retire its synthetic stop plans and cancel
/// the dangling native legs. The safety-relevant behaviours: a <b>partial</b> fill (net non-zero) cancels nothing;
/// the operator's journaled resting entry is never cancelled; a replay is a no-op; a venue-rejected cancel is
/// benign; and an exit never crosses the R-20 boundary.
/// </summary>
public class OcoExitServiceTests
{
    private const string OurCredentialKey = "topstep-main";
    private const string Contract = "CON.F.US.MES.U26";
    private static VenueId Projectx { get; } = VenueId.Parse("projectx");
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly DateTimeOffset _now = new(2026, 1, 15, 14, 30, 0, TimeSpan.Zero);
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly IAuditLog _auditLog = A.Fake<IAuditLog>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public OcoExitServiceTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(Projectx);
        // Default: the venue confirms the contract is still flat (no open position) and lists no working orders.
        // Tests that exercise the re-open race or dangling legs override these.
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([]);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]);
    }

    private PositionSnapshot Position(string venueAccountKey, int netQuantity, string contract = Contract) =>
        new(VenueAccountId.Create(Projectx, venueAccountKey), VenueContractId.Create(Projectx, contract),
            netQuantity, new Price(5_300m));

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.Empty));

    private OcoExitService Service() => new(
        Context(),
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
        _factory,
        _auditLog,
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = OurCredentialKey }),
        NullLogger<OcoExitService>.Instance);

    private PositionEvent Exit(string venueAccountKey, int netQuantity, string contract = Contract) =>
        new(VenueAccountId.Create(Projectx, venueAccountKey), _now,
            VenueContractId.Create(Projectx, contract), netQuantity, new Price(5_300m));

    private static WorkingOrder Leg(string key, decimal? stop = null, string contract = Contract) =>
        new(key, VenueContractId.Create(Projectx, contract), stop is { } s ? new Price(s) : null, null, Size: 1);

    private async Task<Guid> SeedAccountAsync(Guid owner, string venueAccountKey, string credentialKey = OurCredentialKey)
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
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
            Balance = 50_000m,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    private async Task<Guid> SeedOrderWithStopPlanAsync(
        Guid owner, Guid accountId, string? venueOrderKey, OrderStatus status, StopStaging staging)
    {
        Guid orderId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = owner,
            AccountId = accountId,
            Instrument = Contract,
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Market,
            EntryPrice = 5_300m,
            WorkingStopPrice = 5_290m,
            SafetyStopPrice = 5_280m,
            Status = status,
            Mode = TradingMode.Practice,
            VenueOrderKey = venueOrderKey,
            PlacedAt = _now,
        });
        context.StopPlans.Add(new StopPlanRecord
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            OrderId = orderId,
            Side = OrderSide.Buy,
            EntryPrice = 5_300m,
            ActualStopPrice = 5_290m,
            SafetyStopPrice = 5_280m,
            ProximityMetric = StopProximityMetric.Ticks,
            ProximityValue = 4m,
            Staging = staging,
        });
        await context.SaveChangesAsync();
        return orderId;
    }

    private async Task<StopStaging> StagingAsync(Guid orderId) =>
        (await Context().StopPlans.IgnoreQueryFilters().SingleAsync(plan => plan.OrderId == orderId)).Staging;

    [Fact]
    public async Task ProcessExitAsync_ShouldDoNothing_WhenThePositionIsNotFlat()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([Leg("leg-safety", stop: 5_280m)]);

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 2), CancellationToken.None); // still 2 long -- NOT flat

        (await StagingAsync(orderId)).Should().Be(StopStaging.Native); // protection untouched -- the position is live
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldRetireStopPlans_AndCancelDanglingLegs_WhenFlat()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([Leg("leg-safety", stop: 5_280m), Leg("leg-actual", stop: 5_290m)]);

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None);

        (await StagingAsync(orderId)).Should().Be(StopStaging.Retired); // synthetic plan retired terminally
        VenueAccountId account = VenueAccountId.Create(Projectx, "9001");
        A.CallTo(() => _venue.CancelOrderAsync(account, "leg-safety", A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _venue.CancelOrderAsync(account, "leg-actual", A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldNeverCancelAJournaledRestingEntry()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        // A resting ENTRY the operator placed (journaled, Working, venue key set) -- must survive the exit.
        await SeedOrderWithStopPlanAsync(owner, accountId, "resting-entry", OrderStatus.Working, StopStaging.Hidden);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([Leg("resting-entry", stop: 5_250m), Leg("leg-safety", stop: 5_280m)]);

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None);

        VenueAccountId account = VenueAccountId.Create(Projectx, "9001");
        A.CallTo(() => _venue.CancelOrderAsync(account, "resting-entry", A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _venue.CancelOrderAsync(account, "leg-safety", A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldBeIdempotent_OnAReplayedExit()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);

        // First exit lists the leg (and cancels it); the replay falls through to the ctor default of none (the leg
        // is already gone). A most-recently-configured .Once() rule matches first, then the default takes over.
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([Leg("leg-safety", stop: 5_280m)]).Once();

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None);
        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None); // replay

        (await StagingAsync(orderId)).Should().Be(StopStaging.Retired);
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, "leg-safety", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly(); // the replay cancels nothing new
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldContinue_WhenAVenueCancelIsRejected()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([Leg("leg-gone", stop: 5_280m), Leg("leg-actual", stop: 5_290m)]);
        VenueAccountId account = VenueAccountId.Create(Projectx, "9001");
        A.CallTo(() => _venue.CancelOrderAsync(account, "leg-gone", A<CancellationToken>._))
            .Throws(new InvalidOperationException("order already gone")); // the venue's own OCO won the race

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None); // must not throw

        (await StagingAsync(orderId)).Should().Be(StopStaging.Retired); // still retired
        A.CallTo(() => _venue.CancelOrderAsync(account, "leg-actual", A<CancellationToken>._)).MustHaveHappened(); // other leg still cancelled
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldRetirePlans_ButSkipNativeCancel_WhenVenueCannotListWorkingOrders()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new NotSupportedException("cannot list"));

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None); // must not throw

        (await StagingAsync(orderId)).Should().Be(StopStaging.Retired); // the synthetic plan is still retired
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldNotCrossTheR20Boundary()
    {
        Guid operatorA = Guid.NewGuid();
        Guid operatorB = Guid.NewGuid();
        Guid accountA = await SeedAccountAsync(operatorA, "9001");
        Guid accountB = await SeedAccountAsync(operatorB, "9002");
        Guid orderA = await SeedOrderWithStopPlanAsync(operatorA, accountA, "entry-a", OrderStatus.Filled, StopStaging.Native);
        Guid orderB = await SeedOrderWithStopPlanAsync(operatorB, accountB, "entry-b", OrderStatus.Filled, StopStaging.Native);

        await Service().ProcessExitAsync(Exit("9002", netQuantity: 0), CancellationToken.None); // B's account went flat

        (await StagingAsync(orderB)).Should().Be(StopStaging.Retired); // B's plan retired
        (await StagingAsync(orderA)).Should().Be(StopStaging.Native);  // A's plan untouched
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldIgnore_AnAccountThisProcessDoesNotServe()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001", credentialKey: "another-firm");
        Guid orderId = await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None);

        (await StagingAsync(orderId)).Should().Be(StopStaging.Native); // not ours -- untouched
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldLeaveAllProtection_WhenTheContractReOpenedByProcessingTime()
    {
        // The critical stale-event re-open race: a flat event arrives, but by processing time the venue reports a
        // LIVE position on the contract (a fast re-entry / stop-and-reverse, or a replayed event). Nothing may be
        // touched -- the resting legs now protect the NEW position.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([Position("9001", netQuantity: -1)]); // re-opened short 1
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([Leg("leg-safety", stop: 5_280m)]);

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None);

        (await StagingAsync(orderId)).Should().Be(StopStaging.Native); // plan NOT retired
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldLeaveAllProtection_WhenTheVenueCannotConfirmFlat()
    {
        // Cannot confirm flatness (venue unreachable) -- do NOT act on an unconfirmed exit; leave protection.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None); // must not throw

        (await StagingAsync(orderId)).Should().Be(StopStaging.Native); // unconfirmed -> nothing touched
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldPreserveAPartiallyFilledEntry()
    {
        // A stop-and-reverse whose reversal entry is PARTIALLY filled still has a live resting remainder at the
        // venue -- it is the operator's own journaled order, not dangling protection, and must never be cancelled.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedOrderWithStopPlanAsync(owner, accountId, "reversal", OrderStatus.PartiallyFilled, StopStaging.Hidden);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([Leg("reversal", stop: 5_250m), Leg("leg-safety", stop: 5_280m)]);

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None);

        VenueAccountId account = VenueAccountId.Create(Projectx, "9001");
        A.CallTo(() => _venue.CancelOrderAsync(account, "reversal", A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _venue.CancelOrderAsync(account, "leg-safety", A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldRetirePlans_ButSkipNativeCancel_WhenListingWorkingOrdersFailsTransiently()
    {
        // A transient list failure (gateway 5xx), not NotSupportedException -- still handled loudly, never fatal.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("gateway 503"));

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None); // must not throw

        (await StagingAsync(orderId)).Should().Be(StopStaging.Retired); // flat confirmed -> plan retired
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldAuditTheRetirement()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None);

        // An immutable AuditRecord is written for the retired plan (gh#220) -- owned, a position-exit action, and
        // NOT synthetic_risk (the position is flat, so no live exposure rested on platform-held protection).
        A.CallTo(() => _auditLog.WriteAsync(
            A<IReadOnlyCollection<AuditRecord>>.That.Matches(rows => rows.Any(record =>
                record.Action == AuditAction.PositionExit
                && record.After == nameof(StopStaging.Retired)
                && !record.SyntheticRisk
                && record.UserId == owner)),
            A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task ProcessExitAsync_ShouldStillRetireThePlan_WhenTheAuditWriteFails()
    {
        // The audit is a SECONDARY write: its failure must never undo the safety action.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderWithStopPlanAsync(owner, accountId, "entry-1", OrderStatus.Filled, StopStaging.Native);
        A.CallTo(() => _auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("audit store down"));

        await Service().ProcessExitAsync(Exit("9001", netQuantity: 0), CancellationToken.None); // must not throw

        (await StagingAsync(orderId)).Should().Be(StopStaging.Retired); // the retire stands
    }
}
