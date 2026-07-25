using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// The arm → edit → take flow (gh#11 increment 2, ADR-0007): arming stages an editable ticket through the full
/// ladder <b>minus transmission</b>; every edit re-gates; taking <b>re-validates everything against fresh venue
/// truth</b> (R-12) before transmitting. The safety-relevant behaviours: a staged order never reached the venue,
/// a take that fails the fresh gate stays staged and transmits nothing, and every gate pass leaves its own
/// decision row — arm-time and take-time are separate audit facts.
/// </summary>
public class StagedOrderEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly VenueAccountId _venueAccount = VenueAccountId.Create(VenueId.Parse("projectx"), "9001");

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public StagedOrderEndpointsTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders)); // can hold the safety stop (gh#11 inc 3)
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([]);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId instrument, CancellationToken _) => Task.FromResult(
                new ResolvedContract(VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26"), instrument)));
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));
    }

    private TradingCopilotDbContext Context()
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(_operator));
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static IOptions<ProjectXConnectionOptions> PxOptions() =>
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" });

    private static IOptions<ExecutionOptions> ExecOptions() =>
        Microsoft.Extensions.Options.Options.Create(new ExecutionOptions());

    private static HostTradingEnvironment Development { get; } = new(DeploymentEnvironment.Development);

    private static SendOrderRequest SmallBuy(int quantity = 1) => new(
        "MES", 0.25m, 5m, OrderSide.Buy, quantity, 5300m, 5295m, 5290m, 5300m, OrderType.Market);

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

    private async Task<IResult> ArmAsync(Guid accountId, SendOrderRequest request)
    {
        await using TradingCopilotDbContext context = Context();
        return await OrderEndpoints.ArmOrderAsync(
            accountId, request, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);
    }

    [Fact]
    public async Task Arm_ShouldStageTheTicket_WithItsDecision_AndNeverTouchTheVenueOrderPath()
    {
        Guid accountId = await SeedAccountAsync();

        IResult result = await ArmAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();

        await using TradingCopilotDbContext reload = Context();
        Order staged = await reload.Orders.SingleAsync();
        staged.Status.Should().Be(OrderStatus.Staged);
        staged.VenueOrderKey.Should().BeNull();          // staged means NOT at the venue
        staged.Mode.Should().Be(TradingMode.Practice);   // mode stamped at arm -- a staged intent is mode-bound
        staged.SafetyStopPrice.Should().Be(5290m);       // the proposal survives whole, for the R-12 re-build
        GateDecisionRecord decision = await reload.GateDecisions.SingleAsync();
        decision.OrderId.Should().Be(staged.Id);         // arming links its decision even now
    }

    [Fact]
    public async Task Arm_ShouldRefuse_WithoutADeclaredRiskProfile()
    {
        Guid accountId = await SeedAccountAsync();
        await using (TradingCopilotDbContext strip = Context())
        {
            strip.RiskProfiles.RemoveRange(await strip.RiskProfiles.ToListAsync());
            await strip.SaveChangesAsync();
        }

        IResult result = await ArmAsync(accountId, SmallBuy());

        // Arm is send-minus-transmission: the same fail-closed preconditions apply (gh#10).
        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Edit_ShouldRegateAndUpdateTheStagedTicket()
    {
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context())
        {
            orderId = (await read.Orders.SingleAsync()).Id;
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.EditStagedOrderAsync(
            orderId, SmallBuy() with { Entry = 5302m, ReferencePrice = 5302m }, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        Order staged = await reload.Orders.SingleAsync();
        staged.Status.Should().Be(OrderStatus.Staged);   // still staged -- editing never transmits
        staged.EntryPrice.Should().Be(5302m);
        (await reload.GateDecisions.CountAsync()).Should().Be(2); // arm decision + edit re-gate decision
    }

    [Fact]
    public async Task Edit_ShouldConflict_WhenTheOrderIsNotStaged()
    {
        Guid accountId = await SeedAccountAsync();
        Guid orderId = Guid.NewGuid();
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.Orders.Add(new Order
            {
                Id = orderId,
                UserId = _operator,
                AccountId = accountId,
                Instrument = "CON.F.US.MES.U26",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                Status = OrderStatus.Working, // already at the venue
                Mode = TradingMode.Practice,
                PlacedAt = DateTimeOffset.UnixEpoch,
            });
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.EditStagedOrderAsync(
            orderId, SmallBuy(), new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Take_ShouldRevalidateFresh_TransmitOnce_AndLeaveBothDecisions()
    {
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context())
        {
            orderId = (await read.Orders.SingleAsync()).Id;
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        // R-12 is FRESH truth: the roster was read at arm AND again at take.
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).MustHaveHappenedTwiceExactly();

        await using TradingCopilotDbContext reload = Context();
        Order taken = await reload.Orders.SingleAsync();
        taken.Status.Should().Be(OrderStatus.Working);
        taken.VenueOrderKey.Should().Be("889001");
        (await reload.GateDecisions.CountAsync()).Should().Be(2); // arm-time + take-time: separate audit facts
    }

    [Fact]
    public async Task Take_ShouldRefuseAndStayStaged_WhenTheFreshGateBlocks()
    {
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext edit = Context())
        {
            // Between arm and take the ticket was edited up to a size the gate cannot pass (10 lots against a
            // 3-lot manual cap and a 100-point risk against a $300 budget).
            Order staged = await edit.Orders.SingleAsync();
            orderId = staged.Id;
            staged.Size = 10;
            staged.StopPrice = 5200m;
            staged.SafetyStopPrice = 5200m;
            await edit.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);

        // R-12 working as designed: what passed at arm does not pass now -> refused, transmitted nothing,
        // still staged for another edit.
        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).Status.Should().Be(OrderStatus.Staged);
        (await reload.GateDecisions.CountAsync()).Should().Be(2); // the blocking take-time decision is audited too
    }

    [Fact]
    public async Task Take_ShouldRebuildTheWorkingStop_NotTheSafetyStop_ForANonStopOrder()
    {
        // The gh#134 defect: a Limit/Market order carries no venue trigger, so the working stop was not
        // persisted -- take rebuilt it as the SAFETY stop, silently re-sizing against a different stop than
        // the operator armed. Proven behaviourally through the real gate under ActualStop sizing, where the
        // working stop is the only thing that moves the size: with the tight working stop the order passes and
        // transmits; with the wide safety stop substituted, PerTradeRisk leaves room for zero and take blocks.
        Guid accountId = await SeedAccountAsync();
        await using (TradingCopilotDbContext tune = Context())
        {
            RiskProfileRecord profile = await tune.RiskProfiles.SingleAsync();
            profile.SizingBasis = SizingBasis.ActualStop;   // size against the WORKING stop
            profile.PerTradeRiskFraction = 0.02m;           // tight budget so the stop distance decides the size
            profile.MaxContractsPerOrder = 100;             // don't let the manual cap mask the difference
            await tune.SaveChangesAsync();
        }

        // Working stop 1pt ($5/contract) vs safety stop 20pt ($100/contract): under ActualStop the working
        // stop admits size; the safety stop (the bug) admits none at this budget.
        SendOrderRequest limit = new("MES", 0.25m, 5m, OrderSide.Buy, 1, 5300m, 5299m, 5280m, 5300m, OrderType.Limit);
        await ArmAsync(accountId, limit);
        Guid orderId;
        await using (TradingCopilotDbContext read = Context())
        {
            Order staged = await read.Orders.SingleAsync();
            orderId = staged.Id;
            staged.StopPrice.Should().BeNull();             // a Limit order has no venue trigger...
            staged.WorkingStopPrice.Should().Be(5299m);     // ...but the working stop is preserved regardless
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);

        // Fixed: take rebuilds the working stop, sizes as arm did, and transmits. (Under the defect this is a
        // 422 -- PerTradeRisk sizes against the safety stop and leaves room for zero.)
        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    // --- The take-profit target rides arm → take (gh#173, ADR-0007) ---

    [Fact]
    public async Task Take_ShouldRoundTripTheTakeProfitTarget_FromArmToTransmission()
    {
        // The operator's take-profit must survive arm → take. The staged row persists it, and take rebuilds the
        // proposal from the row — so the venue receives the target the operator armed, not null.
        Guid accountId = await SeedAccountAsync();
        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest r, CancellationToken _) => sent = r)
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));

        await ArmAsync(accountId, SmallBuy() with { Target = 5310m }); // winning side for a long entered at 5300

        Guid orderId;
        await using (TradingCopilotDbContext read = Context())
        {
            Order staged = await read.Orders.SingleAsync();
            orderId = staged.Id;
            staged.TakeProfitPrice.Should().Be(5310m); // persisted at arm, whole, for the R-12 rebuild
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sent!.ProfitTarget.Should().Be(new Price(5310m)); // rebuilt from the row and transmitted, not dropped
    }

    [Fact]
    public async Task Arm_ShouldRefuseAWrongSideTarget_AndStageNothing()
    {
        // The domain guard (gh#170) runs at arm too: a take-profit on the losing side is a contradiction,
        // refused before anything is staged. Here a long entered at 5300 with a target BELOW entry.
        Guid accountId = await SeedAccountAsync();

        IResult result = await ArmAsync(accountId, SmallBuy() with { Target = 5290m });

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict); // pre-gate refusal maps to Conflict
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.AnyAsync()).Should().BeFalse(); // nothing staged
    }

    [Fact]
    public void StagedResponse_ShouldSurfaceTheTakeProfitTarget()
    {
        // So the operator sees and can edit the staged target (gh#173).
        Order order = new()
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            AccountId = Guid.NewGuid(),
            Instrument = "CON.F.US.MES.U26",
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Market,
            Status = OrderStatus.Staged,
            Mode = TradingMode.Practice,
            PlacedAt = DateTimeOffset.UnixEpoch,
            EntryPrice = 5300m,
            TakeProfitPrice = 5310m,
        };

        StagedOrderResponse response = StagedOrderResponse.From(order, GateDecision.Allow(1, "within every layer"));

        response.Target.Should().Be(5310m);
    }

    [Fact]
    public async Task Take_ShouldConflict_WhenTheOrderIsNotStaged()
    {
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext take = Context())
        {
            Order staged = await take.Orders.SingleAsync();
            orderId = staged.Id;
            staged.Status = OrderStatus.Working; // already transmitted
            await take.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Cancel_ShouldCancelAStagedOrder_AndRefuseANonStagedOne()
    {
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context())
        {
            orderId = (await read.Orders.SingleAsync()).Id;
        }

        await using (TradingCopilotDbContext context = Context())
        {
            IResult cancelled = await OrderEndpoints.CancelStagedOrderAsync(orderId, context, CancellationToken.None);
            StatusOf(cancelled).Should().Be(StatusCodes.Status200OK);
        }

        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).Status.Should().Be(OrderStatus.Cancelled);

        await using TradingCopilotDbContext again = Context();
        IResult second = await OrderEndpoints.CancelStagedOrderAsync(orderId, again, CancellationToken.None);
        StatusOf(second).Should().Be(StatusCodes.Status409Conflict); // cancelled is not staged -- no silent no-op
    }

    // --- Entry-method marker across arm / edit / take (gh#181) ---

    private async Task<Guid> StagedOrderIdAsync()
    {
        await using TradingCopilotDbContext read = Context();
        return (await read.Orders.SingleAsync()).Id;
    }

    [Fact]
    public async Task Arm_ShouldStageAsAnArmedTake()
    {
        Guid accountId = await SeedAccountAsync();

        await ArmAsync(accountId, SmallBuy());

        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).EntryMethod.Should().Be(OrderEntryMethod.ArmedTake);
    }

    [Fact]
    public async Task Edit_ShouldReclassifyTheStagedTicketAsAModifiedTake()
    {
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId = await StagedOrderIdAsync();

        await using (TradingCopilotDbContext context = Context())
        {
            await OrderEndpoints.EditStagedOrderAsync(
                orderId, SmallBuy() with { Entry = 5302m, ReferencePrice = 5302m }, new FixedUser(_operator), context,
                _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);
        }

        // An edit is a deviation from the armed proposal -- it takes as a ModifiedTake (R-11 records deviations).
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).EntryMethod.Should().Be(OrderEntryMethod.ModifiedTake);
    }

    [Fact]
    public async Task Take_ShouldPreserveTheArmedTakeMarker_WhenTheTicketWasNotEdited()
    {
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId = await StagedOrderIdAsync();

        await using (TradingCopilotDbContext context = Context())
        {
            await OrderEndpoints.TakeStagedOrderAsync(
                orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development,
                A.Fake<IKillSwitch>(), CancellationToken.None);
        }

        await using TradingCopilotDbContext reload = Context();
        Order taken = await reload.Orders.SingleAsync();
        taken.Status.Should().Be(OrderStatus.Working);
        taken.EntryMethod.Should().Be(OrderEntryMethod.ArmedTake); // an unchanged take stays an armed take
    }

    [Fact]
    public async Task Take_ShouldCarryTheModifiedTakeMarker_WhenTheTicketWasEdited()
    {
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId = await StagedOrderIdAsync();

        await using (TradingCopilotDbContext edit = Context())
        {
            await OrderEndpoints.EditStagedOrderAsync(
                orderId, SmallBuy() with { Entry = 5302m, ReferencePrice = 5302m }, new FixedUser(_operator), edit,
                _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);
        }
        await using (TradingCopilotDbContext take = Context())
        {
            await OrderEndpoints.TakeStagedOrderAsync(
                orderId, new FixedUser(_operator), take, _factory, PxOptions(), ExecOptions(), Development,
                A.Fake<IKillSwitch>(), CancellationToken.None);
        }

        // The deviation is carried all the way to the placed order -- a reader sees a modified take, not an armed one.
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).EntryMethod.Should().Be(OrderEntryMethod.ModifiedTake);
    }
}
