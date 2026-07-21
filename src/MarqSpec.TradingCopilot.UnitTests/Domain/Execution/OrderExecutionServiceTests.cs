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
        _service = new OrderExecutionService(_gate, _venue);
    }

    private static VenueAccount Account(TradingMode mode)
    {
        return new VenueAccount(
            VenueAccountId.Create(Venue, "9001"),
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

    private static RiskContext Context()
    {
        return new RiskContext(
            new AccountRiskState(55_000m, 0m, 0m),
            TrailingDrawdown.Start(TrailingMode.EndOfDay, 5_000m, 55_000m),
            new AccountRiskRules(3_000m, null, FloorSource.FirmImposed),
            new RiskProfile(0.20m, 1.5m, 1_500m, 2_000m, null, false, SizingBasis.ActualStop),
            ManualCaps.Create(10),
            new SanityCaps(10, 10_000_000m, 40));
    }

    private static ExecutionRequest Request(VenueAccount account, int quantity = 4)
    {
        return new ExecutionRequest(
            Proposal(quantity),
            VenueContractId.Create(Venue, "CON.F.US.EP.U26"),
            account,
            Context());
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
            Request(Account(TradingMode.Practice)), DeploymentEnvironment.Development, CancellationToken.None);

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
            Request(Account(TradingMode.Practice), quantity: 4), DeploymentEnvironment.Development, CancellationToken.None);

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
            Request(Account(TradingMode.Practice)), DeploymentEnvironment.Development, CancellationToken.None);

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
            Request(Account(TradingMode.Practice)), DeploymentEnvironment.Development, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByRisk);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- R-14: the mode guard ---

    [Fact]
    public async Task SendAsync_ShouldRefuseALiveAccountOutsideProduction()
    {
        GateReturns(GateDecision.Allow(4, "within every layer"));

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Live)), DeploymentEnvironment.Staging, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByMode);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldCheckModeBeforeSizing_SoAForbiddenAccountIsNeverEvenPriced()
    {
        // Order matters: an account we may not trade should be refused before the risk model runs against it.
        GateReturns(GateDecision.Allow(4, "within every layer"));

        await _service.SendAsync(
            Request(Account(TradingMode.Live)), DeploymentEnvironment.Development, CancellationToken.None);

        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldAllowALiveAccountInProduction()
    {
        GateReturns(GateDecision.Allow(4, "within every layer"));
        VenueAccepts();

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Live)), DeploymentEnvironment.Production, CancellationToken.None);

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
        await _service.SendAsync(Request(account), DeploymentEnvironment.Development, CancellationToken.None);

        sent!.Account.Should().Be(account.Id);
        sent.Contract.Venue.Should().Be(Venue);
    }

    [Fact]
    public async Task SendAsync_ShouldAlwaysExplainItself()
    {
        GateReturns(GateDecision.Block(RiskLayer.SanityCap, "fat finger"));

        ExecutionResult result = await _service.SendAsync(
            Request(Account(TradingMode.Practice)), DeploymentEnvironment.Development, CancellationToken.None);

        result.Reason.Should().NotBeNullOrWhiteSpace();
    }
}
