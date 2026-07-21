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

    private static OrderProposal Proposal(int quantity = 4)
    {
        return new OrderProposal(
            Es,
            OrderSide.Buy,
            quantity,
            Entry: new Price(5_000m),
            Stop: new Price(4_995m),
            SafetyStop: new Price(4_990m),
            ReferencePrice: new Price(5_000m));
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

    private static ExecutionRequest Request(VenueAccount account, int quantity = 4)
    {
        return new ExecutionRequest(
            Proposal(quantity),
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
