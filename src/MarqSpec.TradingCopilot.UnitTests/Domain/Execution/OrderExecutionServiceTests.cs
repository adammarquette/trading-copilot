using FakeItEasy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Execution;

/// <summary>
/// The one path an order can take to a broker. Every case here is about what must <b>not</b> reach the venue —
/// this is where "the model proposes, the gate decides" stops being architecture and becomes behaviour.
/// </summary>
public class OrderExecutionServiceTests
{
    private static VenueId Venue => VenueId.Parse("projectx");

    private readonly IRiskGate _gate = A.Fake<IRiskGate>();
    private readonly IOrderExecutor _venue = A.Fake<IOrderExecutor>();
    private readonly OrderExecutionService _service;

    public OrderExecutionServiceTests()
    {
        // The executor has to say who it is: the service now refuses a request tagged for another venue, and an
        // unconfigured fake would report the default id and fail every case for the wrong reason.
        A.CallTo(() => _venue.Id).Returns(Venue);

        // A venue that can hold an exchange-native protective stop, unless a test says otherwise (gh#11 inc 3).
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders));

        _service = ServiceIn(DeploymentEnvironment.Development);
    }

    private static VenueAccountId AccountId => VenueAccountId.Create(Venue, "9001");

    private OrderExecutionService ServiceIn(DeploymentEnvironment environment)
    {
        // Fixed at construction, never per send: a caller able to name its own environment could walk a live
        // account through the R-14 guard from a development host.
        return new OrderExecutionService(_gate, _venue, environment);
    }

    private static VenueAccount Account(TradingMode mode)
    {
        return new VenueAccount(
            AccountId,
            mode == TradingMode.Practice ? "PRAC-50K" : "LIVE-50K",
            Balance: 50_000m,
            CanTrade: true,
            IsVisible: true,
            mode);
    }

    private static InstrumentSpec Es => InstrumentSpec.Create(InstrumentId.Parse("ES"), 0.25m, 50m);

    private static OrderProposal Proposal(int quantity = 4, Price? target = null, OrderSide side = OrderSide.Buy)
    {
        // Stops flip with side (both on the losing side of entry, safety beyond the working stop); entry is
        // 5000 either way. A take-profit target, when present, must ride the winning side.
        (Price stop, Price safety) = side == OrderSide.Buy
            ? (new Price(4_995m), new Price(4_990m))
            : (new Price(5_005m), new Price(5_010m));

        return new OrderProposal(
            Es,
            side,
            quantity,
            Entry: new Price(5_000m),
            Stop: stop,
            SafetyStop: safety,
            ReferencePrice: new Price(5_000m),
            Target: target);
    }

    private static RiskContext Context(VenueAccountId? account = null)
    {
        return new RiskContext(
            account ?? AccountId,
            new AccountRiskState(55_000m, 0m, 0m),
            TrailingDrawdown.Start(TrailingMode.EndOfDay, 5_000m, 55_000m),
            new AccountRiskRules(3_000m, null, FloorSource.FirmImposed),
            new RiskProfile(0.20m, 1.5m, 1_500m, 2_000m, null, false, SizingBasis.ActualStop),
            ManualCaps.Create(10),
            new SanityCaps(10, 10_000_000m, 40));
    }

    private static ResolvedContract EsContract =>
        new(VenueContractId.Create(Venue, "CON.F.US.EP.U26"), InstrumentId.Parse("ES"));

    private static ExecutionRequest Request(
        VenueAccount account, int quantity = 4, Price? target = null, OrderSide side = OrderSide.Buy)
    {
        return new ExecutionRequest(
            Proposal(quantity, target, side),
            EsContract,
            account,
            Context(account.Id));
    }

    private void GateReturns(GateDecision decision)
    {
        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).Returns(decision);
    }

    private void VenueAccepts(string orderId = "555")
    {
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily((OrderRequest r, CancellationToken _) =>
                Task.FromResult(new PlacedOrder(r.Account, orderId, DateTimeOffset.UnixEpoch)));
    }

    // --- The always-native safety stop (gh#11 increment 3, ADR-0007) ---

    [Fact]
    public async Task SendAsync_ShouldAttachTheSafetyStopAsAnExchangeNativeProtectiveStop()
    {
        // "A live position is never without a real exchange-held stop" (ADR-0007): every transmitted entry
        // carries its safety stop as a protective bracket, so the exchange attaches it on fill.
        GateReturns(GateDecision.Allow(4, "within every layer"));
        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest r, CancellationToken _) => sent = r)
            .ReturnsLazily((OrderRequest r, CancellationToken _) =>
                Task.FromResult(new PlacedOrder(r.Account, "555", DateTimeOffset.UnixEpoch)));

        ExecutionResult result = await _service.SendAsync(Request(Account(TradingMode.Practice)), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.Placed);
        sent!.ProtectiveStop.Should().Be(new Price(4_990m)); // the proposal's safety stop, rode with the entry
    }

    [Fact]
    public async Task SendAsync_ShouldRefuse_WhenTheVenueCannotHoldANativeProtectiveStop()
    {
        // Fail-closed: if the venue can't hold a protective stop, the position would be unprotected -- so the
        // entry is not sent at all. Better no trade than an unprotected one (ADR-0007).
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.Quotes)); // no brackets
        GateReturns(GateDecision.Allow(4, "the gate would allow it"));

        ExecutionResult result = await _service.SendAsync(Request(Account(TradingMode.Practice)), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByUnprotectableStop);
        result.Order.Should().BeNull();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- The take-profit target: the third bracket leg (gh#170, ADR-0007) ---

    [Fact]
    public async Task SendAsync_ShouldAttachTheTakeProfitTarget_AsANativeBracketLeg()
    {
        // The profit side of the OCO bracket: an entry rests with its take-profit target (on the winning side)
        // so the exchange holds it alongside the protective stop and takes the move without the operator watching.
        GateReturns(GateDecision.Allow(4, "within every layer"));
        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest r, CancellationToken _) => sent = r)
            .ReturnsLazily((OrderRequest r, CancellationToken _) =>
                Task.FromResult(new PlacedOrder(r.Account, "555", DateTimeOffset.UnixEpoch)));

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Practice), target: new Price(5_010m)), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.Placed);
        sent!.ProfitTarget.Should().Be(new Price(5_010m)); // the proposal's target, rode with the entry
    }

    [Fact]
    public async Task SendAsync_ShouldSendNoProfitTarget_WhenNoTakeProfitIsGiven()
    {
        // Regression: a null target is valid -- the two-leg bracket (entry + protective stop) still sends
        // unchanged, so this increment does not disturb every existing send.
        GateReturns(GateDecision.Allow(4, "within every layer"));
        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest r, CancellationToken _) => sent = r)
            .ReturnsLazily((OrderRequest r, CancellationToken _) =>
                Task.FromResult(new PlacedOrder(r.Account, "555", DateTimeOffset.UnixEpoch)));

        await _service.SendAsync(Request(Account(TradingMode.Practice)), CancellationToken.None); // no target

        sent!.ProfitTarget.Should().BeNull();
    }

    [Theory]
    [InlineData(OrderSide.Buy, 4_990)]  // a long taking profit BELOW entry is a loss, not a profit
    [InlineData(OrderSide.Buy, 5_000)]  // at entry is no profit either
    [InlineData(OrderSide.Sell, 5_010)] // a short taking profit ABOVE entry is a loss
    [InlineData(OrderSide.Sell, 5_000)]
    public async Task SendAsync_ShouldRefuse_WhenTheTakeProfitTargetIsNotOnTheWinningSideOfEntry(
        OrderSide side, int target)
    {
        // The mirror of safety-beyond-actual: a take-profit must sit where the position PROFITS -- above entry
        // for a long, below it for a short. A wrong-side target is a contradiction; refuse it before sizing,
        // never silently drop or flip it into a stop.
        GateReturns(GateDecision.Allow(4, "never reached -- refused before the gate"));

        ExecutionRequest request = Request(Account(TradingMode.Practice), side: side, target: new Price(target));

        ExecutionResult result = await _service.SendAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByInvalidTarget);
        result.Decision.Should().BeNull();
        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).MustNotHaveHappened();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public void Evaluate_ShouldRefuseAWrongSideTarget_ExactlyAsSendWould()
    {
        // Arm judges a ticket exactly as send does (gh#11 inc 2): a wrong-side target is refused at arm, not
        // discovered at take.
        GateReturns(GateDecision.Allow(4, "never reached"));

        ExecutionResult result = _service.Evaluate(
            Request(Account(TradingMode.Practice), target: new Price(4_990m))); // below entry for a long

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByInvalidTarget);
        result.Decision.Should().BeNull();
    }

    // --- Evaluate: the ladder minus transmission (gh#11 increment 2) ---

    [Fact]
    public void Evaluate_ShouldRunTheGateAndReturnItsDecision_WithoutTransmitting()
    {
        // Arming judges a ticket exactly as sending would -- same ladder, same code -- but the venue must
        // never see it: transmission is a separate, explicit act (ADR-0007).
        GateReturns(GateDecision.Block(RiskLayer.ManualCap, "over the manual cap"));

        ExecutionResult result = _service.Evaluate(Request(Account(TradingMode.Practice)));

        result.Outcome.Should().Be(ExecutionOutcome.Evaluated);
        result.Decision!.Outcome.Should().Be(GateOutcome.Blocked); // a blocking decision still stages -- it is
        result.Order.Should().BeNull();                            // information, not authorization
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public void Evaluate_ShouldRefusePreGate_ExactlyAsSendWould()
    {
        GateReturns(GateDecision.Allow(4, "irrelevant -- never reached"));

        ExecutionResult result = _service.Evaluate(Request(Account(TradingMode.Undeclared)));

        // Undeclared trades nowhere (gh#60): the pre-gate ladder fires before sizing, for arm and send alike.
        result.Outcome.Should().Be(ExecutionOutcome.RefusedByMode);
        result.Decision.Should().BeNull();
    }

    // --- The happy path ---

    [Fact]
    public async Task SendAsync_ShouldPlaceTheOrder_WhenTheModeIsAllowedAndTheGateApproves()
    {
        GateReturns(GateDecision.Allow(4, "within every layer"));
        VenueAccepts();

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Practice)), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.Placed);
        result.Order.Should().NotBeNull();
        result.Decision!.ApprovedQuantity.Should().Be(4);
    }

    [Fact]
    public async Task SendAsync_ShouldSendTheApprovedQuantity_NotTheRequestedOne()
    {
        // The gate resizing to 2 must be what reaches the broker. Sending the requested 4 would make the gate
        // advisory, which is the one thing it must never be.
        GateReturns(GateDecision.Resize(2, RiskLayer.DailyGovernor, "governor"));
        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest r, CancellationToken _) => sent = r)
            .ReturnsLazily((OrderRequest r, CancellationToken _) =>
                Task.FromResult(new PlacedOrder(r.Account, "555", DateTimeOffset.UnixEpoch)));

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Practice), quantity: 4), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.Placed);
        sent.Should().NotBeNull();
        sent!.Quantity.Should().Be(2);
    }

    // --- Nothing reaches the venue when it shouldn't ---

    [Fact]
    public async Task SendAsync_ShouldNotTouchTheVenue_WhenTheGateBlocks()
    {
        GateReturns(GateDecision.Block(RiskLayer.DrawdownFloor, "floor breached"));

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Practice)), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByRisk);
        result.Order.Should().BeNull();
        result.Decision!.BindingLayer.Should().Be(RiskLayer.DrawdownFloor);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldNotTouchTheVenue_WhenTheGateApprovesZero()
    {
        // "No trade" is a real outcome (R-5). A zero-quantity order must never be transmitted.
        GateReturns(new GateDecision(GateOutcome.Resized, 0, RiskLayer.PerTradeRisk, "no viable size"));

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Practice)), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByRisk);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- R-14: the mode guard ---

    [Fact]
    public async Task SendAsync_ShouldRefuseALiveAccountOutsideProduction()
    {
        GateReturns(GateDecision.Allow(4, "within every layer"));

        ExecutionResult result = await ServiceIn(DeploymentEnvironment.Staging).SendAsync(
            Request(Account(TradingMode.Live)), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByMode);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldCheckModeBeforeSizing_SoAForbiddenAccountIsNeverEvenPriced()
    {
        // Order matters: an account we may not trade should be refused before the risk model runs against it.
        GateReturns(GateDecision.Allow(4, "within every layer"));

        await _service.SendAsync(
            Request(Account(TradingMode.Live)), CancellationToken.None);

        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldAllowALiveAccountInProduction()
    {
        GateReturns(GateDecision.Allow(4, "within every layer"));
        VenueAccepts();

        ExecutionResult result = await ServiceIn(DeploymentEnvironment.Production).SendAsync(
            Request(Account(TradingMode.Live)), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.Placed);
    }

    // --- The order that is sent is the one that was gated ---

    [Fact]
    public async Task SendAsync_ShouldSendAgainstTheAccountItWasGatedFor()
    {
        GateReturns(GateDecision.Allow(4, "within every layer"));
        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest r, CancellationToken _) => sent = r)
            .ReturnsLazily((OrderRequest r, CancellationToken _) =>
                Task.FromResult(new PlacedOrder(r.Account, "555", DateTimeOffset.UnixEpoch)));

        VenueAccount account = Account(TradingMode.Practice);
        await _service.SendAsync(Request(account), CancellationToken.None);

        sent!.Account.Should().Be(account.Id);
        sent.Contract.Venue.Should().Be(Venue);
    }

    [Fact]
    public async Task SendAsync_ShouldAlwaysExplainItself()
    {
        GateReturns(GateDecision.Block(RiskLayer.SanityCap, "fat finger"));

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Practice)), CancellationToken.None);

        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    // --- The request must mean what it appears to mean ---

    [Fact]
    public async Task SendAsync_ShouldRefuse_WhenTheRiskSnapshotDescribesADifferentAccount()
    {
        // Equity, drawdown floor and daily limits are account-specific. Sizing against one account and sending
        // to another authorizes a quantity the target account never justified.
        GateReturns(GateDecision.Allow(4, "within every layer"));
        VenueAccount account = Account(TradingMode.Practice);

        ExecutionRequest crossed = Request(account) with
        {
            Risk = Context(VenueAccountId.Create(Venue, "9999")),
        };

        ExecutionResult result = await _service.SendAsync(crossed, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByMismatch);
        result.Decision.Should().BeNull();
        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).MustNotHaveHappened();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldRefuse_WhenTheContractWasResolvedForADifferentInstrument()
    {
        // The proposal's tick size and point value are ES's; the contract is NQ's. The gate would authorize
        // exposure the transmitted contract does not have.
        GateReturns(GateDecision.Allow(4, "within every layer"));

        ExecutionRequest crossed = Request(Account(TradingMode.Practice)) with
        {
            Contract = new ResolvedContract(
                VenueContractId.Create(Venue, "CON.F.US.ENQ.U26"), InstrumentId.Parse("NQ")),
        };

        ExecutionResult result = await _service.SendAsync(crossed, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByMismatch);
        result.Reason.Should().Contain("ES").And.Contain("NQ");
        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).MustNotHaveHappened();
    }

    // --- Order types ---

    [Theory]
    [InlineData(OrderType.Market, null, null)]
    [InlineData(OrderType.Limit, 5_000, null)]
    [InlineData(OrderType.Stop, null, 5_000)]
    [InlineData(OrderType.StopLimit, 5_000, 5_000)]
    public async Task SendAsync_ShouldTransmitTheTypeWithExactlyThePricesThatTypeNeeds(
        OrderType type,
        int? expectedLimit,
        int? expectedStop)
    {
        // A price on the wrong type -- or a missing one on the right type -- is a malformed ticket the venue
        // either rejects or, worse, fills somewhere unintended.
        GateReturns(GateDecision.Allow(4, "within every layer"));
        OrderRequest? sent = null;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Invokes((OrderRequest r, CancellationToken _) => sent = r)
            .ReturnsLazily((OrderRequest r, CancellationToken _) =>
                Task.FromResult(new PlacedOrder(r.Account, "555", DateTimeOffset.UnixEpoch)));

        ExecutionRequest request = Request(Account(TradingMode.Practice)) with { Type = type };

        await _service.SendAsync(request, CancellationToken.None);

        sent!.Type.Should().Be(type);
        sent.LimitPrice.Should().Be(expectedLimit is null ? null : new Price(expectedLimit.Value));
        sent.StopPrice.Should().Be(expectedStop is null ? null : new Price(expectedStop.Value));
    }

    [Theory]
    [InlineData(DeploymentEnvironment.Development)]
    [InlineData(DeploymentEnvironment.Staging)]
    [InlineData(DeploymentEnvironment.Production)]
    public async Task SendAsync_ShouldRefuseAnUndeclaredAccount_InEveryEnvironmentIncludingProduction(
        DeploymentEnvironment environment)
    {
        // The seam between the send path and declared trading modes (gh#11 + gh#60): neither shipped with the
        // other, so nothing exercised them together. An account whose stage the operator has not classified is
        // exactly the one that must not reach a broker -- production included, since Live is permitted there and
        // Undeclared must not inherit that permission.
        GateReturns(GateDecision.Allow(4, "within every layer"));
        VenueAccount unclassified = Account(TradingMode.Practice) with
        {
            Name = "EXPRESS-50K-9001",
            Mode = TradingMode.Undeclared,
        };

        ExecutionResult result = await ServiceIn(environment).SendAsync(
            Request(unclassified), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByMode);
        result.Decision.Should().BeNull();
        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).MustNotHaveHappened();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldRefuse_WhenTheVenueReportsTheAccountAsNotTradable()
    {
        // A passed, failed or closed prop account reports CanTrade false. Leaving the broker to reject it would
        // mean the ticket left the enforcing path before anything refused it.
        GateReturns(GateDecision.Allow(4, "within every layer"));
        VenueAccount closed = Account(TradingMode.Practice) with { CanTrade = false };

        ExecutionResult result = await _service.SendAsync(Request(closed), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByAccountState);
        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).MustNotHaveHappened();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldRefuse_WhenTheAccountBelongsToAnotherVenue()
    {
        // Account handles collide across venues -- a bare "9001" exists at every broker. Relying on the adapter
        // to notice gives an exception from its mapping rather than the refusal this path promises.
        GateReturns(GateDecision.Allow(4, "within every layer"));
        VenueAccountId foreign = VenueAccountId.Create(VenueId.Parse("tradovate"), "9001");
        VenueAccount account = Account(TradingMode.Practice) with { Id = foreign };

        ExecutionRequest request = Request(account) with { Risk = Context(foreign) };

        ExecutionResult result = await _service.SendAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByMismatch);
        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).MustNotHaveHappened();
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldRefuse_WhenTheContractBelongsToAnotherVenue()
    {
        // Especially dangerous because a colliding contract key would name a real, different instrument.
        GateReturns(GateDecision.Allow(4, "within every layer"));

        ExecutionRequest request = Request(Account(TradingMode.Practice)) with
        {
            Contract = new ResolvedContract(
                VenueContractId.Create(VenueId.Parse("tradovate"), "CON.F.US.EP.U26"), InstrumentId.Parse("ES")),
        };

        ExecutionResult result = await _service.SendAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByMismatch);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldRefuseAnUnrecognizedOrderType_RatherThanTransmitItWithNoPrices()
    {
        // A numeric deserialization or a cast produces a value no switch here handles. Falling through would
        // send a ticket with every price left null -- the venue either rejects it or fills it somewhere
        // unintended. Whitelist, not blacklist.
        GateReturns(GateDecision.Allow(4, "within every layer"));

        ExecutionRequest request = Request(Account(TradingMode.Practice)) with { Type = (OrderType)99 };

        ExecutionResult result = await _service.SendAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByUnsupportedType);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldNotTransmit_WhenTheGateReturnsAnUnrecognizedOutcome()
    {
        // "Anything but Blocked" would make a later-added outcome authorization by default. GateDecision has a
        // public constructor and IRiskGate is replaceable, so this is reachable without editing the enum.
        GateReturns(new GateDecision((GateOutcome)99, ApprovedQuantity: 4, RiskLayer.SanityCap, "unknown"));

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Practice)), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByRisk);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(GateOutcome.Allowed)]
    [InlineData(GateOutcome.Resized)]
    public async Task SendAsync_ShouldTransmit_ForEveryOutcomeThatIsAuthorization(GateOutcome outcome)
    {
        GateReturns(new GateDecision(outcome, ApprovedQuantity: 2, RiskLayer.PerTradeRisk, "sized"));
        VenueAccepts();

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Practice)), CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.Placed);
    }

    [Fact]
    public async Task SendAsync_ShouldCarryNoGateDecision_ForEveryPreGateRefusal()
    {
        // Consumers must read Outcome, not infer the reason from a null Decision -- four different guards
        // return before the gate runs.
        GateReturns(GateDecision.Allow(4, "within every layer"));

        ExecutionRequest[] refused =
        [
            Request(Account(TradingMode.Practice) with { CanTrade = false }),
            Request(Account(TradingMode.Practice)) with { Risk = Context(VenueAccountId.Create(Venue, "9999")) },
            Request(Account(TradingMode.Practice)) with { Type = OrderType.TrailingStop },
            Request(Account(TradingMode.Practice)) with { Type = (OrderType)99 },
            Request(Account(TradingMode.Practice), target: new Price(4_990m)), // wrong-side take-profit
        ];

        foreach (ExecutionRequest request in refused)
        {
            ExecutionResult result = await _service.SendAsync(request, CancellationToken.None);

            result.Decision.Should().BeNull();
            result.Outcome.Should().NotBe(ExecutionOutcome.Placed);
            result.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task SendAsync_ShouldRefuseATrailingStop_BecauseTheTicketCarriesNoTrailDistance()
    {
        // Not a venue-capability question: the neutral ticket has nowhere to put a trail distance, so no venue
        // could receive it correctly. Refusing here makes the answer the same whichever adapter is wired in,
        // rather than depending on ProjectX happening to check its capability flags.
        GateReturns(GateDecision.Allow(4, "within every layer"));

        ExecutionRequest trailing = Request(Account(TradingMode.Practice)) with
        {
            Type = OrderType.TrailingStop,
        };

        ExecutionResult result = await _service.SendAsync(trailing, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByUnsupportedType);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }
}
