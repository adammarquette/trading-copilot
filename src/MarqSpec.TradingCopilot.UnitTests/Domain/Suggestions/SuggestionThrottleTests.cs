using MarqSpec.TradingCopilot.Domain.Suggestions;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Suggestions;

/// <summary>
/// The suggestion-issuance throttle (R-4 / R-5, gh#588) — a PURE, deterministic policy that throttles as
/// daily-drawdown headroom depletes and suppresses at the governor or the daily-target stand-down. Dependency-free,
/// so these are mocks-free unit tests. The invariants that matter and are floored at WE 4 because they are
/// safety-adjacent: it can only ever <b>reduce or suppress</b> — never admit more than Full would — and a
/// model-authored confidence can only filter a candidate out, <b>never</b> lift the headroom-derived cap.
/// </summary>
public class SuggestionThrottleTests
{
    private static readonly SuggestionThrottle _throttle = new();

    private static SuggestionThrottlePolicy Policy(
        decimal threshold = 0.5m, int fullCap = 8, int floor = 70, bool standDown = true) =>
        SuggestionThrottlePolicy.Declare(threshold, fullCap, floor, standDown);

    // ---- the three modes ----

    [Fact]
    public void Decide_ShouldBeFull_WhenHeadroomIsHealthy()
    {
        SuggestionThrottleDecision d = _throttle.Decide(Policy(fullCap: 8), new(HeadroomFraction: 1m, DailyTargetReached: false));

        d.Mode.Should().Be(SuggestionThrottleMode.Full);
        d.PerWindowCap.Should().Be(8);
        d.RequiredConfidence.Should().Be(0, "Full imposes no conviction filter");
        d.Reason.Should().Be(SuggestionThrottleReason.None);
        d.IsSuppressed.Should().BeFalse();
    }

    [Fact]
    public void Decide_ShouldThrottle_WhenHeadroomIsInTheBand()
    {
        SuggestionThrottleDecision d = _throttle.Decide(Policy(threshold: 0.5m, fullCap: 8, floor: 70), new(0.25m, false));

        d.Mode.Should().Be(SuggestionThrottleMode.Throttled);
        d.PerWindowCap.Should().BeLessThan(8).And.BePositive("throttled still surfaces the best few, never zero");
        d.RequiredConfidence.Should().Be(70, "throttled is higher-conviction only");
        d.Reason.Should().Be(SuggestionThrottleReason.None, "throttled is not suppressed");
    }

    [Fact]
    public void Decide_ShouldSuppressWithGovernorReason_WhenHeadroomIsExhausted()
    {
        SuggestionThrottleDecision d = _throttle.Decide(Policy(), new(HeadroomFraction: -0.1m, DailyTargetReached: false));

        d.Mode.Should().Be(SuggestionThrottleMode.Suppressed);
        d.Reason.Should().Be(SuggestionThrottleReason.GovernorReached);
        d.PerWindowCap.Should().Be(0);
        d.IsSuppressed.Should().BeTrue();
        d.Admits(issuedInWindow: 0, confidence: 100).Should().BeFalse("suppressed admits nothing, at any confidence");
    }

    [Fact]
    public void Decide_ShouldSuppressWithStandDownReason_WhenTheDailyTargetIsHit()
    {
        SuggestionThrottleDecision d = _throttle.Decide(Policy(standDown: true), new(HeadroomFraction: 1m, DailyTargetReached: true));

        d.Mode.Should().Be(SuggestionThrottleMode.Suppressed);
        d.Reason.Should().Be(SuggestionThrottleReason.DailyTargetStandDown);
        d.PerWindowCap.Should().Be(0);
    }

    [Fact]
    public void Decide_ShouldNotSuppress_WhenTheTargetIsHitButStandDownIsOff()
    {
        // Stand-down is opt-in per account (the wireframe's "stop for day"). With it off, hitting the target changes
        // nothing — headroom alone decides.
        SuggestionThrottleDecision d = _throttle.Decide(Policy(standDown: false), new(HeadroomFraction: 1m, DailyTargetReached: true));

        d.IsSuppressed.Should().BeFalse();
        d.Mode.Should().Be(SuggestionThrottleMode.Full);
    }

    [Fact]
    public void Decide_ShouldPreferTheGovernorReason_WhenBothGovernorAndTargetApply()
    {
        // Both suppress, but a depleted drawdown governor is the harder stop (manage / flatten), so it is the reason
        // surfaced to the operator.
        SuggestionThrottleDecision d = _throttle.Decide(Policy(), new(HeadroomFraction: 0m, DailyTargetReached: true));

        d.Reason.Should().Be(SuggestionThrottleReason.GovernorReached);
    }

    // ---- boundaries ----

    [Fact]
    public void Decide_ShouldBeFull_AtExactlyTheThreshold()
    {
        _throttle.Decide(Policy(threshold: 0.5m), new(0.5m, false)).Mode.Should().Be(SuggestionThrottleMode.Full);
    }

    [Fact]
    public void Decide_ShouldThrottle_JustBelowTheThreshold()
    {
        _throttle.Decide(Policy(threshold: 0.5m), new(0.499m, false)).Mode.Should().Be(SuggestionThrottleMode.Throttled);
    }

    [Fact]
    public void Decide_ShouldSuppressAtGovernor_AtExactlyZeroHeadroom()
    {
        SuggestionThrottleDecision d = _throttle.Decide(Policy(), new(0m, false));

        d.Mode.Should().Be(SuggestionThrottleMode.Suppressed);
        d.Reason.Should().Be(SuggestionThrottleReason.GovernorReached);
    }

    // ---- the cap arithmetic across headroom fractions ----

    [Theory]
    [InlineData(0.5, 8)]      // at the threshold -> Full's cap
    [InlineData(0.4375, 7)]
    [InlineData(0.375, 6)]
    [InlineData(0.25, 4)]
    [InlineData(0.125, 2)]
    [InlineData(0.0625, 1)]
    [InlineData(0.03125, 1)]  // scales below one but is floored there — the single best setup still surfaces
    public void Decide_ShouldScaleTheCapWithHeadroom(decimal headroom, int expectedCap)
    {
        _throttle.Decide(Policy(threshold: 0.5m, fullCap: 8), new(headroom, false))
            .PerWindowCap.Should().Be(expectedCap);
    }

    // ---- confidence is a filter only, never a lever on the cap (the R-4 invariant) ----

    [Fact]
    public void Admits_ShouldNeverLetConfidenceLiftTheCap()
    {
        // The cap is computed from headroom alone. At the cap, even a perfect-confidence candidate is refused — a
        // model cannot inflate its own number to escape the throttle.
        SuggestionThrottleDecision throttled = _throttle.Decide(Policy(threshold: 0.5m, fullCap: 8), new(0.25m, false));
        throttled.Mode.Should().Be(SuggestionThrottleMode.Throttled);

        throttled.Admits(issuedInWindow: throttled.PerWindowCap, confidence: 100).Should().BeFalse();
        throttled.Admits(issuedInWindow: throttled.PerWindowCap - 1, confidence: 100).Should().BeTrue();
    }

    [Fact]
    public void Admits_ShouldRequireTheConvictionFloor_WhenThrottled()
    {
        SuggestionThrottleDecision throttled = _throttle.Decide(Policy(floor: 70), new(0.25m, false));

        throttled.Admits(issuedInWindow: 0, confidence: 69).Should().BeFalse("below the floor");
        throttled.Admits(issuedInWindow: 0, confidence: 70).Should().BeTrue("meets the floor");
    }

    [Fact]
    public void Admits_ShouldNotFilterOnConfidence_WhenFull()
    {
        SuggestionThrottleDecision full = _throttle.Decide(Policy(), new(1m, false));

        full.RequiredConfidence.Should().Be(0);
        full.Admits(issuedInWindow: 0, confidence: 0).Should().BeTrue("Full admits any conviction under its cap");
    }

    [Fact]
    public void Admits_ShouldRefuse_AtOrBeyondTheCap()
    {
        SuggestionThrottleDecision full = _throttle.Decide(Policy(fullCap: 8), new(1m, false));

        full.Admits(issuedInWindow: 7, confidence: 100).Should().BeTrue("under the cap");
        full.Admits(issuedInWindow: 8, confidence: 100).Should().BeFalse("at the cap");
        full.Admits(issuedInWindow: 9, confidence: 100).Should().BeFalse("beyond the cap");
    }

    // ---- the safety invariant: only ever reduce or suppress ----

    [Fact]
    public void Decide_ShouldNeverAdmitMoreThanFullWould_ForAnyInput()
    {
        // A throttle BUG must never be able to INCREASE issuance. Over a grid of day-states: the cap never exceeds
        // Full's, the conviction floor never drops below Full's (zero), and no candidate is admitted that Full would
        // itself refuse.
        SuggestionThrottlePolicy policy = Policy(threshold: 0.5m, fullCap: 8, floor: 70);
        SuggestionThrottleDecision full = _throttle.Decide(policy, new(HeadroomFraction: 10m, DailyTargetReached: false));
        full.Mode.Should().Be(SuggestionThrottleMode.Full);

        decimal[] headrooms = [-1m, -0.01m, 0m, 0.01m, 0.1m, 0.25m, 0.49m, 0.5m, 0.51m, 1m, 5m];
        foreach (decimal headroom in headrooms)
        {
            foreach (bool target in new[] { false, true })
            {
                SuggestionThrottleDecision d = _throttle.Decide(policy, new(headroom, target));

                d.PerWindowCap.Should().BeLessThanOrEqualTo(full.PerWindowCap, "the cap can only ever scale down");
                d.RequiredConfidence.Should().BeGreaterThanOrEqualTo(full.RequiredConfidence, "the floor can only rise");

                for (int count = 0; count <= full.PerWindowCap + 1; count++)
                {
                    foreach (int confidence in new[] { 0, 50, 70, 100 })
                    {
                        if (d.Admits(count, confidence))
                        {
                            full.Admits(count, confidence).Should()
                                .BeTrue("no input may admit a candidate that Full would itself refuse");
                        }
                    }
                }
            }
        }
    }

    // ---- the decision always explains itself (a caller quotes it to the operator) ----

    [Theory]
    [InlineData(1.0, false)]     // Full
    [InlineData(0.25, false)]    // Throttled
    [InlineData(-0.1, false)]    // Suppressed — governor
    [InlineData(1.0, true)]      // Suppressed — stand-down
    public void Decide_ShouldAlwaysPopulateAnExplanation(decimal headroom, bool target)
    {
        _throttle.Decide(Policy(), new(headroom, target)).Explanation.Should().NotBeNullOrWhiteSpace();
    }

    // ---- Declare enforces its invariants up front ----

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Declare_ShouldRejectAnOutOfRangeThreshold(decimal threshold)
    {
        Action act = () => SuggestionThrottlePolicy.Declare(threshold, fullWindowCap: 8, throttledConvictionFloor: 70, standDownOnDailyTarget: true);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Declare_ShouldRejectANonPositiveCap(int cap)
    {
        Action act = () => SuggestionThrottlePolicy.Declare(0.5m, cap, throttledConvictionFloor: 70, standDownOnDailyTarget: true);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Declare_ShouldRejectAnOutOfRangeConvictionFloor(int floor)
    {
        Action act = () => SuggestionThrottlePolicy.Declare(0.5m, fullWindowCap: 8, floor, standDownOnDailyTarget: true);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Declare_ShouldReturnThePolicy_WhenEveryValueIsInRange()
    {
        SuggestionThrottlePolicy policy = SuggestionThrottlePolicy.Declare(0.5m, 8, 70, standDownOnDailyTarget: true);

        policy.ThrottleThresholdFraction.Should().Be(0.5m);
        policy.FullWindowCap.Should().Be(8);
        policy.ThrottledConvictionFloor.Should().Be(70);
        policy.StandDownOnDailyTarget.Should().BeTrue();
    }
}
