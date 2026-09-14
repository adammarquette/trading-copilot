using FakeItEasy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Execution;

/// <summary>
/// The in-place modify path (gh#259 reprice, gh#292 resize): re-gated through the same enforcing service as a
/// send. A <b>pure reprice</b> (<c>resize: false</c>) must never reach the venue while killed, bypass the gate,
/// or silently change size — the gate must approve the <b>unchanged</b> size outright, and a <see cref="GateOutcome.Resized"/>
/// refuses (silently downsizing would desync the attached safety bracket). A <b>resize</b> (<c>resize: true</c>) is
/// a deliberate size change, so it honours the gate exactly as a send does — <see cref="GateOutcome.Allowed"/> or
/// <see cref="GateOutcome.Resized"/> — and transmits the <b>gate-approved</b> quantity (the safety bracket carries
/// no size and the gateway sizes it to the fill, gh#292). The kill switch refuses either (outbound, unlike a cancel).
/// </summary>
public class OrderExecutionServiceModifyTests
{
    private const string WorkingOrderKey = "PX-55001";

    private static VenueId Venue => VenueId.Parse("projectx");

    private readonly IRiskGate _gate = A.Fake<IRiskGate>();
    private readonly IOrderExecutor _venue = A.Fake<IOrderExecutor>();
    private readonly IKillSwitch _killSwitch = A.Fake<IKillSwitch>(); // defaults IsEngaged == false (not killed)
    private readonly OrderExecutionService _service;

    public OrderExecutionServiceModifyTests()
    {
        A.CallTo(() => _venue.Id).Returns(Venue);
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders));
        _service = new OrderExecutionService(_gate, _venue, DeploymentEnvironment.Development, _killSwitch);
    }

    private static VenueAccountId AccountId => VenueAccountId.Create(Venue, "9001");

    private static VenueAccount Account(TradingMode mode = TradingMode.Practice)
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
            ReferencePrice: new Price(5_000m),
            Target: null);
    }

    private static RiskContext Context()
    {
        return new RiskContext(
            AccountId,
            new AccountRiskState(55_000m, 0m, 0m),
            TrailingDrawdown.Start(TrailingMode.EndOfDay, 5_000m, 55_000m),
            new AccountRiskRules(3_000m, null, FloorSource.FirmImposed),
            new RiskProfile(0.20m, 1.5m, 1_500m, 2_000m, null, false, SizingBasis.ActualStop),
            ManualCaps.Create(10),
            new SanityCaps(10, 10_000_000m, 40));
    }

    private static ResolvedContract EsContract =>
        new(VenueContractId.Create(Venue, "CON.F.US.EP.U26"), InstrumentId.Parse("ES"));

    private static ExecutionRequest Request(int quantity = 4, OrderType type = OrderType.Limit)
    {
        return new ExecutionRequest(Proposal(quantity), EsContract, Account(), Context(), type);
    }

    private void GateReturns(GateDecision decision)
    {
        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).Returns(decision);
    }

    private void VenueAcceptsModify()
    {
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Returns(Task.CompletedTask);
    }

    private static void MustNotHaveModified(IOrderExecutor venue)
    {
        A.CallTo(() => venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- The happy path: re-gate, then transmit the reprice at the unchanged size ---

    [Fact]
    public async Task ModifyAsync_ShouldTransmitTheReprice_WhenTheGateAllowsAtTheRequestedSize()
    {
        GateReturns(GateDecision.Allow(4, "within every layer at the new price"));
        VenueAcceptsModify();

        ExecutionResult result = await _service.ModifyAsync(Request(quantity: 4), WorkingOrderKey, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.Modified);
        result.Order.Should().BeNull(); // no new venue order -- the order keeps its handle
        result.Decision!.ApprovedQuantity.Should().Be(4); // the gate approved the unchanged size...
        A.CallTo(() => _venue.ModifyOrderAsync(
            AccountId, WorkingOrderKey, new Price(5_000m), A<Price?>._, null, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly(); // ...but the venue is sent null size = "leave unchanged" (gh#270), preserving queue position
    }

    [Fact]
    public async Task ModifyAsync_ShouldSendTheEntryAsTheLimitPrice_ForALimitOrder()
    {
        GateReturns(GateDecision.Allow(4, "ok"));
        Price? sentLimit = null;
        Price? sentStop = null;
        A.CallTo(() => _venue.ModifyOrderAsync(
            A<VenueAccountId>._, A<string>._, A<Price?>._, A<Price?>._, A<int?>._, A<CancellationToken>._))
            .Invokes((VenueAccountId _, string _, Price? limit, Price? stop, int? _, CancellationToken _) =>
            {
                sentLimit = limit;
                sentStop = stop;
            })
            .Returns(Task.CompletedTask);

        await _service.ModifyAsync(Request(type: OrderType.Limit), WorkingOrderKey, CancellationToken.None);

        sentLimit.Should().Be(new Price(5_000m)); // a Limit order reprices its limit price
        sentStop.Should().BeNull();               // and carries no stop/trigger
    }

    // --- The anti-desync anchor: a resize REFUSES, it never silently downsizes ---

    [Fact]
    public async Task ModifyAsync_ShouldRefuseAndNotTouchTheVenue_WhenTheGateResizesBelowTheRequestedSize()
    {
        // The core gh#259 safety property: at a wider stop the gate may want a smaller size. A send honours that
        // by downsizing; a modify must NOT -- the attached safety-stop bracket's quantity is not addressable
        // through a modify, so a silent downsize would strand protection. Refuse the reprice instead.
        GateReturns(GateDecision.Resize(2, RiskLayer.PerTradeRisk, "the wider stop only fits 2"));

        ExecutionResult result = await _service.ModifyAsync(Request(quantity: 4), WorkingOrderKey, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByRisk);
        result.Decision!.Outcome.Should().Be(GateOutcome.Resized);
        MustNotHaveModified(_venue);
    }

    [Fact]
    public async Task ModifyAsync_ShouldRefuse_WhenTheGateAllowsButAtADifferentQuantityThanRequested()
    {
        // Defensive: IRiskGate is replaceable and GateDecision has a public ctor, so an Allowed decision whose
        // quantity does not match the request must still refuse -- transmitting would change size off-contract.
        GateReturns(new GateDecision(GateOutcome.Allowed, ApprovedQuantity: 3, BindingLayer: null, "mismatched qty"));

        ExecutionResult result = await _service.ModifyAsync(Request(quantity: 4), WorkingOrderKey, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByRisk);
        MustNotHaveModified(_venue);
    }

    // --- The kill switch refuses a modify (unlike a cancel) ---

    [Fact]
    public async Task ModifyAsync_ShouldRefuse_WhenTheKillSwitchIsEngaged()
    {
        // A reprice is outbound and risk-additive, so the panic button disables it -- refused above the flow,
        // before the gate even runs. (A cancel, being risk-reducing, deliberately bypasses this; a modify must not.)
        A.CallTo(() => _killSwitch.IsEngaged).Returns(true);
        GateReturns(GateDecision.Allow(4, "the gate would allow it -- never reached"));

        ExecutionResult result = await _service.ModifyAsync(Request(), WorkingOrderKey, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByKillSwitch);
        result.Decision.Should().BeNull();
        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).MustNotHaveHappened();
        MustNotHaveModified(_venue);
    }

    // --- The gate still binds ---

    [Fact]
    public async Task ModifyAsync_ShouldRefuseAndNotTouchTheVenue_WhenTheGateBlocks()
    {
        GateReturns(GateDecision.Block(RiskLayer.SanityCap, "the new entry trips the fat-finger band"));

        ExecutionResult result = await _service.ModifyAsync(Request(), WorkingOrderKey, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByRisk);
        result.Decision!.BindingLayer.Should().Be(RiskLayer.SanityCap);
        MustNotHaveModified(_venue);
    }

    [Fact]
    public async Task ModifyAsync_ShouldRefusePreGate_ExactlyAsSendWould()
    {
        // The same ladder runs before the gate: an undeclared account is refused for a modify just as for a send.
        GateReturns(GateDecision.Allow(4, "never reached"));
        ExecutionRequest undeclared = new(
            Proposal(), EsContract, Account() with { Mode = TradingMode.Undeclared }, Context(), OrderType.Limit);

        ExecutionResult result = await _service.ModifyAsync(undeclared, WorkingOrderKey, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByMode);
        result.Decision.Should().BeNull();
        MustNotHaveModified(_venue);
    }

    [Fact]
    public async Task ModifyAsync_ShouldRefuse_WhenTheVenueCannotHoldANativeProtectiveStop()
    {
        // Fail-closed, same as a send: if a fill could not rest on an exchange-held stop, the reprice is not sent.
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.Quotes)); // no brackets
        GateReturns(GateDecision.Allow(4, "the gate would allow it"));

        ExecutionResult result = await _service.ModifyAsync(Request(), WorkingOrderKey, CancellationToken.None);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByUnprotectableStop);
        MustNotHaveModified(_venue);
    }

    // --- RESIZE (resize: true) honours the gate and transmits the approved size (gh#292) ---

    [Fact]
    public async Task ModifyAsync_ShouldTransmitTheNewSize_WhenResizeAndTheGateAllowsAtTheNewSize()
    {
        // A deliberate resize: the gate allows the new (larger) size, so it is transmitted. Unlike a reprice, the
        // size IS sent -- you cannot change the quantity without sending it; the on-fill bracket sizes to the fill.
        GateReturns(GateDecision.Allow(6, "6 within every layer"));
        VenueAcceptsModify();

        ExecutionResult result = await _service.ModifyAsync(
            Request(quantity: 6), WorkingOrderKey, CancellationToken.None, resize: true);

        result.Outcome.Should().Be(ExecutionOutcome.Modified);
        result.Decision!.ApprovedQuantity.Should().Be(6);
        A.CallTo(() => _venue.ModifyOrderAsync(
            AccountId, WorkingOrderKey, new Price(5_000m), A<Price?>._, 6, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly(); // the NEW size reaches the venue (not null, unlike a reprice)
    }

    [Fact]
    public async Task ModifyAsync_ShouldTransmitTheGateDownsizedQuantity_WhenResizeAndTheGateResizes()
    {
        // The core resize property (the inverse of the reprice anchor): a resize HONOURS a gate downsize -- it
        // transmits decision.ApprovedQuantity (never the asked size), so the gate is never exceeded, never silent.
        GateReturns(GateDecision.Resize(3, RiskLayer.PerTradeRisk, "only 3 fit at this stop"));
        VenueAcceptsModify();

        ExecutionResult result = await _service.ModifyAsync(
            Request(quantity: 6), WorkingOrderKey, CancellationToken.None, resize: true);

        result.Outcome.Should().Be(ExecutionOutcome.Modified);
        result.Decision!.Outcome.Should().Be(GateOutcome.Resized);
        result.Decision!.ApprovedQuantity.Should().Be(3);
        A.CallTo(() => _venue.ModifyOrderAsync(
            AccountId, WorkingOrderKey, A<Price?>._, A<Price?>._, 3, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly(); // the gate-approved 3, not the asked 6
    }

    [Fact]
    public async Task ModifyAsync_ShouldRefuseAndNotTouchTheVenue_WhenResizeAndTheGateBlocks()
    {
        GateReturns(GateDecision.Block(RiskLayer.DrawdownFloor, "no room for even one at this size"));

        ExecutionResult result = await _service.ModifyAsync(
            Request(quantity: 6), WorkingOrderKey, CancellationToken.None, resize: true);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByRisk);
        MustNotHaveModified(_venue);
    }

    [Fact]
    public async Task ModifyAsync_ShouldRefuse_WhenResizeAndTheKillSwitchIsEngaged()
    {
        // A resize is outbound and (on an upsize) risk-additive, so the kill switch refuses it, exactly like a reprice.
        A.CallTo(() => _killSwitch.IsEngaged).Returns(true);
        GateReturns(GateDecision.Allow(6, "never reached"));

        ExecutionResult result = await _service.ModifyAsync(
            Request(quantity: 6), WorkingOrderKey, CancellationToken.None, resize: true);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByKillSwitch);
        A.CallTo(() => _gate.Evaluate(A<OrderProposal>._, A<RiskContext>._)).MustNotHaveHappened();
        MustNotHaveModified(_venue);
    }

    [Fact]
    public async Task ModifyAsync_ShouldRefuse_WhenResizeAndTheVenueCannotHoldANativeProtectiveStop()
    {
        // Fail-closed: a resized fill still needs its exchange-held stop, so a venue that cannot hold one is refused.
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.Quotes)); // no brackets
        GateReturns(GateDecision.Allow(6, "the gate would allow it"));

        ExecutionResult result = await _service.ModifyAsync(
            Request(quantity: 6), WorkingOrderKey, CancellationToken.None, resize: true);

        result.Outcome.Should().Be(ExecutionOutcome.RefusedByUnprotectableStop);
        MustNotHaveModified(_venue);
    }
}
