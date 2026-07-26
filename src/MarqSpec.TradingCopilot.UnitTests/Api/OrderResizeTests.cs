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
/// <b>Resizing</b> a working order's contract quantity through <c>PATCH /orders/{id}/price</c> (gh#292) — the final
/// modify follow-up. A resize re-gates at the NEW size <b>below the model</b> and modifies the order in place at the
/// venue, transmitting the <b>gate-approved</b> quantity (a downsize the gate binds is honoured, never silently
/// exceeded). The safety stop is invariant; the always-native bracket carries no size and the gateway sizes it to the
/// realized fill, so a resized fill is protected by construction — the <c>Working</c>-only guard closes the only
/// stale-bracket-size window (a partially-filled parent). Composes with an entry reprice and a working-stop re-stage.
/// </summary>
public class OrderResizeTests
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

    public OrderResizeTests()
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

    private static IOptions<ExecutionOptions> ExecOptions() => Options.Create(new ExecutionOptions()); // MaxContractsPerOrder = 5

    private static HostTradingEnvironment Development { get; } = new(DeploymentEnvironment.Development);

    private static ModifyWorkingOrderRequest Resize(int size) => new(EntryPrice: null, Size: size);

    private static ModifyWorkingOrderRequest ResizeAndReprice(int size, decimal entry, decimal reference) =>
        new(EntryPrice: entry, ReferencePrice: reference, Size: size);

    private static ModifyWorkingOrderRequest ResizeAndRestage(int size, decimal workingStop) =>
        new(EntryPrice: null, WorkingStopPrice: workingStop, Size: size);

    private Task<IResult> ModifyAsync(Guid orderId, ModifyWorkingOrderRequest request, Guid? asUser = null, IKillSwitch? killSwitch = null) =>
        OrderEndpoints.ModifyWorkingOrderPriceAsync(
            orderId, request, new FixedUser(asUser ?? _operator), Context(asUser), _factory, PxOptions(),
            ExecOptions(), Development, killSwitch ?? A.Fake<IKillSwitch>(), _auditLog, NullLoggerFactory.Instance, CancellationToken.None);

    private async Task<Guid> SeedAsync(
        int size = 1,
        SizingBasis sizingBasis = SizingBasis.SafetyStop,
        StopStaging? staging = StopStaging.Hidden,
        decimal workingStop = 5_295m,
        decimal safetyStop = 5_290m,
        decimal entryPrice = 5_300m,
        OrderStatus status = OrderStatus.Working,
        bool hasVenueKey = true,
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
            PerTradeRiskFraction = 0.15m,
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
            Size = size,
            Type = OrderType.Limit,
            EntryPrice = entryPrice,
            WorkingStopPrice = workingStop,
            SafetyStopPrice = safetyStop,
            ReferencePrice = entryPrice,
            LimitPrice = entryPrice,
            TickSize = 0.25m,
            PointValue = 5m,
            Status = status,
            Mode = TradingMode.Practice,
            EntryMethod = OrderEntryMethod.Manual,
            VenueOrderKey = hasVenueKey ? WorkingOrderKey : null,
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

    private async Task<bool> AnyGateDecisionAsync(Guid orderId) =>
        await Context().GateDecisions.IgnoreQueryFilters().AnyAsync(decision => decision.OrderId == orderId);

    private void VenueUntouched() =>
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._)).MustNotHaveHappened();

    // --- The happy path: an upsize re-gates, transmits the new size, and persists it ---

    [Fact]
    public async Task Resize_ShouldTransmitTheNewSizeToTheVenueAndPersistIt_WhenTheGateAllows()
    {
        Guid orderId = await SeedAsync(size: 1); // gate allows up to 5 (SanityCap)
        int? sentSize = null;
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? _, Price? _, int? size, CancellationToken _) => sentSize = size)
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, Resize(3));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sentSize.Should().Be(3); // the NEW size reaches the venue (not null, unlike a reprice)

        Order after = await OrderAsync(orderId);
        after.Size.Should().Be(3);                 // persisted
        after.EntryPrice.Should().Be(5_300m);      // invariant
        after.WorkingStopPrice.Should().Be(5_295m); // invariant
        after.SafetyStopPrice.Should().Be(5_290m); // invariant — the catastrophic floor is untouched

        A.CallTo(() => _auditLog.WriteAsync(
            A<IReadOnlyCollection<AuditRecord>>.That.Matches(rows => rows.Any(r =>
                r.Action == AuditAction.OrderModified && r.Before == "1" && r.After == "3" && r.UserId == _operator)),
            A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task Resize_ShouldHonourADownsize_WhenTheOperatorReducesTheSize()
    {
        Guid orderId = await SeedAsync(size: 4);
        int? sentSize = null;
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? _, Price? _, int? size, CancellationToken _) => sentSize = size)
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, Resize(2));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sentSize.Should().Be(2);
        (await OrderAsync(orderId)).Size.Should().Be(2);
    }

    // --- The gate binds a too-large resize DOWN, and the downsize is visible, never silent ---

    [Fact]
    public async Task Resize_ShouldTransmitAndPersistTheGateApprovedSize_WhenTheGateBindsTheResizeDown()
    {
        Guid orderId = await SeedAsync(size: 1); // SanityCap MaxContractsPerOrder = 5
        int? sentSize = null;
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? _, Price? _, int? size, CancellationToken _) => sentSize = size)
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, Resize(8)); // asks 8, the SanityCap floors it to 5

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sentSize.Should().Be(5);                       // the GATE-APPROVED size reaches the venue, never the asked 8
        (await OrderAsync(orderId)).Size.Should().Be(5); // and is persisted — never the asked one
    }

    // --- Size <= 0 is refused as clean input, ahead of everything ---

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Resize_ShouldReturn422_WhenTheSizeIsNotPositive(int size)
    {
        Guid orderId = await SeedAsync(size: 2);

        IResult result = await ModifyAsync(orderId, Resize(size));

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderAsync(orderId)).Size.Should().Be(2); // untouched
        VenueUntouched();
        // The guard short-circuits BEFORE the gate composes -- distinguishing it from the gate's own
        // approved-quantity<=0 refusal, which would journal a sized decision. Nothing was sized.
        (await AnyGateDecisionAsync(orderId)).Should().BeFalse();
    }

    // --- A no-op resize (size equals the current size, nothing else) is a 400, not a wasted venue call ---

    [Fact]
    public async Task Resize_ShouldReturn400_WhenTheSizeEqualsTheCurrentSizeAndNothingElseMoves()
    {
        Guid orderId = await SeedAsync(size: 3);

        IResult result = await ModifyAsync(orderId, Resize(3));

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        VenueUntouched();
    }

    // --- Only a WORKING (0-filled) order can be resized — the partial-fill guard that closes the bracket window ---

    [Theory]
    [InlineData(OrderStatus.Filled)]
    [InlineData(OrderStatus.PartiallyFilled)]
    public async Task Resize_ShouldReturn409AndNotTouchTheVenue_WhenTheOrderIsNotWorking(OrderStatus status)
    {
        Guid orderId = await SeedAsync(size: 2, status: status);

        IResult result = await ModifyAsync(orderId, Resize(4));

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        (await OrderAsync(orderId)).Size.Should().Be(2); // untouched
        VenueUntouched();
    }

    [Fact]
    public async Task Resize_ShouldReturn409_WhenTheOrderHasNoVenueHandle()
    {
        Guid orderId = await SeedAsync(size: 2, hasVenueKey: false);

        IResult result = await ModifyAsync(orderId, Resize(4));

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        VenueUntouched();
    }

    // --- The kill switch refuses a resize (outbound), and a venue rejection leaves the size untouched ---

    [Fact]
    public async Task Resize_ShouldRefuse_WhenTheKillSwitchIsEngaged()
    {
        Guid orderId = await SeedAsync(size: 2);
        IKillSwitch killed = A.Fake<IKillSwitch>();
        A.CallTo(() => killed.IsEngaged).Returns(true);

        IResult result = await ModifyAsync(orderId, Resize(4), killSwitch: killed);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict); // outbound disabled
        (await OrderAsync(orderId)).Size.Should().Be(2);
        VenueUntouched();
    }

    [Fact]
    public async Task Resize_ShouldLeaveTheSizeUntouched_WhenTheVenueRejectsTheModify()
    {
        Guid orderId = await SeedAsync(size: 2);
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("order already filled"));

        IResult result = await ModifyAsync(orderId, Resize(4));

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        (await OrderAsync(orderId)).Size.Should().Be(2); // NOT forced — venue truth reconciles the real status
    }

    // --- A size-only resize needs no referencePrice; a size+entry resize still does ---

    [Fact]
    public async Task Resize_ShouldSucceedWithoutAReferencePrice_WhenOnlyTheSizeMoves()
    {
        Guid orderId = await SeedAsync(size: 1);

        IResult result = await ModifyAsync(orderId, Resize(2)); // no referencePrice supplied

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        (await OrderAsync(orderId)).Size.Should().Be(2);
    }

    [Fact]
    public async Task Resize_ShouldReturn400_WhenTheEntryMovesWithoutAReferencePrice()
    {
        Guid orderId = await SeedAsync(size: 1);

        // size + entry but no reference: the fat-finger band has nothing to measure against.
        IResult result = await ModifyAsync(orderId, new ModifyWorkingOrderRequest(EntryPrice: 5_301m, ReferencePrice: null, Size: 2));

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await OrderAsync(orderId)).Size.Should().Be(1);
        VenueUntouched();
    }

    // --- Combined: size + entry re-gates at the new size AND the new entry, atomically ---

    [Fact]
    public async Task Resize_ShouldRepriceTheEntryAndResize_InOneAtomicCommit()
    {
        Guid orderId = await SeedAsync(size: 1); // entry 5300, working 5295, safety 5290
        Price? sentLimit = null;
        int? sentSize = null;
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? limit, Price? _, int? size, CancellationToken _) =>
            {
                sentLimit = limit;
                sentSize = size;
            })
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, ResizeAndReprice(size: 3, entry: 5_302m, reference: 5_302m));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sentLimit.Should().Be(new Price(5_302m));
        sentSize.Should().Be(3);

        Order after = await OrderAsync(orderId);
        after.EntryPrice.Should().Be(5_302m);
        after.LimitPrice.Should().Be(5_302m);
        after.Size.Should().Be(3);
        after.SafetyStopPrice.Should().Be(5_290m); // invariant
    }

    // --- Combined: size + working-stop sends the size to the venue but re-stages the stop LOCALLY, atomically ---

    [Fact]
    public async Task Resize_ShouldSendTheSizeToVenueAndReStageTheWorkingStopLocally_InOneCommit()
    {
        Guid orderId = await SeedAsync(size: 1, workingStop: 5_295m); // Hidden plan
        int? sentSize = null;
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? _, Price? _, int? size, CancellationToken _) => sentSize = size)
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, ResizeAndRestage(size: 3, workingStop: 5_297m));

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sentSize.Should().Be(3); // the size reaches the venue

        Order after = await OrderAsync(orderId);
        after.Size.Should().Be(3);
        after.WorkingStopPrice.Should().Be(5_297m); // the hidden stop re-staged locally

        StopPlanRecord plan = (await PlanAsync(orderId))!;
        plan.ActualStopPrice.Should().Be(5_297m); // the Hidden plan moved in the same commit
        plan.SafetyStopPrice.Should().Be(5_290m); // invariant
    }

    // --- TOCTOU: an order that fills while the resize is in flight must NOT get its (stale) new size written ---

    [Fact]
    public async Task Resize_ShouldNotWriteTheSize_WhenTheOrderFillsWhileTheModifyIsInFlight()
    {
        Guid orderId = await SeedAsync(size: 1);
        A.CallTo(() => _venue.ModifyOrderAsync(A<VenueAccountId>._, WorkingOrderKey, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? _, Price? _, int? _, CancellationToken _) =>
            {
                // The venue accepts, but a fill lands via the seam before the local commit: the order goes terminal.
                using TradingCopilotDbContext seam = Context();
                Order live = seam.Orders.IgnoreQueryFilters().Single(o => o.Id == orderId);
                live.Status = OrderStatus.Filled;
                seam.SaveChanges();
            })
            .Returns(Task.CompletedTask);

        IResult result = await ModifyAsync(orderId, Resize(4));

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        (await OrderAsync(orderId)).Size.Should().Be(1); // the stale new size was NOT written; venue truth reconciles
    }

    // --- Tenancy: another operator's order is invisible (R-20) ---

    [Fact]
    public async Task Resize_ShouldReturn404_ForAnotherOperatorsOrder()
    {
        Guid otherOperator = Guid.NewGuid();
        Guid orderId = await SeedAsync(size: 2, owner: otherOperator);

        IResult result = await ModifyAsync(orderId, Resize(4)); // as _operator

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        (await OrderAsync(orderId)).Size.Should().Be(2);
    }
}
