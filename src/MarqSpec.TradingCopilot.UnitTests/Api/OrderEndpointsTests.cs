using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Orders;
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
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// The <c>POST /accounts/{id}/orders</c> handler (gh#11, S3 increment 1) — the #74 gated send path finally
/// composed at the API. The safety-relevant behaviours: no declared risk profile ⇒ no send (fail-closed input),
/// a non-flat account ⇒ no send (unrealized P&amp;L would be guessed), a gate block persists its
/// <c>GateDecisionRecord</c> but never an <c>Order</c> row, and a placed order journals BOTH under the DB mode
/// guard.
/// </summary>
public class OrderEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly IAccountEntryGuard _entryGuard = A.Fake<IAccountEntryGuard>();
    private readonly VenueAccountId _venueAccount = VenueAccountId.Create(VenueId.Parse("projectx"), "9001");

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public OrderEndpointsTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);

        // The executor's own venue identity -- the service's mismatch guard compares every handle against it,
        // and an unconfigured fake (default VenueId) reads as a foreign venue and refuses everything.
        A.CallTo(() => _venue.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders)); // can hold the safety stop (gh#11 inc 3)

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
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));

        // The account-entry guard (gh#531) just runs the callback here: the in-memory provider is single-threaded
        // and cannot run the real advisory lock (that is why it is a seam). The deterministic no-stacking check
        // INSIDE the callback is what the unit tier proves; the real serialization is proven on Postgres by QA.
        A.CallTo(() => _entryGuard.RunExclusiveAsync<IResult>(
                A<TradingCopilotDbContext>._, A<Guid>._, A<Func<Task<IResult>>>._, A<CancellationToken>._))
            .ReturnsLazily((TradingCopilotDbContext _, Guid _, Func<Task<IResult>> transmit, CancellationToken _) => transmit());
    }

    private TradingCopilotDbContext Context(Guid? asUser = null)
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(asUser ?? _operator));
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static IOptions<ProjectXConnectionOptions> PxOptions(string credentialKey = "topstep-main") =>
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey });

    private static IOptions<ExecutionOptions> ExecOptions() =>
        Microsoft.Extensions.Options.Options.Create(new ExecutionOptions());

    private static HostTradingEnvironment Development { get; } = new(DeploymentEnvironment.Development);

    /// <summary>An MES buy risking 5 points to make 10, well inside every default cap.</summary>
    private static SendOrderRequest SmallBuy(int quantity = 1) => new(
        Symbol: "MES",
        TickSize: 0.25m,
        PointValue: 5m,
        Side: OrderSide.Buy,
        Quantity: quantity,
        Entry: 5300m,
        Stop: 5295m,
        SafetyStop: 5290m,
        ReferencePrice: 5300m,
        Type: OrderType.Market);

    private async Task<Guid> SeedAsync(
        TradingMode mode = TradingMode.Practice,
        bool withRiskProfile = true,
        string credentialKey = "topstep-main",
        DefaultEntryAction defaultEntryAction = DefaultEntryAction.ApproveAndArm,
        bool connectionActive = true,
        bool accountActive = true)
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
            CredentialKey = credentialKey,
            IsActive = connectionActive,
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = mode,
            CanTrade = true,
            IsVisible = true,
            Balance = 50_000m,
            IsActive = accountActive,
        });
        if (withRiskProfile)
        {
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
                DefaultEntryAction = defaultEntryAction,
            });
        }

        await context.SaveChangesAsync();
        return accountId;
    }

    private async Task<IResult> SendAsync(Guid accountId, SendOrderRequest request, string configuredKey = "topstep-main")
    {
        await using TradingCopilotDbContext context = Context();
        return await OrderEndpoints.SendOrderAsync(
            accountId, request, new FixedUser(_operator), context, _factory, PxOptions(configuredKey), ExecOptions(), Development, A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, _entryGuard, CancellationToken.None);
    }

    /// <summary>Seeds one existing order on the account in the given lifecycle status (gh#531 no-stacking fixtures).</summary>
    private async Task<Guid> SeedOutstandingOrderAsync(Guid accountId, OrderStatus status)
    {
        Guid orderId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = _operator,
            AccountId = accountId,
            Instrument = "CON.F.US.MES.U26",
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Market,
            Status = status,
            Mode = TradingMode.Practice,
            PlacedAt = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync();
        return orderId;
    }

    private async Task SeedFiringConditionalAsync(Guid accountId)
    {
        // A mid-fire Firing conditional (gh#589): a maybe-live entry with NO Order row. The no-stacking check reads only
        // AccountId + Status, so a minimal-but-valid record suffices to block a send.
        await using TradingCopilotDbContext context = Context();
        context.ConditionalOrders.Add(new ConditionalOrderRecord
        {
            Id = Guid.NewGuid(),
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
            Status = ConditionalStatus.Firing,
            Mode = TradingMode.Practice,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SendOrder_ShouldReturnNotFound_ForAnUnknownAccount()
    {
        await SeedAsync();

        StatusOf(await SendAsync(Guid.NewGuid(), SmallBuy())).Should().Be(StatusCodes.Status404NotFound);
    }

    // --- A deactivated connection / account is not a usable send path (gh#322, R-17, ADR-0015) ---

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenTheConnectionIsDeactivated()
    {
        // gh#322 regression guard (found by QA gh#229): soft-delete exists so an operator can retire a login while
        // keeping its journal. If a retired connection still routes live orders the deactivation is cosmetic — on a
        // live real-money account, a real hazard. Refused at the choke point, BEFORE the gate sizes or the venue is
        // touched, exactly as the credential-rotation endpoint already refuses a deactivated connection.
        Guid accountId = await SeedAsync(connectionActive: false);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.GateDecisions.AnyAsync()).Should().BeFalse("refused before the gate ever sized it");
        (await reload.Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenTheAccountIsDeactivated()
    {
        // The cascade half of the same guard: DELETE /connections/{id} deactivates the connection AND its accounts,
        // so an account deactivated on its own (or by that cascade) must refuse too — checking only the connection
        // would leave the cascaded rows a usable path.
        Guid accountId = await SeedAsync(accountActive: false);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.GateDecisions.AnyAsync()).Should().BeFalse("refused before the gate ever sized it");
        (await reload.Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenNoRiskProfileIsDeclared()
    {
        Guid accountId = await SeedAsync(withRiskProfile: false);

        IResult result = await SendAsync(accountId, SmallBuy());

        // The fail-closed input working as designed (gh#10): no declared limits, no send -- the gate never
        // fabricates permissive defaults.
        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenThisProcessHoldsADifferentCredentialKey()
    {
        Guid accountId = await SeedAsync(credentialKey: "someone-elses-login");

        IResult result = await SendAsync(accountId, SmallBuy(), configuredKey: "topstep-main");

        // One credential set per process (ADR-0015) -- the same guard discovery enforces, on the send path.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenTheAccountIsNotFlat()
    {
        Guid accountId = await SeedAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(
            [
                new PositionSnapshot(_venueAccount, VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26"), 1, new Price(5290m)),
            ]);

        IResult result = await SendAsync(accountId, SmallBuy());

        // Increment-1 honesty (gh#11): the venue reports no unrealized P&L, and guessing zero flatters a red
        // day. Flat is the only state where UnrealizedPnL = 0 is a fact rather than a guess.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- No stacking a new entry on an outstanding one (gh#531): the deterministic half of the direct-send race fix.
    //     Two concurrent sends each compose a FLAT snapshot (ComposeAsync reserves nothing for a working order), both
    //     pass the gate, both transmit -> up to 2x approved risk. The check below refuses a send when the account is
    //     already committed to an entry; the per-account guard holds it + the place under one lock. ---

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenTheAccountHasAnOutstandingWorkingOrder()
    {
        // ComposeAsync sizes off a flat account and reserves nothing for a working order (it moves neither positions
        // nor balance until it fills). A second send would therefore size against the same flat snapshot and add a
        // second entry -- so an outstanding working entry means the account is no longer clearly flat, and a new send
        // is refused (409) before the venue is touched, with no new order row journaled.
        Guid accountId = await SeedAsync();
        await SeedOutstandingOrderAsync(accountId, OrderStatus.Working);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.CountAsync()).Should().Be(1); // no NEW row -- only the outstanding order remains
    }

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenTheAccountHasAPartiallyFilledOrder()
    {
        // A partial fill is live exposure PLUS a still-working remainder -- doubly non-flat, so the same refusal.
        Guid accountId = await SeedAsync();
        await SeedOutstandingOrderAsync(accountId, OrderStatus.PartiallyFilled);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendOrder_ShouldBeBlockedByATakingRow_BecauseGh589CountsTakingNow()
    {
        // The deliberate flip the pre-gh#589 test warned would break here. gh#531 EXCLUDED Taking because a stranded
        // take had no recovery, so counting it would dead-lock the account's send path. gh#589 makes a stranded Taking
        // recoverable -- ANY post-send venue fault (rejection / timeout / disconnect) leaves it Taking + loud (only a
        // pre-venue compose fault releases the claim, since nothing was placed), resolved via POST /orders/{id}/reconcile,
        // and a Taking surviving a restart fails the rehydration safe + loud -- so a take in flight is now counted as the
        // imminent exposure it is: a direct send on an account with a Taking row is REFUSED, closing send-vs-take.
        Guid accountId = await SeedAsync();
        await SeedOutstandingOrderAsync(accountId, OrderStatus.Taking);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenTheAccountHasAMidFireFiringConditional()
    {
        // gh#589: the no-stacking check counts a mid-fire Firing conditional, not only an outstanding Order. A Firing
        // conditional is a maybe-live entry with NO Order row (its fire faulted before journaling), so a send that
        // ignored it would size against a flat snapshot. The direct send is REFUSED, closing send-vs-stranded-fire.
        Guid accountId = await SeedAsync();
        await SeedFiringConditionalAsync(accountId);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendOrder_ShouldPlace_WhenAStagedOrderExists()
    {
        // A Staged ticket is server-side only -- unsent, no venue exposure -- so it never makes the account non-flat
        // and never blocks a direct send (the gh#531 carve-out). Single-send behaviour is unchanged.
        Guid accountId = await SeedAsync();
        await SeedOutstandingOrderAsync(accountId, OrderStatus.Staged);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenTheAccountHasAnEntryInAnUnrecognizedStatus()
    {
        // Fail-closed (gh#531 review): the no-stacking check is an allow-list of safe-to-ignore states and blocks
        // EVERYTHING else -- including a status it does not recognise. A future OrderStatus (or a corrupt/Unknown
        // row) that carries exposure must not fall through to "proceed" and silently reopen the 2x race. A cast
        // out-of-range value stands in for "a status added later that nobody classified in the check."
        Guid accountId = await SeedAsync();
        await SeedOutstandingOrderAsync(accountId, (OrderStatus)99);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.CountAsync()).Should().Be(1); // no NEW row -- only the unrecognized-status order remains
    }

    [Fact]
    public async Task SendOrder_ShouldDoNothing_WhenTheGuardDoesNotRunTheCallback()
    {
        // The whole correctness of gh#531 rests on the transmit tail -- the no-stacking check AND the place -- living
        // INSIDE the guard's callback, so that under the real advisory lock the check runs while the lock is held.
        // This pins that: a guard that declines to run the callback produces NO venue place and NO order row. A
        // refactor that moved the check or the place OUTSIDE RunExclusiveAsync (reopening the TOCTOU race) fails here.
        Guid accountId = await SeedAsync();
        A.CallTo(() => _entryGuard.RunExclusiveAsync<IResult>(
                A<TradingCopilotDbContext>._, A<Guid>._, A<Func<Task<IResult>>>._, A<CancellationToken>._))
            .Returns(Results.Conflict(new { error = "guard declined" }));

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SendOrder_ShouldPlace_WhenAPriorOrderIsTerminal()
    {
        // The check counts only OUTSTANDING entries. A Filled order is already realized into the venue balance
        // ComposeAsync reads, and a Cancelled/Rejected one never rests as exposure -- none is risk a new send must
        // reserve around. Staged is unsent and server-side only. So a send whose only prior order is terminal
        // proceeds exactly as a first send does: single-request behaviour is unchanged.
        Guid accountId = await SeedAsync();
        await SeedOutstandingOrderAsync(accountId, OrderStatus.Filled);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.CountAsync()).Should().Be(2); // the terminal one plus the newly placed entry
    }

    [Fact]
    public async Task SendOrder_ShouldRunUnderTheAccountEntryGuard()
    {
        // The per-account lock (gh#531) that serializes the no-stacking check + the place must actually wrap the
        // transmit tail, so two racers cannot both pass the check before either journals. Here we assert the send
        // runs through the guard for THIS account; the real advisory-lock serialization is proven on Postgres by QA.
        Guid accountId = await SeedAsync();

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _entryGuard.RunExclusiveAsync<IResult>(
                A<TradingCopilotDbContext>._, accountId, A<Func<Task<IResult>>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendOrder_ShouldPersistTheBlockingDecision_AndNeverAnOrderRow_WhenTheGateBlocks()
    {
        Guid accountId = await SeedAsync();

        // Ten contracts against a 3-contract manual cap and a 300-per-trade budget: the gate must block or
        // resize; either way this proposal's full size never survives. Risking 100 points on MES ($500/contract)
        // makes even one contract exceed MaxDrawdownPerTrade -- an outright block.
        SendOrderRequest oversized = SmallBuy(quantity: 10) with { Stop = 5200m, SafetyStop = 5200m };

        IResult result = await SendAsync(accountId, oversized);

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();

        await using TradingCopilotDbContext reload = Context();
        GateDecisionRecord decision = await reload.GateDecisions.SingleAsync();
        decision.Outcome.Should().Be(GateOutcome.Blocked);
        decision.AccountId.Should().Be(accountId);
        decision.OrderId.Should().BeNull();                       // blocked-by-gate: decision row only,
        (await reload.Orders.AnyAsync()).Should().BeFalse();      // never an order row
    }

    [Fact]
    public async Task SendOrder_ShouldPlaceAndJournalBoth_WhenTheGateAllows()
    {
        Guid accountId = await SeedAsync();

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext reload = Context();
        Order order = await reload.Orders.SingleAsync();
        order.AccountId.Should().Be(accountId);
        order.Mode.Should().Be(TradingMode.Practice);  // the DB mode-guard trigger's first real producer
        order.Status.Should().Be(OrderStatus.Working);
        order.VenueOrderKey.Should().Be("889001");
        order.UserId.Should().Be(_operator);

        GateDecisionRecord decision = await reload.GateDecisions.SingleAsync();
        decision.Outcome.Should().Be(GateOutcome.Allowed);
        decision.OrderId.Should().Be(order.Id);        // the audit pair, linked
    }

    [Fact]
    public async Task SendOrder_ShouldTransmitAndJournalTheTakeProfitTarget()
    {
        // gh#173: a target on the send DTO reaches the venue as the bracket's profit leg AND is journaled on
        // the order row — so a placed order records the exit the operator asked for.
        Guid accountId = await SeedAsync();
        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest r, CancellationToken _) => sent = r)
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));

        IResult result = await SendAsync(accountId, SmallBuy() with { Target = 5310m }); // winning side for a long

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        sent!.ProfitTarget.Should().Be(new Price(5310m));
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).TakeProfitPrice.Should().Be(5310m);
    }

    [Fact]
    public async Task SendOrder_ShouldAuthorizeAgainstTheDeclaredMode_WhenTheVenueClaimsADifferentOne()
    {
        // The R-14 mode-source guard (gh#148, found by the gh#135 suite). ComposeAsync must hand the execution
        // service the operator's DECLARED mode, not the one the venue reports: gh#60 removed the venue flag as
        // the stake signal after it misclassified all 293 accounts on a real login.
        //
        // Declared Practice + venue claims Live, in Development: Practice trades anywhere, Live nowhere outside
        // production -- so the send succeeds only if the DECLARED mode is what reaches TradingModePolicy.
        // Reverting the override makes this fail 409 RefusedByMode.
        Guid accountId = await SeedAsync(mode: TradingMode.Practice);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Live)
            {
                Stage = AccountStage.Practice,
            },
        ]);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        // The journaled row carries the declared mode too -- the DB mode guard compares against that (gh#7).
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).Mode.Should().Be(TradingMode.Practice);
    }

    [Fact]
    public async Task SendOrder_ShouldRefuseADeclaredLiveAccount_EvenWhenTheVenueClaimsItIsPractice()
    {
        // The DANGEROUS direction of the same seam (gh#148): the venue calls a live account "practice" -- the
        // gh#60 failure exactly, where the venue's flag classified real funded accounts as practice. If the
        // guard read the venue's claim it would authorize a real-money trade from a Development host.
        Guid accountId = await SeedAsync(mode: TradingMode.Live);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "EXPRESS-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice)
            {
                Stage = AccountStage.Funded,
            },
        ]);

        IResult result = await SendAsync(accountId, SmallBuy());

        // Refused before sizing: Live may not be traded outside production, whatever the venue calls it.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.AnyAsync()).Should().BeFalse();
        (await reload.GateDecisions.AnyAsync()).Should().BeFalse(); // never sized, so no decision exists
    }

    [Fact]
    public async Task SendOrder_ShouldRecordAHiddenStopPlan_WhenTheSafetyStopSitsBeyondTheWorkingStop()
    {
        // ADR-0007 staging (gh#11): the working stop starts HIDDEN -- keeping the entry off the book -- while
        // the safety stop already rests natively as the entry's protective bracket (inc 3).
        Guid accountId = await SeedAsync();

        await SendAsync(accountId, SmallBuy()); // entry 5300, working 5295, safety 5290

        await using TradingCopilotDbContext reload = Context();
        StopPlanRecord plan = await reload.StopPlans.SingleAsync();
        Order order = await reload.Orders.SingleAsync();
        plan.OrderId.Should().Be(order.Id);
        plan.Staging.Should().Be(StopStaging.Hidden);
        plan.ActualStopPrice.Should().Be(5_295m);
        plan.SafetyStopPrice.Should().Be(5_290m);   // beyond the working stop -- the catastrophic floor
        plan.ProximityMetric.Should().Be(StopProximityMetric.Ticks);
        plan.UserId.Should().Be(_operator);
    }

    [Fact]
    public async Task SendOrder_ShouldRecordNoStopPlan_WhenTheWorkingAndSafetyStopsCoincide()
    {
        // Nothing to stage: the operator's working stop IS the safety stop, and it already rests natively.
        // Inventing a plan here would assert a promotion that can never be meaningful.
        Guid accountId = await SeedAsync();

        await SendAsync(accountId, SmallBuy() with { Stop = 5_290m, SafetyStop = 5_290m });

        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.AnyAsync()).Should().BeTrue();      // the order still placed...
        (await reload.StopPlans.AnyAsync()).Should().BeFalse();  // ...with no staging plan
    }

    [Fact]
    public async Task SendOrder_ShouldRefusePreGate_WhenTheAccountModeIsUndeclared()
    {
        Guid accountId = await SeedAsync(mode: TradingMode.Undeclared);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "50KTC-V2", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Undeclared) { Stage = AccountStage.Unknown },
        ]);

        IResult result = await SendAsync(accountId, SmallBuy());

        // Undeclared trades nowhere (gh#60) -- refused before sizing; no decision exists, so none is persisted.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.GateDecisions.AnyAsync()).Should().BeFalse();
        (await reload.Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SendOrder_ShouldJournalTheDirectSendAsManual()
    {
        Guid accountId = await SeedAsync();

        await SendAsync(accountId, SmallBuy());

        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).EntryMethod.Should().Be(OrderEntryMethod.Manual);
    }

    // --- Send-as-is fast path (gh#181) ---

    private async Task<IResult> SendAsIsAsync(
        Guid accountId, SendOrderRequest request, string configuredKey = "topstep-main", IKillSwitch? killSwitch = null)
    {
        await using TradingCopilotDbContext context = Context();
        return await OrderEndpoints.SendAsIsOrderAsync(
            accountId, request, new FixedUser(_operator), context, _factory, PxOptions(configuredKey), ExecOptions(),
            Development, killSwitch ?? A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, _entryGuard, CancellationToken.None);
    }

    [Fact]
    public async Task SendAsIs_ShouldPlaceAndJournalAsSendAsIs_WhenTheGateAllows()
    {
        Guid accountId = await SeedAsync();

        IResult result = await SendAsIsAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext reload = Context();
        Order order = await reload.Orders.SingleAsync();
        order.Status.Should().Be(OrderStatus.Working);
        order.EntryMethod.Should().Be(OrderEntryMethod.SendAsIs); // a reader can tell it from an armed / modified take
        GateDecisionRecord decision = await reload.GateDecisions.SingleAsync();
        decision.Outcome.Should().Be(GateOutcome.Allowed);
        decision.OrderId.Should().Be(order.Id); // a sized attempt always leaves the audit pair
    }

    [Fact]
    public async Task SendAsIs_ShouldTransmitTheApprovedQuantity_NotTheRequested()
    {
        // The invariant that keeps the gate enforcing, not advisory: five requested, the 3-contract manual cap
        // resizes, and it is the APPROVED quantity that reaches the venue and the journal.
        Guid accountId = await SeedAsync();
        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest r, CancellationToken _) => sent = r)
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));

        await SendAsIsAsync(accountId, SmallBuy(quantity: 5));

        sent!.Quantity.Should().Be(3);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).Size.Should().Be(3);
    }

    [Fact]
    public async Task SendAsIs_ShouldRefuseByKillSwitch_BeforeSizing()
    {
        Guid accountId = await SeedAsync();
        IKillSwitch engaged = A.Fake<IKillSwitch>();
        A.CallTo(() => engaged.IsEngaged).Returns(true);

        IResult result = await SendAsIsAsync(accountId, SmallBuy(), killSwitch: engaged);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.GateDecisions.AnyAsync()).Should().BeFalse(); // refused at the choke point, before the gate sized
        (await reload.Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsIs_ShouldRefuse_WhenNoRiskProfileIsDeclared()
    {
        Guid accountId = await SeedAsync(withRiskProfile: false);

        IResult result = await SendAsIsAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity); // absence is refusal, not a default
        (await Context().Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsIs_ShouldRefuse_WhenThisProcessHoldsADifferentCredentialKey()
    {
        Guid accountId = await SeedAsync(credentialKey: "another-firm");

        IResult result = await SendAsIsAsync(accountId, SmallBuy(), configuredKey: "topstep-main");

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict); // one credential set per process (ADR-0015)
    }

    [Fact]
    public async Task SendAsIs_ShouldRefuse_WhenTheAccountIsNotFlat()
    {
        Guid accountId = await SeedAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(
            [
                new PositionSnapshot(_venueAccount, VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26"), 2, new Price(5300m)),
            ]);

        IResult result = await SendAsIsAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict); // an open position makes the risk state a guess
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsIs_ShouldRefuse_WhenTheAccountHasAnOutstandingWorkingOrder()
    {
        // The send-as-is fast path shares TransmitAsync, so it inherits the same no-stacking refusal (gh#531): an
        // outstanding working entry blocks a second send on this path just as it does on the manual send path.
        Guid accountId = await SeedAsync();
        await SeedOutstandingOrderAsync(accountId, OrderStatus.Working);

        IResult result = await SendAsIsAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.CountAsync()).Should().Be(1); // no NEW row -- only the outstanding order remains
    }

    [Fact]
    public async Task SendAsIs_ShouldRefuse_WhenTheVenueCannotHoldTheSafetyStop()
    {
        Guid accountId = await SeedAsync();
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.Quotes)); // no BracketOrders

        IResult result = await SendAsIsAsync(accountId, SmallBuy());

        // Better no trade than an unprotected one -- RefusedByUnprotectableStop maps to a conflict; nothing journaled.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        (await Context().Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsIs_ShouldRefusePreGate_WhenTheAccountModeIsUndeclared()
    {
        Guid accountId = await SeedAsync(mode: TradingMode.Undeclared);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "50KTC-V2", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Undeclared) { Stage = AccountStage.Unknown },
        ]);

        IResult result = await SendAsIsAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict); // undeclared trades nowhere (R-14)
        (await Context().Orders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsIs_ShouldPersistTheBlockingDecision_AndNeverAnOrderRow_WhenTheGateBlocks()
    {
        Guid accountId = await SeedAsync();
        SendOrderRequest oversized = SmallBuy(quantity: 10) with { Stop = 5200m, SafetyStop = 5200m };

        IResult result = await SendAsIsAsync(accountId, oversized);

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        await using TradingCopilotDbContext reload = Context();
        GateDecisionRecord decision = await reload.GateDecisions.SingleAsync();
        decision.Outcome.Should().Be(GateOutcome.Blocked);
        decision.OrderId.Should().BeNull();                  // blocked-by-gate: decision row only,
        (await reload.Orders.AnyAsync()).Should().BeFalse(); // never an orphaned order row
    }

    [Fact]
    public async Task SendAsIs_ShouldTransmitTheApprovedQuantity_EvenWhenTheDefaultEntryActionIsSendAsIs()
    {
        // The DefaultEntryAction preference (gh#218) is a UX hint on the risk profile; it never reaches the gate.
        // With SendAsIs as the default, the same resizable ticket still resizes to the approved 3 -- identical to
        // the ApproveAndArm case in SendAsIs_ShouldTransmitTheApprovedQuantity_NotTheRequested above.
        Guid accountId = await SeedAsync(defaultEntryAction: DefaultEntryAction.SendAsIs);
        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest r, CancellationToken _) => sent = r)
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));

        await SendAsIsAsync(accountId, SmallBuy(quantity: 5));

        sent!.Quantity.Should().Be(3); // the preference does not bind the gate -- same approved quantity either way
    }
}
