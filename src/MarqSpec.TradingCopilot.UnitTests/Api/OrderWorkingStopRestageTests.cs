using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// Re-staging a working order's <b>hidden working stop</b> via <c>PATCH /orders/{id}/price</c> (gh#267). The
/// safety-relevant behaviours: a hidden stop is a promotion target, not a venue order, so a re-stage is a <b>local</b>
/// write (the venue is never touched); size and the safety stop stay invariant so every hard risk limit is preserved;
/// a re-stage re-gates <b>only</b> when it widens under <c>SizingBasis.ActualStop</c> (the one enforced layer measured
/// at the working stop); the strict safety→working→entry ordering is re-validated so the DB CHECK can never trip; a
/// promoted (Native) / orphaned / terminal plan is refused; and the move is audited.
/// </summary>
public class OrderWorkingStopRestageTests
{
    private const string OurCredentialKey = "topstep-main";
    private const string WorkingOrderKey = "PX-55001";

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly IAuditLog _auditLog = A.Fake<IAuditLog>();
    private readonly VenueAccountId _venueAccount = VenueAccountId.Create(VenueId.Parse("projectx"), "9001");

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public OrderWorkingStopRestageTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders));
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([]);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId instrument, CancellationToken _) => Task.FromResult(
                new ResolvedContract(VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26"), instrument)));
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.CompletedTask);
    }

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private static int StatusOf(IResult result) => result is IStatusCodeHttpResult status ? status.StatusCode ?? 0 : 0;

    private static IOptions<ProjectXConnectionOptions> PxOptions() =>
        Options.Create(new ProjectXConnectionOptions { CredentialKey = OurCredentialKey });

    private static IOptions<ExecutionOptions> ExecOptions() => Options.Create(new ExecutionOptions());

    private static HostTradingEnvironment Development { get; } = new(DeploymentEnvironment.Development);

    private static ModifyWorkingOrderRequest Restage(decimal workingStop) =>
        new(EntryPrice: null, WorkingStopPrice: workingStop);

    private Task<IResult> ModifyAsync(Guid orderId, ModifyWorkingOrderRequest request, Guid? asUser = null) =>
        OrderEndpoints.ModifyWorkingOrderPriceAsync(
            orderId, request, new FixedUser(asUser ?? _operator), Context(asUser), _factory, PxOptions(),
            ExecOptions(), Development, A.Fake<IKillSwitch>(), IOrderAckRecorder.None, _auditLog, NullLoggerFactory.Instance, CancellationToken.None);

    private async Task<Guid> SeedAsync(
        SizingBasis sizingBasis = SizingBasis.SafetyStop,
        StopStaging? staging = StopStaging.Hidden,
        decimal workingStop = 5_295m,
        decimal safetyStop = 5_290m,
        decimal entryPrice = 5_300m,
        decimal perTradeRiskFraction = 0.15m,
        OrderType type = OrderType.Limit,
        OrderStatus status = OrderStatus.Working,
        string? venueOrderKey = WorkingOrderKey,
        Guid? owner = null)
    {
        Guid user = owner ?? _operator;
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(user);
        context.Firms.Add(new Firm
        {
            Id = firmId,
            UserId = user,
            Name = "Topstep",
            Type = FirmType.PropFirm,
            StageConventions =
            [
                new FirmStageConvention { Id = Guid.NewGuid(), UserId = user, FirmId = firmId, Stage = AccountStage.Practice, CapitalAtRisk = false },
            ],
        });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = user,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = OurCredentialKey,
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = user,
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
            UserId = user,
            AccountId = accountId,
            StartingBalance = 50_000m,
            FloorSource = FloorSource.FirmImposed,
            TrailingMode = TrailingMode.EndOfDay,
            TrailingAmount = 2_000m,
            PerTradeRiskFraction = perTradeRiskFraction,
            TargetRewardRatio = 1.5m,
            MaxDrawdownPerTrade = 300m,
            DailyDrawdownGovernor = 600m,
            SizingBasis = sizingBasis,
            MaxContractsPerOrder = 10,
        });
        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = user,
            AccountId = accountId,
            Instrument = "CON.F.US.MES.U26",
            Symbol = "MES",
            Side = OrderSide.Buy,
            Size = 1,
            Type = type,
            EntryPrice = entryPrice,
            WorkingStopPrice = workingStop,
            SafetyStopPrice = safetyStop,
            ReferencePrice = entryPrice,
            LimitPrice = type == OrderType.Limit ? entryPrice : null,
            StopPrice = type is OrderType.Stop or OrderType.StopLimit ? workingStop : null,
            TickSize = 0.25m,
            PointValue = 5m,
            Status = status,
            Mode = TradingMode.Practice,
            EntryMethod = OrderEntryMethod.Manual,
            VenueOrderKey = venueOrderKey,
            PlacedAt = new DateTimeOffset(2026, 1, 15, 14, 30, 0, TimeSpan.Zero),
        });
        if (staging is { } stopStaging)
        {
            context.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = user,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = entryPrice,
                ActualStopPrice = workingStop,
                SafetyStopPrice = safetyStop,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 4m,
                Staging = stopStaging,
            });
        }

        await context.SaveChangesAsync();
        return orderId;
    }

    private async Task<Order> OrderAsync(Guid orderId) =>
        await Context().Orders.IgnoreQueryFilters().SingleAsync(order => order.Id == orderId);

    private async Task<List<StopPlanRecord>> PlansAsync(Guid orderId) =>
        await Context().StopPlans.IgnoreQueryFilters().Where(plan => plan.OrderId == orderId).ToListAsync();

    private void VenueUntouched()
    {
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    private void VenueNeverRead()
    {
        // A pure-local re-stage (tighten, or SafetyStop basis) runs no ComposeAsync, so no venue roster/positions read.
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- The local re-stage: a tighten moves the hidden stop with no venue interaction ---

    [Fact]
    public async Task Restage_ShouldMoveTheHiddenStopLocally_WhenTighteningAndUpdateThePlanAndAudit()
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m); // safety 5290, entry 5300

        IResult result = await ModifyAsync(orderId, Restage(5_297m)); // tighter (closer to entry)

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_297m);
        (await PlansAsync(orderId)).Single().ActualStopPrice.Should().Be(5_297m);
        VenueUntouched();
        VenueNeverRead(); // a tighten is provably safe -- no re-gate, no venue reads
        A.CallTo(() => _auditLog.WriteAsync(
            A<IReadOnlyCollection<AuditRecord>>.That.Matches(rows => rows.Any(r =>
                r.Action == AuditAction.OrderModified && !r.SyntheticRisk && r.UserId == _operator)),
            A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task Restage_ShouldNotTouchTheOrdersSizeOrSafetyStop()
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m);

        await ModifyAsync(orderId, Restage(5_297m));

        Order after = await OrderAsync(orderId);
        after.Size.Should().Be(1);                 // invariant
        after.SafetyStopPrice.Should().Be(5_290m); // invariant -- the catastrophic floor stands
        after.EntryPrice.Should().Be(5_300m);      // invariant -- this path never moves the entry
        after.Status.Should().Be(OrderStatus.Working);
    }

    [Fact]
    public async Task Restage_ShouldMoveTheJournalStopPriceInLockstep_ForAStopTypeOrder()
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m, type: OrderType.Stop);

        await ModifyAsync(orderId, Restage(5_297m));

        Order after = await OrderAsync(orderId);
        after.WorkingStopPrice.Should().Be(5_297m);
        after.StopPrice.Should().Be(5_297m); // the journal StopPrice tracks the working stop (ApplyProposal convention)
    }

    // --- Re-gate ONLY a widen under SizingBasis.ActualStop ---

    [Fact]
    public async Task Restage_ShouldRefuse422AndNotTouchVenue_WhenAWidenUnderActualStopExceedsPerTradeRisk()
    {
        // ActualStop sizes per-trade risk at the WORKING stop. Widening it (toward safety) at the resting size breaches
        // that enforced layer -- refused, and (being a hidden stop) nothing is transmitted regardless.
        Guid orderId = await SeedAsync(
            sizingBasis: SizingBasis.ActualStop, workingStop: 5_299m, safetyStop: 5_285m, perTradeRiskFraction: 0.03m);

        IResult result = await ModifyAsync(orderId, Restage(5_286m)); // widen 5299 -> 5286 (13 pts = $65 > $60 budget)

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_299m); // journal unchanged
        (await PlansAsync(orderId)).Single().ActualStopPrice.Should().Be(5_299m);
        VenueUntouched();
    }

    [Fact]
    public async Task Restage_ShouldAllowLocallyWithoutReGating_WhenAWidenIsUnderSizingBasisSafetyStop()
    {
        // The SAME widen, but SafetyStop basis: the working stop is absent from all gate math, so the move cannot
        // breach any limit -- it re-stages locally with NO re-gate (proven by the venue roster never being read).
        Guid orderId = await SeedAsync(
            sizingBasis: SizingBasis.SafetyStop, workingStop: 5_299m, safetyStop: 5_285m, perTradeRiskFraction: 0.03m);

        IResult result = await ModifyAsync(orderId, Restage(5_286m));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_286m);
        VenueNeverRead(); // no ComposeAsync -- a SafetyStop-basis move is provably safe
    }

    [Fact]
    public async Task Restage_ShouldJournalAGateDecision_WhenAWidenUnderActualStopPassesTheGate()
    {
        // A sized, gate-approved risk-increase must leave a GateDecisionRecord -- like every other gate path, and
        // symmetric with the refusal branch (gh#267 review): the audit trail carries approved widens, not only refused.
        Guid orderId = await SeedAsync(
            sizingBasis: SizingBasis.ActualStop, workingStop: 5_299m, safetyStop: 5_285m, perTradeRiskFraction: 0.15m);

        IResult result = await ModifyAsync(orderId, Restage(5_296m)); // widen 5299 -> 5296 ($20/ct, fits at size 1)

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_296m);
        List<GateDecisionRecord> decisions = await Context().GateDecisions
            .IgnoreQueryFilters().Where(d => d.OrderId == orderId).ToListAsync();
        decisions.Should().ContainSingle(d => d.Outcome == GateOutcome.Allowed);
    }

    // --- Create-if-stageable: a coincident-stop order gains a Hidden plan ---

    [Fact]
    public async Task Restage_ShouldCreateAHiddenPlan_WhenInstallingAWorkingStopOnACoincidentOrder()
    {
        // Placed with working == safety (a stop-less-staging send got NO plan). Installing a distinct working stop is a
        // tightening, so it stages a Hidden plan exactly as placement would have.
        Guid orderId = await SeedAsync(staging: null, workingStop: 5_290m, safetyStop: 5_290m); // coincident, no plan

        IResult result = await ModifyAsync(orderId, Restage(5_296m)); // safety 5290 < 5296 < entry 5300

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_296m);
        StopPlanRecord created = (await PlansAsync(orderId)).Single();
        created.Staging.Should().Be(StopStaging.Hidden);
        created.ActualStopPrice.Should().Be(5_296m);
        created.ProximityValue.Should().Be(new ExecutionOptions().StopPromotionTicks);
    }

    // --- Refuse the promoted / emergency / terminal plans ---

    [Theory]
    [InlineData(StopStaging.Native)]
    [InlineData(StopStaging.Orphaned)]
    [InlineData(StopStaging.Retired)]
    public async Task Restage_ShouldReturn409AndNotChangeAnything_WhenThePlanIsNotHidden(StopStaging staging)
    {
        Guid orderId = await SeedAsync(staging: staging, workingStop: 5_295m);

        IResult result = await ModifyAsync(orderId, Restage(5_297m));

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_295m);
        (await PlansAsync(orderId)).Single().ActualStopPrice.Should().Be(5_295m); // the promoted/orphaned plan untouched
        VenueUntouched();
    }

    // --- The strict ordering guard (the CK_StopPlans_SafetyBeyondActual pre-empt) ---

    [Fact]
    public async Task Restage_ShouldReturn422_WhenMovingTheWorkingStopOntoTheSafetyStop()
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m, safetyStop: 5_290m);

        IResult result = await ModifyAsync(orderId, Restage(5_290m)); // onto safety = remove the stop, out of scope

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_295m);
    }

    [Theory]
    [InlineData(5_289)] // beyond the safety stop (5290) -> would trip the DB CHECK
    [InlineData(5_301)] // through the entry (5300)
    public async Task Restage_ShouldReturn422_WhenTheWorkingStopLeavesTheSafetyToEntryChannel(int newStop)
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m, safetyStop: 5_290m, entryPrice: 5_300m);

        IResult result = await ModifyAsync(orderId, Restage(newStop));

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_295m);
        VenueUntouched();
    }

    // --- Tenancy + request shape ---

    [Fact]
    public async Task Restage_ShouldReturn404_ForAnotherOperatorsOrder()
    {
        Guid otherOperator = Guid.NewGuid();
        Guid orderId = await SeedAsync(workingStop: 5_295m, owner: otherOperator);

        IResult result = await ModifyAsync(orderId, Restage(5_297m)); // called as _operator (default)

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_295m);
    }

    // (Moving BOTH the entry and the working stop together landed in gh#278 -- covered by OrderCombinedModifyTests.)

    [Fact]
    public async Task Modify_ShouldReturn400_WhenNeitherPriceIsSupplied()
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m);

        IResult result = await ModifyAsync(orderId, new ModifyWorkingOrderRequest(EntryPrice: null, WorkingStopPrice: null));

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Restage_ShouldStillComplete_WhenTheAuditWriteFails()
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m);
        A.CallTo(() => _auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("audit store down"));

        IResult result = await ModifyAsync(orderId, Restage(5_297m)); // must not throw

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_297m); // the re-stage stands
    }
}
