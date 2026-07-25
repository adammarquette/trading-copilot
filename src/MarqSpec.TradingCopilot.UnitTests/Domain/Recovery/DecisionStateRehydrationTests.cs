using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Recovery;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Recovery;

/// <summary>
/// The pure decision-state rehydration analysis (gh#221, ADR-0013): it counts what a restart brings back and finds
/// every <b>impossible cross-entity combination</b> a crash mid-write could leave — without resuming anything. The
/// safety property is that a coherent surface is reported clean, and each contradictory combination is flagged so
/// the composition layer can fail safe and loud.
/// </summary>
public class DecisionStateRehydrationTests
{
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _otherOwner = Guid.NewGuid();

    private static RehydratedOrder Order(Guid id, Guid owner, OrderStatus status, bool hasVenueKey = false) =>
        new(id, owner, status, hasVenueKey);

    private static RehydratedConditional Conditional(Guid id, Guid owner, ConditionalStatus status, Guid? firedOrderId = null) =>
        new(id, owner, status, firedOrderId);

    private static RehydratedStopPlan StopPlan(Guid id, Guid owner, Guid orderId, StopStaging staging) =>
        new(id, owner, orderId, staging);

    private static DecisionSurfaceReport Analyze(
        IReadOnlyList<RehydratedOrder>? orders = null,
        IReadOnlyList<RehydratedConditional>? conditionals = null,
        IReadOnlyList<RehydratedStopPlan>? stopPlans = null,
        int activeSuggestions = 0) =>
        DecisionStateRehydration.Analyze(orders ?? [], conditionals ?? [], stopPlans ?? [], activeSuggestions);

    [Fact]
    public void Analyze_ShouldReportConsistent_WhenTheSurfaceIsInertAndCoherent()
    {
        Guid working = Guid.NewGuid();
        Guid staged = Guid.NewGuid();

        DecisionSurfaceReport report = Analyze(
            orders:
            [
                Order(working, _owner, OrderStatus.Working, hasVenueKey: true),
                Order(staged, _owner, OrderStatus.Staged), // armed server-side, no venue key -- inert
            ],
            conditionals:
            [
                Conditional(Guid.NewGuid(), _owner, ConditionalStatus.Pending),
                Conditional(Guid.NewGuid(), _owner, ConditionalStatus.Fired, firedOrderId: working),
            ],
            stopPlans:
            [
                StopPlan(Guid.NewGuid(), _owner, staged, StopStaging.Hidden), // hidden for a staged order -- fine
                StopPlan(Guid.NewGuid(), _owner, working, StopStaging.Native), // native for a live order -- fine
            ],
            activeSuggestions: 2);

        report.IsConsistent.Should().BeTrue();
        report.Inconsistencies.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ShouldCountInertState_ForTheSummary()
    {
        Guid working = Guid.NewGuid();
        Guid staged = Guid.NewGuid();

        DecisionSurfaceReport report = Analyze(
            orders:
            [
                Order(working, _owner, OrderStatus.Working, hasVenueKey: true),
                Order(staged, _owner, OrderStatus.Staged),
            ],
            conditionals:
            [
                Conditional(Guid.NewGuid(), _owner, ConditionalStatus.Pending),
                Conditional(Guid.NewGuid(), _owner, ConditionalStatus.Pending),
                Conditional(Guid.NewGuid(), _owner, ConditionalStatus.Cancelled),
            ],
            stopPlans:
            [
                StopPlan(Guid.NewGuid(), _owner, staged, StopStaging.Hidden),
                StopPlan(Guid.NewGuid(), _owner, working, StopStaging.Native),
                StopPlan(Guid.NewGuid(), _owner, working, StopStaging.Orphaned),
            ],
            activeSuggestions: 4);

        report.Orders.Should().Be(2);
        report.StagedOrders.Should().Be(1);
        report.Conditionals.Should().Be(3);
        report.PendingConditionals.Should().Be(2);
        report.HiddenStops.Should().Be(1);
        report.NativeStops.Should().Be(1);
        report.OrphanedStops.Should().Be(1);
        report.ActiveSuggestions.Should().Be(4);
        report.IsConsistent.Should().BeTrue();
    }

    [Fact]
    public void Analyze_ShouldFlagStagedOrderAtVenue_WhenAStagedOrderCarriesAVenueKey()
    {
        Guid order = Guid.NewGuid();

        DecisionSurfaceReport report = Analyze(
            orders: [Order(order, _owner, OrderStatus.Staged, hasVenueKey: true)]);

        DecisionInconsistency issue = report.Inconsistencies.Should().ContainSingle().Which;
        issue.Kind.Should().Be(DecisionInconsistencyKind.StagedOrderAtVenue);
        issue.Owner.Should().Be(_owner);
        issue.EntityId.Should().Be(order);
    }

    [Fact]
    public void Analyze_ShouldFlagConditionalFiredWithoutOrder_WhenAFiredConditionalHasNoLink()
    {
        Guid conditional = Guid.NewGuid();

        DecisionSurfaceReport report = Analyze(
            conditionals: [Conditional(conditional, _owner, ConditionalStatus.Fired, firedOrderId: null)]);

        report.Inconsistencies.Should().ContainSingle()
            .Which.Kind.Should().Be(DecisionInconsistencyKind.ConditionalFiredWithoutOrder);
    }

    [Fact]
    public void Analyze_ShouldFlagConditionalFiredOrderMissing_WhenTheLinkedOrderIsAbsent()
    {
        DecisionSurfaceReport report = Analyze(
            conditionals: [Conditional(Guid.NewGuid(), _owner, ConditionalStatus.Fired, firedOrderId: Guid.NewGuid())]);

        report.Inconsistencies.Should().ContainSingle()
            .Which.Kind.Should().Be(DecisionInconsistencyKind.ConditionalFiredOrderMissing);
    }

    [Theory]
    [InlineData(ConditionalStatus.Pending)]
    [InlineData(ConditionalStatus.Cancelled)]
    [InlineData(ConditionalStatus.Expired)]
    public void Analyze_ShouldFlagConditionalUnfiredWithOrder_WhenANotFiredConditionalLinksAnOrder(ConditionalStatus status)
    {
        Guid order = Guid.NewGuid();

        DecisionSurfaceReport report = Analyze(
            orders: [Order(order, _owner, OrderStatus.Working, hasVenueKey: true)],
            conditionals: [Conditional(Guid.NewGuid(), _owner, status, firedOrderId: order)]);

        report.Inconsistencies.Should().ContainSingle()
            .Which.Kind.Should().Be(DecisionInconsistencyKind.ConditionalUnfiredWithOrder);
    }

    [Fact]
    public void Analyze_ShouldFlagStopPlanWithoutOrder_WhenTheParentOrderIsAbsent()
    {
        Guid plan = Guid.NewGuid();

        DecisionSurfaceReport report = Analyze(
            stopPlans: [StopPlan(plan, _owner, Guid.NewGuid(), StopStaging.Hidden)]);

        DecisionInconsistency issue = report.Inconsistencies.Should().ContainSingle().Which;
        issue.Kind.Should().Be(DecisionInconsistencyKind.StopPlanWithoutOrder);
        issue.EntityId.Should().Be(plan);
    }

    [Theory]
    [InlineData(OrderStatus.Staged)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Rejected)]
    public void Analyze_ShouldFlagNativeStopWithoutLiveOrder_WhenTheParentOrderIsNotLive(OrderStatus status)
    {
        Guid order = Guid.NewGuid();

        DecisionSurfaceReport report = Analyze(
            orders: [Order(order, _owner, status)],
            stopPlans: [StopPlan(Guid.NewGuid(), _owner, order, StopStaging.Native)]);

        report.Inconsistencies.Should().ContainSingle()
            .Which.Kind.Should().Be(DecisionInconsistencyKind.NativeStopWithoutLiveOrder);
    }

    [Theory]
    [InlineData(OrderStatus.Working)]
    [InlineData(OrderStatus.PartiallyFilled)]
    [InlineData(OrderStatus.Filled)]
    public void Analyze_ShouldNotFlagNativeStop_WhenTheParentOrderIsLive(OrderStatus status)
    {
        Guid order = Guid.NewGuid();

        DecisionSurfaceReport report = Analyze(
            orders: [Order(order, _owner, status, hasVenueKey: true)],
            stopPlans: [StopPlan(Guid.NewGuid(), _owner, order, StopStaging.Native)]);

        report.IsConsistent.Should().BeTrue(); // a native stop is legitimate for a live position
    }

    [Fact]
    public void Analyze_ShouldFlagStopPlanOwnerMismatch_WhenTheStopPlanOwnerDiffersFromItsOrder()
    {
        Guid order = Guid.NewGuid();

        DecisionSurfaceReport report = Analyze(
            orders: [Order(order, _owner, OrderStatus.Working, hasVenueKey: true)],
            stopPlans: [StopPlan(Guid.NewGuid(), _otherOwner, order, StopStaging.Hidden)]);

        report.Inconsistencies.Should().ContainSingle()
            .Which.Kind.Should().Be(DecisionInconsistencyKind.StopPlanOwnerMismatch);
    }

    [Fact]
    public void Analyze_ShouldFindEachInconsistency_Independently_AcrossOwners()
    {
        Guid stagedAtVenue = Guid.NewGuid();

        DecisionSurfaceReport report = Analyze(
            orders: [Order(stagedAtVenue, _owner, OrderStatus.Staged, hasVenueKey: true)],
            conditionals: [Conditional(Guid.NewGuid(), _otherOwner, ConditionalStatus.Fired, firedOrderId: null)],
            stopPlans: [StopPlan(Guid.NewGuid(), _otherOwner, Guid.NewGuid(), StopStaging.Hidden)]);

        report.IsConsistent.Should().BeFalse();
        report.Inconsistencies.Select(issue => issue.Kind).Should().BeEquivalentTo(
        [
            DecisionInconsistencyKind.StagedOrderAtVenue,
            DecisionInconsistencyKind.ConditionalFiredWithoutOrder,
            DecisionInconsistencyKind.StopPlanWithoutOrder,
        ]);
    }
}
