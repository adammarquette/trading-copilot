using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Triggers;

/// <summary>
/// Distinguishing a <b>transient</b> unmeasurable reading from a trigger that has gone permanently inert (gh#469).
/// </summary>
/// <remarks>
/// <para>
/// The fail-closed half already worked: a null indicator reads <see cref="ConditionSatisfaction.Unmeasurable"/>,
/// and <see cref="TriggerDebounce"/> holds — never a fire, never a re-arm. What was missing is that nothing said
/// so. A trigger whose dependency stopped being produced — the symbol dropped from <c>Ingestion:Symbols</c>, the
/// resolution changed, the indicator left <c>IndicatorSet</c> — simply stopped firing, and <i>"the condition never
/// occurred"</i> was indistinguishable from <i>"unevaluable for a week"</i>.
/// </para>
/// <para>
/// The distinction is <b>duration</b>, which is why it needs state rather than a log line per pass. One missed
/// scan is a late bar and completely normal; a day of them is a broken trigger. This decides which is which.
/// </para>
/// </remarks>
public class TriggerStalenessTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 30, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Track_ShouldStartTheClock_WhenAMeasurableTriggerFirstGoesUnmeasurable()
    {
        TriggerStaleness result = TriggerStaleness.Track(unmeasurableSince: null, ConditionSatisfaction.Unmeasurable, _now);

        result.UnmeasurableSince.Should().Be(_now, "the clock starts at the first miss, so duration is knowable");
        result.ShouldReport.Should().BeFalse("a single miss is a late bar, not a broken dependency");
    }

    [Fact]
    public void Track_ShouldKeepTheOriginalInstant_WhenItIsStillUnmeasurable()
    {
        // The clock must not restart every pass, or the duration never grows and the threshold is never crossed --
        // the trigger would stay silently inert forever, which is the whole defect.
        TriggerStaleness result = TriggerStaleness.Track(
            unmeasurableSince: _now, ConditionSatisfaction.Unmeasurable, _now.AddMinutes(30));

        result.UnmeasurableSince.Should().Be(_now, "the clock measures the outage, so it keeps its start");
    }

    [Fact]
    public void Track_ShouldReport_OnceTheOutageOutlastsTheThreshold()
    {
        TriggerStaleness result = TriggerStaleness.Track(
            unmeasurableSince: _now, ConditionSatisfaction.Unmeasurable, _now + TriggerStaleness.ReportAfter);

        result.ShouldReport.Should().BeTrue("past the threshold this is a dependency that stopped being produced");
    }

    [Fact]
    public void Track_ShouldNotReport_WhileTheOutageIsStillWithinTheThreshold()
    {
        TriggerStaleness result = TriggerStaleness.Track(
            unmeasurableSince: _now,
            ConditionSatisfaction.Unmeasurable,
            _now + TriggerStaleness.ReportAfter - TimeSpan.FromSeconds(1));

        result.ShouldReport.Should().BeFalse("below the threshold it is still plausibly a late bar");
    }

    [Theory]
    [InlineData(ConditionSatisfaction.Satisfied)]
    [InlineData(ConditionSatisfaction.NotSatisfied)]
    public void Track_ShouldClearTheClock_WhenAReadingIsMeasurableAgain(ConditionSatisfaction measurable)
    {
        // Recovery must be silent and complete. A trigger that healed is not broken, and leaving the clock set
        // would report it forever -- alert fatigue over a resolved condition (ADR-0019's noise budget).
        TriggerStaleness result = TriggerStaleness.Track(
            unmeasurableSince: _now, measurable, _now + TriggerStaleness.ReportAfter * 10);

        result.UnmeasurableSince.Should().BeNull("a measurable reading means the dependency is back");
        result.ShouldReport.Should().BeFalse();
    }

    [Fact]
    public void Track_ShouldNotStartTheClock_WhenTheReadingIsIndeterminate()
    {
        // Indeterminate is a hysteresis dead-band: the indicator WAS measured, the level is just inside the band.
        // That is a working trigger holding position, not a missing dependency, and conflating them would report
        // every hysteresis trigger as broken.
        TriggerStaleness result = TriggerStaleness.Track(
            unmeasurableSince: null, ConditionSatisfaction.Indeterminate, _now);

        result.UnmeasurableSince.Should().BeNull("a dead-band reading is measured — the dependency resolved fine");
        result.ShouldReport.Should().BeFalse();
    }

    [Fact]
    public void Track_ShouldClearTheClock_WhenAnIndeterminateReadingFollowsAnOutage()
    {
        // Same reasoning in the recovery direction: reaching the dead band proves the indicator is being produced.
        TriggerStaleness result = TriggerStaleness.Track(
            unmeasurableSince: _now, ConditionSatisfaction.Indeterminate, _now.AddDays(1));

        result.UnmeasurableSince.Should().BeNull();
        result.ShouldReport.Should().BeFalse();
    }

    [Fact]
    public void ReportAfter_ShouldOutlastSeveralScans_SoOneLateBarIsNeverReported()
    {
        // The threshold is only meaningful relative to the scan interval: at the default 60 s poll it must span
        // several passes, or a single slow bar trips it and the report becomes noise nobody reads.
        TriggerStaleness.ReportAfter.Should().BeGreaterThan(
            TimeSpan.FromMinutes(5), "it must outlast several scan intervals, not one late bar");
    }
}
