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
/// Repricing a <b>working</b> order via <c>PATCH /orders/{id}/price</c> (gh#259): the venue-facing sibling of the
/// cancel. The safety-relevant behaviours — a reprice re-gates through the FULL ladder (unlike a cancel, which is
/// risk-reducing and skips it), so a risk-increasing move that breaches the limits is refused and never
/// transmitted; SIZE and the always-native safety stop are held invariant (bracket coverage stays exactly right);
/// a venue rejection never forces a wrong terminal status; R-20 and the ADR-0015 credential guard hold; and the
/// reprice is audited as a secondary, failure-tolerant write.
/// </summary>
public class OrderModifyEndpointTests
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

    public OrderModifyEndpointTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders));

        // A healthy, FLAT practice account at the venue unless a test says otherwise.
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

    private static IOptions<ProjectXConnectionOptions> PxOptions(string credentialKey = OurCredentialKey) =>
        Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey });

    private static IOptions<ExecutionOptions> ExecOptions() => Options.Create(new ExecutionOptions());

    private static HostTradingEnvironment Development { get; } = new(DeploymentEnvironment.Development);

    /// <summary>The reprice request the happy path uses — a small upward nudge, well inside every default cap.</summary>
    private static ModifyWorkingOrderRequest Reprice(decimal entry = 5_301m, decimal reference = 5_301m) =>
        new(EntryPrice: entry, ReferencePrice: reference);

    private Task<IResult> ModifyAsync(Guid orderId, ModifyWorkingOrderRequest request, Guid? asUser = null, string configuredKey = OurCredentialKey, IKillSwitch? killSwitch = null) =>
        OrderEndpoints.ModifyWorkingOrderPriceAsync(
            orderId, request, new FixedUser(asUser ?? _operator), Context(asUser), _factory, PxOptions(configuredKey),
            ExecOptions(), Development, killSwitch ?? A.Fake<IKillSwitch>(), _auditLog, NullLoggerFactory.Instance, CancellationToken.None);

    private async Task<Guid> SeedWorkingOrderAsync(
        OrderStatus status = OrderStatus.Working,
        string? venueOrderKey = WorkingOrderKey,
        bool withRiskProfile = true,
        bool withStopPlan = true,
        Guid? owner = null,
        string credentialKey = OurCredentialKey,
        OrderSide side = OrderSide.Buy,
        decimal entry = 5_300m,
        decimal workingStop = 5_295m,
        decimal safetyStop = 5_290m)
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
            CredentialKey = credentialKey,
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
        if (withRiskProfile)
        {
            context.RiskProfiles.Add(new RiskProfileRecord
            {
                Id = Guid.NewGuid(),
                UserId = user,
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
        }

        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = user,
            AccountId = accountId,
            Instrument = "CON.F.US.MES.U26",
            Symbol = "MES",
            Side = side,
            Size = 1,
            Type = OrderType.Limit,
            EntryPrice = entry,
            WorkingStopPrice = workingStop,
            SafetyStopPrice = safetyStop,
            ReferencePrice = entry,
            LimitPrice = entry,
            TickSize = 0.25m,
            PointValue = 5m,
            Status = status,
            Mode = TradingMode.Practice,
            EntryMethod = OrderEntryMethod.Manual,
            VenueOrderKey = venueOrderKey,
            PlacedAt = new DateTimeOffset(2026, 1, 15, 14, 30, 0, TimeSpan.Zero),
        });
        if (withStopPlan)
        {
            context.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = user,
                OrderId = orderId,
                Side = side,
                EntryPrice = entry,
                ActualStopPrice = workingStop,
                SafetyStopPrice = safetyStop,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 4m,
                Staging = StopStaging.Hidden,
            });
        }

        await context.SaveChangesAsync();
        return orderId;
    }

    private async Task<Order> OrderAsync(Guid orderId) =>
        await Context().Orders.IgnoreQueryFilters().SingleAsync(order => order.Id == orderId);

    private async Task<StopPlanRecord?> PlanAsync(Guid orderId) =>
        await Context().StopPlans.IgnoreQueryFilters().FirstOrDefaultAsync(plan => plan.OrderId == orderId);

    // --- The happy path: reprice at the venue, update the journal + plan in lockstep, audit ---

    [Fact]
    public async Task ModifyPrice_ShouldRepriceTheEntryAtVenueAndUpdateJournalAndPlanAndAudit_WhenTheGateAllows()
    {
        Guid orderId = await SeedWorkingOrderAsync();
        Price? sentLimit = null;
        int? sentSize = null;
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? limit, Price? _, int? size, CancellationToken _) =>
            {
                sentLimit = limit;
                sentSize = size;
            })
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, Reprice(entry: 5_301m));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sentLimit.Should().Be(new Price(5_301m)); // a Limit order reprices its limit price
        sentSize.Should().BeNull();                // size held invariant -> sent as "leave unchanged" (gh#270), preserving queue position

        Order after = await OrderAsync(orderId);
        after.EntryPrice.Should().Be(5_301m);
        after.LimitPrice.Should().Be(5_301m);
        after.WorkingStopPrice.Should().Be(5_295m); // UNCHANGED -- entry-only this increment
        after.Size.Should().Be(1);                  // unchanged
        after.SafetyStopPrice.Should().Be(5_290m);  // unchanged -- the native bracket leg stands
        after.Status.Should().Be(OrderStatus.Working); // unchanged

        StopPlanRecord plan = (await PlanAsync(orderId))!;
        plan.EntryPrice.Should().Be(5_301m);        // the entry basis moves with the entry
        plan.ActualStopPrice.Should().Be(5_295m);   // UNCHANGED -- the working stop does not move
        plan.SafetyStopPrice.Should().Be(5_290m);   // unchanged

        A.CallTo(() => _auditLog.WriteAsync(
            A<IReadOnlyCollection<AuditRecord>>.That.Matches(rows => rows.Any(record =>
                record.Action == AuditAction.OrderModified && !record.SyntheticRisk && record.UserId == _operator)),
            A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task ModifyPrice_ShouldReturn422AndNotTouchVenue_WhenTheNewEntryCrossesItsStops()
    {
        // The reprice must not move the entry across the (unchanged) working / safety stops -- doing so would
        // invert the trade's geometry and, uncaught, trip the StopPlan safety-beyond-actual DB CHECK at commit
        // AFTER the venue already repriced. A long entry dropped below its working stop (5295) is refused here,
        // before the venue is touched. (gh#259 review finding.)
        Guid orderId = await SeedWorkingOrderAsync(); // BUY: working 5295, safety 5290

        IResult result = await ModifyAsync(orderId, Reprice(entry: 5_294m, reference: 5_294m));

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_300m); // journal untouched
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- The SELL side: geometry is mirrored (both stops ABOVE entry), so both branches need their own proof ---

    [Fact]
    public async Task ModifyPrice_ShouldRepriceTheEntry_ForASellOrder()
    {
        // A SELL rests with working (5305) and safety (5310) stops ABOVE entry (5300). Repricing the entry up to
        // 5301 keeps it below both stops (the Sell branch of the cross-stops guard), and the gate approves at size 1.
        Guid orderId = await SeedWorkingOrderAsync(side: OrderSide.Sell, entry: 5_300m, workingStop: 5_305m, safetyStop: 5_310m);
        Price? sentLimit = null;
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? limit, Price? _, int? _, CancellationToken _) => sentLimit = limit)
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, Reprice(entry: 5_301m, reference: 5_301m));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sentLimit.Should().Be(new Price(5_301m));
        Order after = await OrderAsync(orderId);
        after.EntryPrice.Should().Be(5_301m);
        after.WorkingStopPrice.Should().Be(5_305m); // unchanged
        after.SafetyStopPrice.Should().Be(5_310m);  // unchanged
    }

    [Fact]
    public async Task ModifyPrice_ShouldReturn422AndNotTouchVenue_WhenASellEntryCrossesItsStops()
    {
        // The mirror of the Buy cross-stops guard: a SELL entry raised ABOVE its working stop (5305) inverts the
        // geometry and is refused before the venue is touched. A sign error in the Sell branch would slip past a
        // Buy-only suite (gh#270 review).
        Guid orderId = await SeedWorkingOrderAsync(side: OrderSide.Sell, entry: 5_300m, workingStop: 5_305m, safetyStop: 5_310m);

        IResult result = await ModifyAsync(orderId, Reprice(entry: 5_306m, reference: 5_306m));

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_300m); // journal untouched
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- Only a working order with a venue handle ---

    [Theory]
    [InlineData(OrderStatus.Staged)]
    [InlineData(OrderStatus.Filled)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.PartiallyFilled)] // partly a live position -- repricing its entry is the hazard the guard refuses
    [InlineData(OrderStatus.Rejected)]
    public async Task ModifyPrice_ShouldReturn409AndNotTouchVenue_WhenTheOrderIsNotWorking(OrderStatus status)
    {
        Guid orderId = await SeedWorkingOrderAsync(status: status);

        IResult result = await ModifyAsync(orderId, Reprice());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ModifyPrice_ShouldReturn409_WhenTheWorkingOrderHasNoVenueHandle()
    {
        Guid orderId = await SeedWorkingOrderAsync(venueOrderKey: null);

        IResult result = await ModifyAsync(orderId, Reprice());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    // --- R-20 + ADR-0015 ---

    [Fact]
    public async Task ModifyPrice_ShouldReturn404AndNotTouchVenue_WhenTheOrderBelongsToAnotherOperator()
    {
        Guid otherOperator = Guid.NewGuid();
        Guid orderId = await SeedWorkingOrderAsync(owner: otherOperator);

        IResult result = await ModifyAsync(orderId, Reprice()); // called as _operator -- B's order is R-20-invisible

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_300m); // untouched
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ModifyPrice_ShouldReturn409AndNotTouchVenue_WhenTheCredentialSetIsForeign()
    {
        Guid orderId = await SeedWorkingOrderAsync(credentialKey: "another-firm");

        IResult result = await ModifyAsync(orderId, Reprice()); // this process holds "topstep-main"

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict); // ADR-0015
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- The re-gate binds: fail-closed inputs, flatness, and a risk-increasing reprice ---

    [Fact]
    public async Task ModifyPrice_ShouldReturn422_WhenNoRiskProfileIsDeclared()
    {
        Guid orderId = await SeedWorkingOrderAsync(withRiskProfile: false);

        IResult result = await ModifyAsync(orderId, Reprice());

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity); // absence is refusal, not a default
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ModifyPrice_ShouldReturn409_WhenTheAccountIsNotFlat()
    {
        Guid orderId = await SeedWorkingOrderAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(
            [
                new PositionSnapshot(_venueAccount, VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26"), 2, new Price(5_300m)),
            ]);

        IResult result = await ModifyAsync(orderId, Reprice());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict); // open position => risk state is a guess
    }

    [Fact]
    public async Task ModifyPrice_ShouldReturn422AndLeaveTheJournalUnchanged_WhenTheGateBlocksTheWiderStop()
    {
        // Move the entry far from the (unchanged) safety stop: the per-trade risk on the widened distance breaches
        // the declared cap, so the gate refuses -- the reprice is not transmitted and the journal stands. (The entry
        // 5360 stays above the working stop 5295, so it clears the ordering guard and reaches the gate.)
        Guid orderId = await SeedWorkingOrderAsync();

        IResult result = await ModifyAsync(orderId, Reprice(entry: 5_360m, reference: 5_360m));

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_300m); // journal untouched
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- The kill switch refuses a modify (unlike a cancel) ---

    [Fact]
    public async Task ModifyPrice_ShouldReturn409AndLeaveTheJournalUnchanged_WhenTheKillSwitchIsEngaged()
    {
        Guid orderId = await SeedWorkingOrderAsync();
        IKillSwitch killed = A.Fake<IKillSwitch>();
        A.CallTo(() => killed.IsEngaged).Returns(true);

        IResult result = await ModifyAsync(orderId, Reprice(), killSwitch: killed);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict); // outbound disabled -- a reprice can add risk
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_300m);
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- Venue rejection never forces a wrong status ---

    [Fact]
    public async Task ModifyPrice_ShouldReturn409AndLeaveTheJournalUnchanged_WhenTheVenueRejects()
    {
        Guid orderId = await SeedWorkingOrderAsync();
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("order already filled")); // it may have FILLED at the old price

        IResult result = await ModifyAsync(orderId, Reprice());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        Order after = await OrderAsync(orderId);
        after.EntryPrice.Should().Be(5_300m);          // NOT forced -- the seam reconciles the real status
        after.Status.Should().Be(OrderStatus.Working); // untouched
        (await PlanAsync(orderId))!.EntryPrice.Should().Be(5_300m);
    }

    // --- TOCTOU: a fill/cancel landing mid-flight aborts the price write ---

    [Fact]
    public async Task ModifyPrice_ShouldReturn409AndNotRewritePrice_WhenTheOrderMovedFromWorkingBeforeCommit()
    {
        Guid orderId = await SeedWorkingOrderAsync();

        // The venue accepts the modify, but a fill event lands via the seam (a separate context) before the commit.
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes(() =>
            {
                using TradingCopilotDbContext seam = Context();
                Order live = seam.Orders.IgnoreQueryFilters().Single(order => order.Id == orderId);
                live.Status = OrderStatus.Filled;
                seam.SaveChanges();
            })
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, Reprice());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_300m); // the venue moved, the journal price did not
    }

    // --- The audit is secondary: its failure never undoes the reprice ---

    [Fact]
    public async Task ModifyPrice_ShouldStillComplete_WhenTheAuditWriteFails()
    {
        Guid orderId = await SeedWorkingOrderAsync();
        A.CallTo(() => _auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("audit store down"));

        IResult result = await ModifyAsync(orderId, Reprice(entry: 5_301m)); // must not throw

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        (await OrderAsync(orderId)).EntryPrice.Should().Be(5_301m); // the reprice stands
    }
}
