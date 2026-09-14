using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using OrderEntity = MarqSpec.TradingCopilot.Data.Entities.Order;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Orders;

/// <summary>
/// Regression guards for gh#407: the gate <b>computed</b> a <see cref="GateAdvisory"/> and attached it to the
/// decision, and then nothing carried it across the API boundary — <c>SendOrderResponse</c> had no advisory member
/// and <c>GateDecisionRecord</c> persisted none.
/// </summary>
/// <remarks>
/// Found by QA gh#394. It mattered because <see cref="ConsistencyEnforcement.Advisory"/> is the <b>migration
/// default</b>: every account that predates the column sits on the one posture that produced no observable output
/// at all — it does not refuse (by design) and it did not warn (the defect). The consistency target was
/// configurable, persisted, validated, returned by the risk API, evaluated on every order, and invisible.
/// </remarks>
public class GateAdvisorySurfacingTests
{
    [Fact]
    public void From_ShouldCarryTheAdvisories_WhenTheDecisionRaisedThem()
    {
        // The staged/arm path shows the operator the same decision the send path would make, so it must show the
        // same warnings -- otherwise arming is the one place a breach is silently invisible.
        GateDecision decision = Allowed().With(Breaching());
        OrderEntity order = new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Instrument = "CON.F.US.MES.M26",
            Symbol = "MES",
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Market,
            EntryPrice = 5_000m,
            SafetyStopPrice = 4_980m,
            ReferencePrice = 5_000m,
            Mode = TradingMode.Practice,
            Status = OrderStatus.Staged,
            PlacedAt = DateTimeOffset.UnixEpoch,
        };

        StagedOrderResponse response = StagedOrderResponse.From(order, decision);

        response.Advisories.Should().ContainSingle()
            .Which.Layer.Should().Be(RiskLayer.ConsistencyTarget, "the warning the gate raised must reach the operator");
    }

    [Fact]
    public void From_ShouldCarryNoAdvisories_WhenTheDecisionRaisedNone()
    {
        // Absence must be an empty list, never null: a client that has to null-check a warnings collection will
        // eventually forget to, and the failure mode is a silently unshown warning -- this defect again.
        OrderEntity order = new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Instrument = "CON.F.US.MES.M26",
            Symbol = "MES",
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Market,
            EntryPrice = 5_000m,
            SafetyStopPrice = 4_980m,
            ReferencePrice = 5_000m,
            Mode = TradingMode.Practice,
            Status = OrderStatus.Staged,
            PlacedAt = DateTimeOffset.UnixEpoch,
        };

        StagedOrderResponse response = StagedOrderResponse.From(order, Allowed());

        response.Advisories.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Serialize_ShouldRoundTripAnAdvisory_SoTheJournalCanAnswerWhetherTheOperatorWasWarned()
    {
        // The audit half of gh#407. Without this the journal records the decision but not the warning that
        // accompanied it, so "was the operator told before the payout was disqualified?" is unanswerable.
        IReadOnlyList<GateAdvisory> advisories = [Breaching()];

        string? json = GateAdvisoryJson.Serialize(advisories);
        IReadOnlyList<GateAdvisory> restored = GateAdvisoryJson.Deserialize(json);

        json.Should().NotBeNull();
        restored.Should().ContainSingle();
        restored[0].Layer.Should().Be(RiskLayer.ConsistencyTarget);
        restored[0].ConsumedFraction.Should().Be(0.9m);
        restored[0].Threshold.Should().Be(0.4m);
        restored[0].IsBreached.Should().BeTrue("the derived flag must survive the round trip, not just the numbers");
    }

    [Fact]
    public void Serialize_ShouldReturnNull_WhenThereAreNoAdvisories()
    {
        // A row per decision is written on every sized attempt; storing "[]" on the overwhelming majority that
        // raise nothing is pure noise. Null means "no warning", which is also what a pre-gh#407 row means.
        GateAdvisoryJson.Serialize([]).Should().BeNull();
        GateAdvisoryJson.Deserialize(null).Should().BeEmpty("a legacy row with no column value reads as no warnings");
    }

    private static GateDecision Allowed() =>
        new(GateOutcome.Allowed, ApprovedQuantity: 1, BindingLayer: null, Reason: "Allowed.");

    private static GateAdvisory Breaching() =>
        new(RiskLayer.ConsistencyTarget, ConsumedFraction: 0.9m, Threshold: 0.4m, Reason: "Over the target.");
}
