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
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// Moving a working order's <b>entry and working stop together</b> in one <c>PATCH /orders/{id}/price</c> (gh#278) —
/// the combined path over the gh#259 entry venue reprice + the gh#267 hidden-stop re-stage. The safety-relevant
/// behaviours: the full chain <c>safety → working → entry</c> is re-validated <b>before</b> the venue call (so a
/// crossing can never trip the DB CHECK after the entry already repriced); the gate sees the <b>new</b> working stop
/// (a risk-increasing widen is refused, not silently allowed); the entry, working stop, and plan commit atomically;
/// a venue rejection leaves everything untouched; and a promoted / orphaned plan is refused.
/// </summary>
public class OrderCombinedModifyTests
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

    public OrderCombinedModifyTests()
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

    private static ModifyWorkingOrderRequest Combined(decimal entry, decimal workingStop, decimal? reference = null) =>
        new(EntryPrice: entry, WorkingStopPrice: workingStop, ReferencePrice: reference ?? entry);

    private static ModifyWorkingOrderRequest EntryOnly(decimal entry, decimal? reference = null) =>
        new(EntryPrice: entry, ReferencePrice: reference ?? entry);

    private Task<IResult> ModifyAsync(Guid orderId, ModifyWorkingOrderRequest request, Guid? asUser = null, IKillSwitch? killSwitch = null) =>
        OrderEndpoints.ModifyWorkingOrderPriceAsync(
            orderId, request, new FixedUser(asUser ?? _operator), Context(asUser), _factory, PxOptions(),
            ExecOptions(), Development, killSwitch ?? A.Fake<IKillSwitch>(), _auditLog, NullLoggerFactory.Instance, NullExecutionMetrics.Instance, CancellationToken.None);

    private async Task<Guid> SeedAsync(
        SizingBasis sizingBasis = SizingBasis.SafetyStop,
        StopStaging? staging = StopStaging.Hidden,
        decimal workingStop = 5_295m,
        decimal safetyStop = 5_290m,
        decimal entryPrice = 5_300m,
        decimal perTradeRiskFraction = 0.15m,
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
            Type = OrderType.Limit,
            EntryPrice = entryPrice,
            WorkingStopPrice = workingStop,
            SafetyStopPrice = safetyStop,
            ReferencePrice = entryPrice,
            LimitPrice = entryPrice,
            TickSize = 0.25m,
            PointValue = 5m,
            Status = OrderStatus.Working,
            Mode = TradingMode.Practice,
            EntryMethod = OrderEntryMethod.Manual,
            VenueOrderKey = WorkingOrderKey,
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

    private async Task<StopPlanRecord?> PlanAsync(Guid orderId) =>
        await Context().StopPlans.IgnoreQueryFilters().FirstOrDefaultAsync(plan => plan.OrderId == orderId);

    private void VenueUntouched() =>
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();

    // --- The happy path: entry reprices at the venue, working stop re-stages, both commit atomically ---

    [Fact]
    public async Task Combined_ShouldRepriceEntryAtVenueAndReStageWorkingStop_Atomically()
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m); // entry 5300, safety 5290
        Price? sentLimit = null;
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? limit, Price? _, int? _, CancellationToken _) => sentLimit = limit)
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, Combined(entry: 5_302m, workingStop: 5_297m));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sentLimit.Should().Be(new Price(5_302m)); // the ENTRY reprices at the venue

        Order after = await OrderAsync(orderId);
        after.EntryPrice.Should().Be(5_302m);
        after.WorkingStopPrice.Should().Be(5_297m);
        after.LimitPrice.Should().Be(5_302m);
        after.SafetyStopPrice.Should().Be(5_290m); // invariant
        after.Size.Should().Be(1);                 // invariant

        StopPlanRecord plan = (await PlanAsync(orderId))!;
        plan.EntryPrice.Should().Be(5_302m);
        plan.ActualStopPrice.Should().Be(5_297m);
        plan.SafetyStopPrice.Should().Be(5_290m); // invariant

        A.CallTo(() => _auditLog.WriteAsync(
            A<IReadOnlyCollection<AuditRecord>>.That.Matches(rows => rows.Any(r =>
                r.Action == AuditAction.OrderModified && !r.SyntheticRisk && r.UserId == _operator)),
            A<CancellationToken>._)).MustHaveHappened();
    }

    // --- The full-chain ordering guard fires BEFORE the venue ---

    [Theory]
    [InlineData(5_302, 5_289)] // working stop below the safety stop (5290)
    [InlineData(5_302, 5_303)] // working stop through the (new) entry
    [InlineData(5_294, 5_296)] // entry dropped below the (new) working stop
    public async Task Combined_ShouldReturn422AndNotTouchVenue_WhenTheNewGeometryCrossesTheChain(int entry, int working)
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m, safetyStop: 5_290m, entryPrice: 5_300m);

        IResult result = await ModifyAsync(orderId, Combined(entry, working, reference: entry));

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_300m); // untouched
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_295m);
        VenueUntouched();
    }

    [Fact]
    public async Task Combined_ShouldReturn422_WhenTheWorkingStopMovesOntoTheSafetyStop()
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m, safetyStop: 5_290m);

        IResult result = await ModifyAsync(orderId, Combined(entry: 5_302m, workingStop: 5_290m)); // onto safety = removal

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        VenueUntouched();
    }

    // --- The gate re-validates the NEW working stop ---

    [Fact]
    public async Task Combined_ShouldRefuseByGateAndNotTouchVenue_WhenTheWiderWorkingStopBreachesPerTradeRisk()
    {
        // ActualStop basis sizes per-trade risk at the working stop. Widening it in the combined move pushes the
        // resting size over the per-trade layer -- the gate refuses, and nothing is transmitted.
        Guid orderId = await SeedAsync(
            sizingBasis: SizingBasis.ActualStop, workingStop: 5_299m, safetyStop: 5_285m, perTradeRiskFraction: 0.03m);

        IResult result = await ModifyAsync(orderId, Combined(entry: 5_300m, workingStop: 5_286m)); // 14 pts = $70 > $60

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_299m); // untouched
        VenueUntouched();
    }

    // --- A venue rejection on the entry leaves BOTH untouched ---

    [Fact]
    public async Task Combined_ShouldLeaveOrderAndPlanUntouched_WhenTheVenueRejectsTheEntryReprice()
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m);
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("order already filled"));

        IResult result = await ModifyAsync(orderId, Combined(entry: 5_302m, workingStop: 5_297m));

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        Order after = await OrderAsync(orderId);
        after.EntryPrice.Should().Be(5_300m);       // NOT forced
        after.WorkingStopPrice.Should().Be(5_295m); // the working stop is not written when the entry reprice fails
        (await PlanAsync(orderId))!.ActualStopPrice.Should().Be(5_295m);
    }

    // --- Plan-staging rules carry over from gh#267 ---

    [Theory]
    [InlineData(StopStaging.Native)]
    [InlineData(StopStaging.Orphaned)]
    [InlineData(StopStaging.Retired)]
    public async Task Combined_ShouldReturn409AndNotTouchVenue_WhenThePlanIsNotHidden(StopStaging staging)
    {
        Guid orderId = await SeedAsync(staging: staging, workingStop: 5_295m);

        IResult result = await ModifyAsync(orderId, Combined(entry: 5_302m, workingStop: 5_297m));

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_300m);
        VenueUntouched();
    }

    [Fact]
    public async Task Combined_ShouldCreateAHiddenPlan_WhenInstallingAWorkingStopOnACoincidentOrderWhileMovingTheEntry()
    {
        Guid orderId = await SeedAsync(staging: null, workingStop: 5_290m, safetyStop: 5_290m); // coincident, no plan

        IResult result = await ModifyAsync(orderId, Combined(entry: 5_302m, workingStop: 5_296m));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        Order after = await OrderAsync(orderId);
        after.EntryPrice.Should().Be(5_302m);
        after.WorkingStopPrice.Should().Be(5_296m);
        StopPlanRecord created = (await PlanAsync(orderId))!;
        created.Staging.Should().Be(StopStaging.Hidden);
        created.EntryPrice.Should().Be(5_302m);
        created.ActualStopPrice.Should().Be(5_296m);
    }

    // --- The generalization must NOT regress an ENTRY-ONLY reprice (newWorkingStop == null) ---
    // A coincident-stop order (working == safety, no plan) is a valid, shipped state (gh#259). Its entry-only
    // reprice must stay byte-identical: the ordering guard must NOT apply the combined safety-vs-working strictness
    // (safety == working, so a strict `safety < working` would wrongly 422 a move that gh#259 accepted).

    [Fact]
    public async Task EntryOnly_ShouldRepriceCoincidentStopOrderAtVenue_LeavingTheStopAndPlanAlone()
    {
        Guid orderId = await SeedAsync(staging: null, workingStop: 5_290m, safetyStop: 5_290m); // coincident, no plan
        Price? sentLimit = null;
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? limit, Price? _, int? _, CancellationToken _) => sentLimit = limit)
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, EntryOnly(entry: 5_305m)); // move the entry UP, stops untouched

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sentLimit.Should().Be(new Price(5_305m)); // the entry still reprices at the venue

        Order after = await OrderAsync(orderId);
        after.EntryPrice.Should().Be(5_305m);
        after.WorkingStopPrice.Should().Be(5_290m); // untouched — an entry-only move never touches the working stop
        after.SafetyStopPrice.Should().Be(5_290m);
        (await PlanAsync(orderId)).Should().BeNull(); // no plan installed by an entry-only move
    }

    // --- The kill switch refuses a combined move (the entry reprice is outbound) ---

    [Fact]
    public async Task Combined_ShouldRefuse_WhenTheKillSwitchIsEngaged()
    {
        Guid orderId = await SeedAsync(workingStop: 5_295m);
        IKillSwitch killed = A.Fake<IKillSwitch>();
        A.CallTo(() => killed.IsEngaged).Returns(true);

        IResult result = await ModifyAsync(orderId, Combined(entry: 5_302m, workingStop: 5_297m), killSwitch: killed);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict); // outbound disabled
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_300m);
        (await OrderAsync(orderId)).WorkingStopPrice.Should().Be(5_295m);
        VenueUntouched();
    }

    // --- Tenancy ---

    [Fact]
    public async Task Combined_ShouldReturn404_ForAnotherOperatorsOrder()
    {
        Guid otherOperator = Guid.NewGuid();
        Guid orderId = await SeedAsync(workingStop: 5_295m, owner: otherOperator);

        IResult result = await ModifyAsync(orderId, Combined(entry: 5_302m, workingStop: 5_297m)); // as _operator

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_300m);
    }
}
