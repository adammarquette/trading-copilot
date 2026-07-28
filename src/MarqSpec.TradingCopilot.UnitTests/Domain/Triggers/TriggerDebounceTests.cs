using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Triggers;

/// <summary>
/// The debounce state machine (gh#385) — the full transition matrix, plus the cold-start scenario a two-state
/// model gets wrong (firing on a trigger created while its condition was already true).
/// </summary>
public class TriggerDebounceTests
{
    [Theory]
    // Unseeded seeds silently: pre-existing truth -> Fired (no fire); pre-existing falsehood -> Armed.
    [InlineData(TriggerArmState.Unseeded, ConditionEvaluation.Satisfied, TriggerArmState.Fired, TriggerAction.None)]
    [InlineData(TriggerArmState.Unseeded, ConditionEvaluation.NotSatisfied, TriggerArmState.Armed, TriggerAction.None)]
    [InlineData(TriggerArmState.Unseeded, ConditionEvaluation.Unmeasurable, TriggerArmState.Unseeded, TriggerAction.None)]
    [InlineData(TriggerArmState.Unseeded, ConditionEvaluation.Indeterminate, TriggerArmState.Unseeded, TriggerAction.None)]
    // Armed: the arming edge fires, exactly once.
    [InlineData(TriggerArmState.Armed, ConditionEvaluation.Satisfied, TriggerArmState.Fired, TriggerAction.Fire)]
    [InlineData(TriggerArmState.Armed, ConditionEvaluation.NotSatisfied, TriggerArmState.Armed, TriggerAction.None)]
    [InlineData(TriggerArmState.Armed, ConditionEvaluation.Unmeasurable, TriggerArmState.Armed, TriggerAction.None)]
    [InlineData(TriggerArmState.Armed, ConditionEvaluation.Indeterminate, TriggerArmState.Armed, TriggerAction.None)]
    // Fired: debounce while true; re-arm + resolve on the opposite edge.
    [InlineData(TriggerArmState.Fired, ConditionEvaluation.Satisfied, TriggerArmState.Fired, TriggerAction.None)]
    [InlineData(TriggerArmState.Fired, ConditionEvaluation.NotSatisfied, TriggerArmState.Armed, TriggerAction.Resolve)]
    [InlineData(TriggerArmState.Fired, ConditionEvaluation.Unmeasurable, TriggerArmState.Fired, TriggerAction.None)]
    [InlineData(TriggerArmState.Fired, ConditionEvaluation.Indeterminate, TriggerArmState.Fired, TriggerAction.None)]
    public void Decide_ShouldFollowTheTransitionTable(
        TriggerArmState state, ConditionEvaluation evaluation, TriggerArmState expectedState, TriggerAction expectedAction)
    {
        TriggerDecision decision = TriggerDebounce.Decide(state, evaluation);

        decision.NextState.Should().Be(expectedState);
        decision.Action.Should().Be(expectedAction);
    }

    [Fact]
    public void Decide_ShouldNotFireOnColdStart_ThenFireOnlyOnAGenuineCrossing()
    {
        // A trigger created while its condition is already true must SEED to Fired without firing...
        TriggerDecision seed = TriggerDebounce.Decide(TriggerArmState.Unseeded, ConditionEvaluation.Satisfied);
        seed.Action.Should().Be(TriggerAction.None);
        seed.NextState.Should().Be(TriggerArmState.Fired);

        // ...stay debounced while it remains true...
        TriggerDebounce.Decide(seed.NextState, ConditionEvaluation.Satisfied).Action.Should().Be(TriggerAction.None);

        // ...re-arm (and resolve) when it goes false...
        TriggerDecision rearm = TriggerDebounce.Decide(seed.NextState, ConditionEvaluation.NotSatisfied);
        rearm.NextState.Should().Be(TriggerArmState.Armed);
        rearm.Action.Should().Be(TriggerAction.Resolve);

        // ...and only NOW fire, on the genuine crossing.
        TriggerDebounce.Decide(rearm.NextState, ConditionEvaluation.Satisfied).Action.Should().Be(TriggerAction.Fire);
    }

    [Fact]
    public void Decide_ShouldNeverActWhileUnmeasurable_HoldingWhicheverStateItWasIn()
    {
        // A blind stretch (indicator not caught up) must not re-arm a fired trigger nor arm an unseeded one.
        TriggerDebounce.Decide(TriggerArmState.Fired, ConditionEvaluation.Unmeasurable)
            .Should().Be(new TriggerDecision(TriggerArmState.Fired, TriggerAction.None));
        TriggerDebounce.Decide(TriggerArmState.Armed, ConditionEvaluation.Unmeasurable)
            .Should().Be(new TriggerDecision(TriggerArmState.Armed, TriggerAction.None));
    }
}
