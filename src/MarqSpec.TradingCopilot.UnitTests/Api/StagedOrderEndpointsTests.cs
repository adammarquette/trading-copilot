using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    // The reconcile endpoint (gh#589) reads venue truth through this service; empty flatten schedules + a reachable
    // venue yield a Live basis, so a reachable read is never mistaken for Unknown.
    private WorkingOrderReconciliationService RestingOrders() => new(
        Context(), _factory, PxOptions(),
        Microsoft.Extensions.Options.Options.Create(new FlattenOptions()),
        NullLogger<WorkingOrderReconciliationService>.Instance);

    // The reconcile also confirms the account is FLAT before releasing (gh#589 round-2): "no working order" is not
    // "nothing live" -- a filled take leaves a position, not a working order. This reads venue positions.
    // The fill-history read (gh#631). Neither of the two reads above can see an order that placed, FILLED and
    // round-tripped, so only this one can tell a fire that never reached the market from one that already executed --
    // the difference between correctly re-arming a stranded conditional and firing a second entry.
    private FillReconciliationService Fills() => new(
        Context(), _factory, PxOptions(), NullLogger<FillReconciliationService>.Instance);

    private PositionReconciliationService Positions() => new(
        Context(), _factory, PxOptions(),
        Microsoft.Extensions.Options.Options.Create(new FlattenOptions()),
        NullLogger<PositionReconciliationService>.Instance);

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
            accountId, request, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, CancellationToken.None);
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
            orderId, SmallBuy() with { Entry = 5302m, ReferencePrice = 5302m }, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, CancellationToken.None);

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
            orderId, SmallBuy(), new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, CancellationToken.None);

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
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

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
    public async Task Take_ShouldRefuse_WhenTheAccountAlreadyHasAnotherOutstandingOrder()
    {
        // gh#589: a take is an entry-transmit, so it runs the no-stacking check inside the account lock. The account
        // already holds a DIFFERENT Working order, so taking the staged ticket -- which ComposeAsync would size
        // against a flat snapshot that ignores that exposure -- is refused. The check runs BEFORE the claim, so the
        // staged row being taken is still Staged and never counts ITSELF; only the other outstanding order blocks.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context())
        {
            orderId = (await read.Orders.SingleAsync(o => o.Status == OrderStatus.Staged)).Id;
        }
        await SeedWorkingOrderAsync(accountId);

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.FirstAsync(o => o.Id == orderId)).Status.Should().Be(
            OrderStatus.Staged, "the no-stacking check refuses before the claim, so the ticket stays takeable");
    }

    [Fact]
    public async Task Take_ShouldRunUnderTheAccountEntryGuard()
    {
        // The per-account lock (gh#589) that serializes the no-stacking check + the place must actually wrap the take's
        // transmit tail, so a take and a send / another take cannot both pass the check before either journals. This
        // pins that the take runs through the guard for THIS account; the real advisory-lock serialization is QA's.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }

        IAccountEntryGuard guard = PassthroughGuard();
        await using TradingCopilotDbContext context = Context();
        await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), guard, NullLoggerFactory.Instance, CancellationToken.None);

        A.CallTo(() => guard.RunExclusiveAsync<IResult>(
                A<TradingCopilotDbContext>._, accountId, A<Func<Task<IResult>>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Take_ShouldLeaveTaking_WhenTheVenueSendThrows()
    {
        // gh#589 round-2 (H1): a venue send that THROWS is maybe-live and must be LEFT Taking, never released. Even a
        // "!Success" rejection can't be told apart at this seam from an accepted-but-no-id (the venue took it) or a
        // maybe-landed timeout, so all of them fail toward Taking; releasing would re-open the gh#530 double-place.
        // The reconcile endpoint then resolves it (nothing rests -> release; something rests -> adopt).
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }

        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Throws(() => new InvalidOperationException("venue send faulted (test)"));

        await using TradingCopilotDbContext context = Context();
        Func<Task> take = () => OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        await take.Should().ThrowAsync<InvalidOperationException>();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.FirstAsync(o => o.Id == orderId)).Status.Should().Be(
            OrderStatus.Taking, "a venue send fault is maybe-live, so the row is left Taking for /reconcile, never released (gh#589)");
    }

    [Fact]
    public async Task Take_ShouldLeaveTaking_WhenTheVenueTimesOut()
    {
        // gh#589 round-2 (H1, the sharpest case): a venue/HTTP TIMEOUT surfaces as a TaskCanceledException (an
        // OperationCanceledException) whose token is HttpClient's internal one, NOT the caller's -- the canonical
        // maybe-live failure. It must be left Taking like any other send fault, never mistaken for a definitive
        // rejection and released (which was the double-place bug this fixes). The caller's own token is NOT cancelled.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }

        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Throws(() => new TaskCanceledException("venue send timed out (test)"));

        await using TradingCopilotDbContext context = Context();
        Func<Task> take = () => OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        await take.Should().ThrowAsync<TaskCanceledException>();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.FirstAsync(o => o.Id == orderId)).Status.Should().Be(
            OrderStatus.Taking, "a venue timeout is maybe-live -> left Taking, never released (the H1 double-place fix, gh#589)");
    }

    [Fact]
    public async Task Take_ShouldLeaveTheRowTaking_WhenTheClientDisconnectsMidSend()
    {
        // gh#589 Part B: a client disconnect / shutdown (OperationCanceledException, token cancelled) mid-send MAY
        // have reached the venue -- outcome unknown -- so reverting to Staged could re-take a live order. The row is
        // left Taking (the durable claim, uncancellable, reconciled on replay); uncertainty resolves to the safe state.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }

        using CancellationTokenSource cts = new();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes(() => cts.Cancel())
            .Throws(() => new OperationCanceledException(cts.Token));

        // A fake logger so we can assert the disconnect is surfaced LOUDLY (an order may be live and unrecorded).
        ILogger takeLogger = A.Fake<ILogger>();
        ILoggerFactory loggerFactory = A.Fake<ILoggerFactory>();
        A.CallTo(() => loggerFactory.CreateLogger(A<string>._)).Returns(takeLogger);

        await using TradingCopilotDbContext context = Context();
        Func<Task> take = () => OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), loggerFactory, cts.Token);

        await take.Should().ThrowAsync<OperationCanceledException>();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.FirstAsync(o => o.Id == orderId)).Status.Should().Be(
            OrderStatus.Taking, "a disconnect mid-send leaves the row Taking (may be live at the venue), never Staged (gh#589)");
        A.CallTo(takeLogger)
            .Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Error)
            .MustHaveHappened(); // the may-be-live window is surfaced loudly, not silently stranded
    }

    [Fact]
    public async Task Take_ShouldStampTheRowIdAsTheVenueCorrelationTag()
    {
        // gh#589 Part B: the take stamps the row's own id as the venue order's customTag, so a take that placed then
        // stranded (its key not yet journaled) is recognisable against venue truth on replay -- the durable claim's
        // venue-side counterpart, mirroring the conditional fire (gh#577).
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }

        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest request, CancellationToken _) => sent = request)
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));

        await using TradingCopilotDbContext context = Context();
        await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        sent.Should().NotBeNull();
        sent!.CustomTag.Should().Be(orderId.ToString(), "the venue order carries the row id so a stranded take is reconcilable (gh#589)");
    }

    [Fact]
    public async Task Take_ShouldReleaseTheClaim_WhenComposeThrows()
    {
        // gh#589 review fix: compose + build are pre-venue READS run AFTER the claim. A throw there (a venue roster /
        // positions timeout, a DB blip) placed nothing, so the claim must be released -- otherwise the row strands
        // Taking and, because gh#589 counts Taking, dead-locks the account. Here the take's fresh-truth roster read
        // throws; the row must return to Staged. (Arming above ran while the roster read still worked.)
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }

        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._))
            .Throws(() => new InvalidOperationException("venue roster read failed mid-take (test)"));

        await using TradingCopilotDbContext context = Context();
        Func<Task> take = () => OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        await take.Should().ThrowAsync<InvalidOperationException>();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.FirstAsync(o => o.Id == orderId)).Status.Should().Be(
            OrderStatus.Staged, "a pre-venue compose fault releases the claim rather than stranding the row Taking (gh#589)");
    }

    [Fact]
    public async Task Reconcile_ShouldAdoptTheLiveVenueOrder_WhenItRestsUnderTheRowsTag()
    {
        // gh#589 runtime recovery: a take stranded Taking whose order DID rest at the venue. The reconcile reads venue
        // truth, matches the resting order by the customTag the take stamped (the row id), and adopts it as a tracked
        // Working order (with a promotion plan) -- so the account is unblocked and the live order is managed.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }
        await SetTakingAsync(orderId);

        VenueContractId contract = VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26");
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>(
            [
                new WorkingOrder("VENUE-KEY-42", contract, null, null, Size: 1) { CustomTag = orderId.ToString() },
            ]);

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileTakingOrderAsync(
            orderId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        Order adopted = await reload.Orders.FirstAsync(o => o.Id == orderId);
        adopted.Status.Should().Be(OrderStatus.Working, "the live venue order is adopted, so the row is tracked and the account unblocked");
        adopted.VenueOrderKey.Should().Be("VENUE-KEY-42");
        (await reload.StopPlans.AnyAsync(p => p.OrderId == orderId)).Should().BeTrue("an adopted working order gets a promotion plan");
    }

    [Fact]
    public async Task Reconcile_ShouldReleaseToStaged_WhenNothingRestsAtTheVenue()
    {
        // gh#589: a stranded Taking whose order never rested (a rejection, or a fault before the place). On a REACHABLE
        // venue with nothing under the tag, the reconcile releases the claim (Taking->Staged) -- the ticket is takeable
        // again and the account unblocked.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }
        await SetTakingAsync(orderId);

        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]); // reachable venue, nothing rests

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileTakingOrderAsync(
            orderId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.FirstAsync(o => o.Id == orderId)).Status.Should().Be(
            OrderStatus.Staged, "nothing rests, so the claim is released and the ticket is takeable again");
    }

    [Fact]
    public async Task Reconcile_ShouldRefuseAndLeaveTaking_WhenNothingRestsButAPositionIsOpen()
    {
        // gh#589 round-2: "no working order under the tag" is NOT "nothing live" -- a stranded take may have FILLED
        // before the reconcile (a filled entry is no longer a working order; its bracket legs carry no tag). Releasing
        // then would strand an untracked open position. So a reachable venue with nothing under the tag but an OPEN
        // position refuses: the row stays Taking, and the operator resolves the position first.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }
        await SetTakingAsync(orderId);

        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]); // nothing resting under the tag...
        VenueContractId contract = VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26");
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(
                [new PositionSnapshot(_venueAccount, contract, NetQuantity: 1, new Price(5300m))]); // ...but a position IS open

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileTakingOrderAsync(
            orderId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.FirstAsync(o => o.Id == orderId)).Status.Should().Be(
            OrderStatus.Taking, "a possibly-filled take is never released over an open position — the row stays Taking (gh#589)");
    }

    [Fact]
    public async Task Reconcile_ShouldRefuseAndLeaveTaking_WhenTheVenueIsUnreachable()
    {
        // gh#589 + gh#381: "we could not ask" is not "nothing is there". A stranded Taking is NEVER resolved against an
        // unknown book -- releasing could re-take a live order, adopting could invent a phantom. The row stays Taking.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }
        await SetTakingAsync(orderId);

        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(() => new InvalidOperationException("venue unreachable (test)")); // -> Basis Unknown

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileTakingOrderAsync(
            orderId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.FirstAsync(o => o.Id == orderId)).Status.Should().Be(
            OrderStatus.Taking, "an unknown venue book is never used to resolve a stranded take");
    }

    [Fact]
    public async Task Reconcile_ShouldRefuse_WhenTheOrderIsNotTaking()
    {
        // Only a stranded Taking is reconcilable -- a plain Staged ticket (or any other status) is resolved through its
        // own paths, so reconcile stays a narrow recovery rather than a general force-write.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileTakingOrderAsync(
            orderId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task ReconcileFiringConditional_ShouldAdoptTheLiveVenueOrder_WhenItRestsUnderTheConditionalsTag()
    {
        // gh#589 runtime recovery, the fire-path sibling of Reconcile_ShouldAdopt... : a conditional stranded Firing
        // whose order DID rest at the venue. Unlike the take (which flips an existing Staged row), the stranded fire
        // journaled NO Order row, so the reconcile CREATES the Working row -- matched by the customTag the fire stamped
        // (the conditional's id) -- marks the conditional Fired, and protects the position with a promotion plan.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await SeedFiringConditionalAsync(accountId);

        VenueContractId contract = VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26");
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>(
            [
                new WorkingOrder("VENUE-KEY-77", contract, null, null, Size: 1) { CustomTag = conditionalId.ToString() },
            ]);

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileFiringConditionalAsync(
            conditionalId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        ConditionalOrderRecord conditional = await reload.ConditionalOrders.FirstAsync(c => c.Id == conditionalId);
        conditional.Status.Should().Be(ConditionalStatus.Fired, "the live venue order is adopted, so the conditional is Fired");
        conditional.FiredOrderId.Should().NotBeNull("the adopted Working order is linked to the conditional");
        Order adopted = await reload.Orders.FirstAsync(o => o.Id == conditional.FiredOrderId!.Value);
        adopted.Status.Should().Be(OrderStatus.Working, "the live venue order is adopted, so the account is unblocked");
        adopted.VenueOrderKey.Should().Be("VENUE-KEY-77");
        adopted.EntryMethod.Should().Be(OrderEntryMethod.Conditional, "the adopted order is journaled as a conditional fire");
        (await reload.StopPlans.AnyAsync(p => p.OrderId == adopted.Id)).Should().BeTrue("an adopted working order gets a promotion plan");
    }

    [Fact]
    public async Task ReconcileFiringConditional_ShouldReleaseToPending_WhenNothingRestsAndTheAccountIsFlat()
    {
        // gh#589: a conditional stranded Firing whose order never rested (a rejection, or a fault before the place). On a
        // REACHABLE, FLAT account with nothing under the tag, the reconcile releases the conditional (Firing->Pending) --
        // it re-decides on the next quote and the account is unblocked.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await SeedFiringConditionalAsync(accountId);

        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]); // reachable, nothing rests; positions default flat

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileFiringConditionalAsync(
            conditionalId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.FirstAsync(c => c.Id == conditionalId)).Status.Should().Be(
            ConditionalStatus.Pending, "nothing rests and the account is flat, so the conditional is released to Pending");
        (await reload.Orders.AnyAsync()).Should().BeFalse("nothing was journaled -- the fire did not place");
    }

    [Fact]
    public async Task Reconcile_ShouldJournalFilled_WhenVenueFillHistoryShowsTheTakeFilled()
    {
        // gh#631, the take-path sibling. Releasing a round-tripped take to Staged is not dangerous the way the
        // conditional's re-arm is -- Staged is inert -- but it silently discards a trade that really happened, and the
        // journal is the system's memory (R-8/R-9). A reported fill is journaled onto the row instead.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }
        await SetTakingAsync(orderId);

        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]); // nothing rests; positions default flat
        A.CallTo(() => _venue.FindFilledOrderByTagAsync(
                A<VenueAccountId>._, A<string>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .ReturnsLazily((VenueAccountId _, string tag, DateTimeOffset _, CancellationToken _) =>
                TaggedFillEvidence.Filled(tag, 2m, 5301m, "VENUE-KEY-TAKE"));

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileTakingOrderAsync(
            orderId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        Order reconciled = await reload.Orders.FirstAsync(o => o.Id == orderId);
        reconciled.Status.Should().Be(
            OrderStatus.Filled, "the take executed and round-tripped, so the ticket records the fill rather than being handed back");
        reconciled.Status.Should().NotBe(OrderStatus.Staged, "releasing to Staged would discard a real trade from the journal");
        reconciled.VenueOrderKey.Should().Be("VENUE-KEY-TAKE");
        reconciled.Size.Should().Be(2, "the journaled size is what actually executed at the venue");
    }

    [Fact]
    public async Task ReconcileFiringConditional_ShouldResolveFired_WhenVenueFillHistoryShowsTheTagFilled()
    {
        // gh#631 -- THE failure this card exists to stop. A fire that PLACED, FILLED and then ROUND-TRIPPED (its
        // bracket closed the position) leaves nothing resting AND a flat account, so through the two reads above it is
        // indistinguishable from a fire that never reached the market. Releasing it to Pending re-arms a one-shot whose
        // entry already completed -- and because HasFired is a LEVEL test, the next quote past the trigger fires it
        // again: a second autonomous entry. Fill history is the only thing that separates the two cases.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await SeedFiringConditionalAsync(accountId);

        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]); // nothing rests -- the fill is not a working order
        A.CallTo(() => _venue.FindFilledOrderByTagAsync(
                A<VenueAccountId>._, conditionalId.ToString(), A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(TaggedFillEvidence.Filled(conditionalId.ToString(), 1m, 5300m, "VENUE-KEY-99"));

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileFiringConditionalAsync(
            conditionalId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        ConditionalOrderRecord conditional = await reload.ConditionalOrders.FirstAsync(c => c.Id == conditionalId);
        conditional.Status.Should().Be(
            ConditionalStatus.Fired,
            "the fire executed, so it resolves Fired -- releasing it to Pending would re-arm a one-shot that already entered");
        conditional.Status.Should().NotBe(ConditionalStatus.Pending, "Pending is the only status the firing watcher fires on");
        conditional.FiredOrderId.Should().NotBeNull("the entry that actually executed is journaled and linked");
        Order journaled = await reload.Orders.FirstAsync(o => o.Id == conditional.FiredOrderId!.Value);
        journaled.Status.Should().Be(OrderStatus.Filled, "the venue reports the tag filled");
        journaled.VenueOrderKey.Should().Be("VENUE-KEY-99");
        journaled.EntryMethod.Should().Be(OrderEntryMethod.Conditional);
        (await reload.StopPlans.AnyAsync(p => p.OrderId == journaled.Id)).Should().BeFalse(
            "the account is provably flat, so this fill already round-tripped -- there is no live position to protect");
    }

    [Fact]
    public async Task ReconcileFiringConditional_ShouldResolveFired_WhenTheVenueReportsOnlyAPartialFill()
    {
        // A partial fill is still a fill: the order reached the market and entered a position, so re-arming would
        // still be a second entry. The veto must not be conditional on the fill being complete.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await SeedFiringConditionalAsync(accountId);

        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]);
        A.CallTo(() => _venue.FindFilledOrderByTagAsync(
                A<VenueAccountId>._, conditionalId.ToString(), A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(TaggedFillEvidence.Filled(conditionalId.ToString(), 1m, 5300m, "VENUE-KEY-PARTIAL"));

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileFiringConditionalAsync(
            conditionalId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.FirstAsync(c => c.Id == conditionalId)).Status.Should().Be(
            ConditionalStatus.Fired, "any executed quantity means the fire entered the market");
    }

    [Fact]
    public async Task ReconcileFiringConditional_ShouldRefuseAndLeaveFiring_WhenTheFillHistoryCannotBeRead()
    {
        // gh#631: "we could not tell" is never "it did not fill". If the fill read fails while everything else looked
        // clean, releasing would re-arm the conditional on a guess -- so it stays Firing for the operator, exactly as
        // an unreachable venue is treated by the two reads above.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await SeedFiringConditionalAsync(accountId);

        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]);
        A.CallTo(() => _venue.FindFilledOrderByTagAsync(
                A<VenueAccountId>._, A<string>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("gateway order-history read failed"));

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileFiringConditionalAsync(
            conditionalId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.FirstAsync(c => c.Id == conditionalId)).Status.Should().Be(
            ConditionalStatus.Firing, "an unreadable fill history leaves the row stranded rather than re-armed on an unknown");
    }

    [Fact]
    public async Task ReconcileFiringConditional_ShouldRefuseAndLeaveFiring_WhenNothingRestsButAPositionIsOpen()
    {
        // gh#589 round-2: "no working order under the tag" is NOT "nothing live" -- the fire may have FILLED before the
        // reconcile. Releasing then would re-fire over an untracked open position. A reachable venue with nothing under
        // the tag but an OPEN position refuses: the conditional stays Firing, and the operator resolves the position first.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await SeedFiringConditionalAsync(accountId);

        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]); // nothing under the tag...
        VenueContractId contract = VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26");
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(
                [new PositionSnapshot(_venueAccount, contract, NetQuantity: 1, new Price(5300m))]); // ...but a position IS open

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileFiringConditionalAsync(
            conditionalId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.FirstAsync(c => c.Id == conditionalId)).Status.Should().Be(
            ConditionalStatus.Firing, "a possibly-filled fire is never released over an open position — the conditional stays Firing (gh#589)");
        (await reload.Orders.AnyAsync()).Should().BeFalse("nothing is adopted when there is no working order to match");
    }

    [Fact]
    public async Task ReconcileFiringConditional_ShouldRefuseAndLeaveFiring_WhenTheVenueIsUnreachable()
    {
        // gh#589 + gh#381: "we could not ask" is not "nothing is there". A stranded Firing is NEVER resolved against an
        // unknown book -- releasing could re-fire a live order, adopting could invent a phantom. The conditional stays Firing.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await SeedFiringConditionalAsync(accountId);

        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(() => new InvalidOperationException("venue unreachable (test)")); // -> Basis Unknown

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileFiringConditionalAsync(
            conditionalId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.FirstAsync(c => c.Id == conditionalId)).Status.Should().Be(
            ConditionalStatus.Firing, "an unknown venue book is never used to resolve a stranded fire");
    }

    [Fact]
    public async Task ReconcileFiringConditional_ShouldRefuse_WhenTheConditionalIsNotFiring()
    {
        // Only a stranded Firing is reconcilable -- a Pending conditional (or any other status) is resolved through its
        // own paths (the watcher fires / cancels / expires it), so reconcile stays a narrow recovery.
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await SeedFiringConditionalAsync(accountId, ConditionalStatus.Pending);

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.ReconcileFiringConditionalAsync(
            conditionalId, new FixedUser(_operator), context, RestingOrders(), Positions(), Fills(), ExecOptions(), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Take_ShouldRefuse_WhenTheAccountHasAMidFireFiringConditional()
    {
        // gh#589: the no-stacking check counts a mid-fire Firing conditional, not only an outstanding Order. A Firing
        // conditional is a maybe-live entry with no Order row, so a take that ignored it would size against a flat
        // snapshot. The take is refused (409) before the claim, so the ticket stays Staged and takeable.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context()) { orderId = (await read.Orders.SingleAsync()).Id; }
        await SeedFiringConditionalAsync(accountId);

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.FirstAsync(o => o.Id == orderId)).Status.Should().Be(
            OrderStatus.Staged, "the no-stacking check counts the Firing conditional and refuses before the claim");
    }

    private async Task<Guid> SeedFiringConditionalAsync(Guid accountId, ConditionalStatus status = ConditionalStatus.Firing)
    {
        // A conditional in a chosen lifecycle state (default Firing -- the stranded mid-fire). Minimal but valid: the
        // no-stacking check reads AccountId + Status; the reconcile adopt reads the trade fields to journal the Order.
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext seed = Context();
        seed.ConditionalOrders.Add(new ConditionalOrderRecord
        {
            Id = id,
            UserId = _operator,
            AccountId = accountId,
            Instrument = "CON.F.US.MES.U26",
            Symbol = "MES",
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Market,
            EntryPrice = 5300m,
            WorkingStopPrice = 5295m,
            SafetyStopPrice = 5290m,
            ReferencePrice = 5300m,
            TickSize = 0.25m,
            PointValue = 5m,
            TriggerPrice = 5310m,
            TriggerDirection = ConditionalCrossDirection.RisesTo,
            Status = status,
            Mode = TradingMode.Practice,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        await seed.SaveChangesAsync();
        return id;
    }

    private async Task SetTakingAsync(Guid orderId)
    {
        // Strand a staged ticket in Taking, as a take interrupted mid-send leaves it -- via the same claim (Staged ->
        // Taking) the take uses, so the row's status transition matches production.
        await using TradingCopilotDbContext context = Context();
        (await Claim(context).TryClaimAsync(orderId, CancellationToken.None)).Should().BeTrue();
    }

    private async Task SeedWorkingOrderAsync(Guid accountId)
    {
        await using TradingCopilotDbContext seed = Context();
        seed.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            AccountId = accountId,
            Instrument = "CON.F.US.MES.U26",
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Market,
            Status = OrderStatus.Working,
            Mode = TradingMode.Practice,
            VenueOrderKey = "999999",
            PlacedAt = DateTimeOffset.UnixEpoch,
        });
        await seed.SaveChangesAsync();
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
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

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
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

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
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

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
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

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
            IResult cancelled = await OrderEndpoints.CancelOrderAsync(
                orderId, context, _factory, PxOptions(), A.Fake<IAuditLog>(), NullLoggerFactory.Instance, CancellationToken.None);
            StatusOf(cancelled).Should().Be(StatusCodes.Status200OK);
        }

        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).Status.Should().Be(OrderStatus.Cancelled);

        await using TradingCopilotDbContext again = Context();
        IResult second = await OrderEndpoints.CancelOrderAsync(
            orderId, again, _factory, PxOptions(), A.Fake<IAuditLog>(), NullLoggerFactory.Instance, CancellationToken.None);
        StatusOf(second).Should().Be(StatusCodes.Status409Conflict); // cancelled is not staged/working -- no silent no-op
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
                _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, CancellationToken.None);
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
                A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);
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
                _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, CancellationToken.None);
        }
        await using (TradingCopilotDbContext take = Context())
        {
            await OrderEndpoints.TakeStagedOrderAsync(
                orderId, new FixedUser(_operator), take, _factory, PxOptions(), ExecOptions(), Development,
                A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, Claim(take), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);
        }

        // The deviation is carried all the way to the placed order -- a reader sees a modified take, not an armed one.
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).EntryMethod.Should().Be(OrderEntryMethod.ModifiedTake);
    }

    // --- The concurrent-take claim (gh#530, P1) ---

    [Fact]
    public async Task Take_ShouldRefuseWithoutTouchingTheVenue_WhenAnotherTakeHoldsTheClaim()
    {
        // THE defect. Two takes both read Staged against their own change trackers, both passed the status check,
        // and BOTH TRANSMITTED -- then both wrote this one row, so one live venue order ended up on no Order row:
        // invisible to cancel, to the kill-switch sweep and to the orphan guard, which all resolve by row id.
        //
        // The loser must refuse BEFORE the venue. A post-venue re-read is too late by construction.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context())
        {
            orderId = (await read.Orders.SingleAsync()).Id;
        }

        await using TradingCopilotDbContext context = Context();
        IResult result = await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development,
            A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, LostClaim(), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Take_ShouldHandTheClaimBack_WhenTheFreshGateRefuses()
    {
        // A refused take must leave the ticket exactly as takeable as it was. Without the release the claim would
        // strand it mid-take forever -- trading one silent failure for another.
        Guid accountId = await SeedAccountAsync();
        await ArmAsync(accountId, SmallBuy());
        Guid orderId;
        await using (TradingCopilotDbContext read = Context())
        {
            orderId = (await read.Orders.SingleAsync()).Id;
        }

        IKillSwitch engaged = A.Fake<IKillSwitch>();
        A.CallTo(() => engaged.IsEngaged).Returns(true);

        await using TradingCopilotDbContext context = Context();
        await OrderEndpoints.TakeStagedOrderAsync(
            orderId, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development,
            engaged, NullExecutionMetrics.Instance, Claim(context), PassthroughGuard(), NullLoggerFactory.Instance, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).Status.Should().Be(
            OrderStatus.Staged, "a take that never reached the venue leaves the ticket takeable");
    }

    /// <summary>
    /// The account-entry guard (gh#589) that just runs the callback: the in-memory provider is single-threaded and
    /// cannot run the real advisory lock (that is why it is a seam). The deterministic no-stacking check INSIDE the
    /// callback is what the unit tier proves; the real cross-request serialization is proven on Postgres by QA.
    /// </summary>
    private static IAccountEntryGuard PassthroughGuard()
    {
        IAccountEntryGuard guard = A.Fake<IAccountEntryGuard>();
        A.CallTo(() => guard.RunExclusiveAsync<IResult>(
                A<TradingCopilotDbContext>._, A<Guid>._, A<Func<Task<IResult>>>._, A<CancellationToken>._))
            .ReturnsLazily((TradingCopilotDbContext _, Guid _, Func<Task<IResult>> transmit, CancellationToken _) => transmit());
        return guard;
    }

    /// <summary>A claim that always loses — the second of two concurrent takes.</summary>
    private static IStagedOrderClaim LostClaim()
    {
        IStagedOrderClaim claim = A.Fake<IStagedOrderClaim>();
        A.CallTo(() => claim.TryClaimAsync(A<Guid>._, A<CancellationToken>._)).Returns(false);
        return claim;
    }

    /// <summary>The gh#530 claim, faithful to production over the in-memory provider.</summary>
    /// <remarks>
    /// Production evaluates the claim as a conditional UPDATE in Postgres, which the in-memory provider cannot run
    /// (that is exactly why the seam exists). This double reproduces its OBSERVABLE contract -- claim only from
    /// Staged, release only from Taking, commit immediately -- so these guards see the same status transitions the
    /// endpoint drives in production. What it cannot reproduce is ATOMICITY under real concurrency; that is proven
    /// in the integration tier, against a real database, which is the only place it can be.
    /// </remarks>
    private static IStagedOrderClaim Claim(TradingCopilotDbContext database) => new InMemoryClaim(database);

    private sealed class InMemoryClaim(TradingCopilotDbContext database) : IStagedOrderClaim
    {
        public async Task<bool> TryClaimAsync(Guid orderId, CancellationToken cancellationToken)
        {
            Order? order = await database.Orders
                .FirstOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
            if (order is null || order.Status != OrderStatus.Staged)
            {
                return false;
            }

            order.Status = OrderStatus.Taking;
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken)
        {
            Order? order = await database.Orders
                .FirstOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
            if (order is { Status: OrderStatus.Taking })
            {
                order.Status = OrderStatus.Staged;
                await database.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
