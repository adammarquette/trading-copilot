using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Triggers;

/// <summary>
/// The debounce state machine (ADR-0008) — the whole transition matrix, because each cell is a real behaviour:
/// fire-once on the arming edge, debounce while satisfied, re-arm on the opposite edge, seed pre-existing truth
/// silently, and hold on any unmeasurable / dead-band reading (fail-closed).
/// </summary>
public class TriggerDebounceTests
{
    [Theory]
    // Armed: the arming edge fires; staying unsatisfied holds.
    [InlineData(TriggerArmState.Armed, ConditionSatisfaction.Satisfied, TriggerArmState.Fired, true, false)]
    [InlineData(TriggerArmState.Armed, ConditionSatisfaction.NotSatisfied, TriggerArmState.Armed, false, false)]
    // Fired: continuously-satisfied fires ONCE (debounce); the opposite edge re-arms.
    [InlineData(TriggerArmState.Fired, ConditionSatisfaction.Satisfied, TriggerArmState.Fired, false, false)]
    [InlineData(TriggerArmState.Fired, ConditionSatisfaction.NotSatisfied, TriggerArmState.Armed, false, true)]
    // Unseeded: adopt pre-existing truth SILENTLY (no fire on the first scan), or arm.
    [InlineData(TriggerArmState.Unseeded, ConditionSatisfaction.Satisfied, TriggerArmState.Fired, false, false)]
    [InlineData(TriggerArmState.Unseeded, ConditionSatisfaction.NotSatisfied, TriggerArmState.Armed, false, false)]
    public void Decide_ShouldFollowTheMatrix(
        TriggerArmState state,
        ConditionSatisfaction satisfaction,
        TriggerArmState nextState,
        bool shouldFire,
        bool reArmed)
    {
        TriggerDecision decision = TriggerDebounce.Decide(state, satisfaction);

        decision.Should().Be(new TriggerDecision(nextState, shouldFire, reArmed));
    }

    [Theory]
    [InlineData(TriggerArmState.Unseeded)]
    [InlineData(TriggerArmState.Armed)]
    [InlineData(TriggerArmState.Fired)]
    public void Decide_ShouldHold_OnAnUnmeasurableReading(TriggerArmState state)
    {
        // A null indicator must never fire and never re-arm -- a gap on a Fired trigger cannot collapse to
        // "not satisfied" and then spuriously re-fire when the value returns still-satisfied.
        TriggerDebounce.Decide(state, ConditionSatisfaction.Unmeasurable)
            .Should().Be(new TriggerDecision(state, false, false));
    }

    [Theory]
    [InlineData(TriggerArmState.Unseeded)]
    [InlineData(TriggerArmState.Armed)]
    [InlineData(TriggerArmState.Fired)]
    public void Decide_ShouldHold_OnAHysteresisDeadBandReading(TriggerArmState state)
    {
        TriggerDebounce.Decide(state, ConditionSatisfaction.Indeterminate)
            .Should().Be(new TriggerDecision(state, false, false));
    }

    [Fact]
    public void Decide_ShouldNotReFire_WhenAFiredTriggerReloadsStillSatisfied()
    {
        // Restart safety: ArmState is persisted, so a still-satisfied trigger reloads as Fired and must not re-fire.
        TriggerDebounce.Decide(TriggerArmState.Fired, ConditionSatisfaction.Satisfied)
            .ShouldFire.Should().BeFalse();
    }

    [Fact]
    public void Decide_ShouldNotFire_WhenSeedingPreExistingTruth()
    {
        // A trigger created while the condition is already satisfied must adopt that state silently, not alert.
        TriggerDecision decision = TriggerDebounce.Decide(TriggerArmState.Unseeded, ConditionSatisfaction.Satisfied);

        decision.ShouldFire.Should().BeFalse();
        decision.NextState.Should().Be(TriggerArmState.Fired);
    }
}
