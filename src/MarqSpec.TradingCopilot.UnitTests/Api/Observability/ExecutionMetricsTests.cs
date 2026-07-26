using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Observability;

/// <summary>
/// The execution-specific SLIs (gh#232, ADR-0002, engineering §7). #230 gave generic RED health; these are the
/// signals particular to a system that places orders and flattens before a close.
/// </summary>
/// <remarks>
/// The property these guard above all: <b>an absence must be detectable</b>. A counter that merely stays zero is
/// indistinguishable from a healthy quiet day, so a deadline emits a signal <i>either way</i> — the outcome is a
/// dimension, never the presence or absence of a measurement.
/// </remarks>
public class ExecutionMetricsTests : IDisposable
{
    // A UNIQUE meter per test instance. xUnit runs test classes in parallel and the flatten suites now create
    // real ExecutionMetrics too -- a listener filtering on the shared meter NAME would receive their
    // measurements mid-enumeration, which is a data race on this buffer (it failed in CI, not locally).
    private readonly string _meterName = ExecutionMetrics.MeterName + ".Test." + Guid.NewGuid().ToString("N");
    private readonly ExecutionMetrics _metrics;
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, long Value, Dictionary<string, string?> Tags)> _measurements = [];

    public ExecutionMetricsTests()
    {
        _metrics = new ExecutionMetrics(_meterName);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == _meterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Add((instrument.Name, value, TagsOf(tags))));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _measurements.Add((instrument.Name, (long)value, TagsOf(tags))));

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, string?> TagsOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string?> map = [];
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString();
        }

        return map;
    }

    private IReadOnlyList<(string Instrument, long Value, Dictionary<string, string?> Tags)> For(string instrument) =>
        [.. _measurements.Where(measurement => measurement.Instrument == instrument)];

    [Fact]
    public void RecordGateDecision_ShouldCountTheOutcome_WithTheBindingLayerAsADimension()
    {
        // "100% risk-gate coverage" is a PRD §7 hard criterion. Counting per outcome makes it observable rather
        // than merely asserted in a test suite.
        _metrics.RecordGateDecision(GateOutcome.Blocked, RiskLayer.DrawdownFloor);

        (string Instrument, long Value, Dictionary<string, string?> Tags) recorded =
            For(ExecutionMetrics.GateDecisions).Should().ContainSingle().Subject;

        recorded.Value.Should().Be(1);
        recorded.Tags["outcome"].Should().Be(nameof(GateOutcome.Blocked));
        recorded.Tags["binding_layer"].Should().Be(nameof(RiskLayer.DrawdownFloor));
    }

    [Fact]
    public void RecordGateDecision_ShouldReportNoLayer_WhenNothingBound()
    {
        // An allowed decision binds no layer. The dimension must still be PRESENT and bounded -- a missing tag
        // makes the series shape differ between outcomes and breaks aggregation.
        _metrics.RecordGateDecision(GateOutcome.Allowed, bindingLayer: null);

        For(ExecutionMetrics.GateDecisions).Should().ContainSingle()
            .Which.Tags["binding_layer"].Should().Be("none");
    }

    [Fact]
    public void RecordFlattenDeadline_ShouldEmit_EvenWhenThereWasNothingToFlatten()
    {
        // THE criterion the issue flags as most likely to be got wrong. A deadline that passes with no position
        // must still emit, or "the flatten never fired" and "there was nothing to do" are the same silence -- and
        // the failure this whole system exists to prevent would look exactly like a quiet day.
        _metrics.RecordFlattenDeadline(FlattenTier.Primary, ExecutionMetrics.FlattenNothingToDo);

        (string Instrument, long Value, Dictionary<string, string?> Tags) recorded =
            For(ExecutionMetrics.FlattenDeadlines).Should().ContainSingle().Subject;

        recorded.Value.Should().Be(1);
        recorded.Tags["outcome"].Should().Be(ExecutionMetrics.FlattenNothingToDo);
    }

    [Fact]
    public void RecordFlattenDeadline_ShouldDistinguishTheWatchdog_FromThePrimary()
    {
        // The entire point of a second tier is knowing WHICH one saved you. Merging them hides whether the
        // primary is quietly failing every day while the watchdog covers for it.
        _metrics.RecordFlattenDeadline(FlattenTier.Primary, ExecutionMetrics.FlattenExecuted);
        _metrics.RecordFlattenDeadline(FlattenTier.Watchdog, ExecutionMetrics.FlattenExecuted);

        For(ExecutionMetrics.FlattenDeadlines).Select(measurement => measurement.Tags["tier"])
            .Should().BeEquivalentTo(["primary", "watchdog"]);
    }

    [Fact]
    public void RecordTimeToFlat_ShouldRecordAHistogramValue_WhenAFlattenCompletes()
    {
        _metrics.RecordTimeToFlat(FlattenTier.Primary, TimeSpan.FromMilliseconds(1_500));

        For(ExecutionMetrics.TimeToFlat).Should().ContainSingle()
            .Which.Value.Should().Be(1_500);
    }

    [Fact]
    public void RecordOrderAck_ShouldRecordAHistogramValue_WhenTheVenueAcknowledges()
    {
        // A histogram, never a mean: the tail is the interesting part on an execution path.
        _metrics.RecordOrderAck(TimeSpan.FromMilliseconds(240));

        For(ExecutionMetrics.OrderAckLatency).Should().ContainSingle()
            .Which.Value.Should().Be(240);
    }

    [Fact]
    public void KillSwitchEngaged_ShouldBeObservable_WhenTheGaugeIsRead()
    {
        // A killed system must be visible on a dashboard, not only in a log line nobody is watching.
        _metrics.SetKillSwitchEngaged(engaged: true);
        _listener.RecordObservableInstruments();

        For(ExecutionMetrics.KillSwitchEngaged).Should().NotBeEmpty()
            .And.Subject.Last().Value.Should().Be(1);

        _metrics.SetKillSwitchEngaged(engaged: false);
        _listener.RecordObservableInstruments();

        For(ExecutionMetrics.KillSwitchEngaged).Last().Value.Should().Be(0);
    }

    [Fact]
    public void OrphanedStops_ShouldRiseAndReturnToZero_AsProtectionIsLostAndRearmed()
    {
        // The queryable form of the synthetic-risk condition: how much protection is degraded RIGHT NOW.
        _metrics.SetOrphanedStops(3);
        _listener.RecordObservableInstruments();
        For(ExecutionMetrics.OrphanedStops).Last().Value.Should().Be(3);

        _metrics.SetOrphanedStops(0);
        _listener.RecordObservableInstruments();
        For(ExecutionMetrics.OrphanedStops).Last().Value.Should().Be(0);
    }

    [Fact]
    public void RecordRetentionGap_ShouldCountPerConsumerGroup_WhenTheLogDropsEventsAhead()
    {
        // gh#227 made a silent hole loud in the log; this makes it visible on a dashboard.
        _metrics.RecordRetentionGap("stop-promotion");

        For(ExecutionMetrics.RetentionGaps).Should().ContainSingle()
            .Which.Tags["consumer_group"].Should().Be("stop-promotion");
    }

    [Fact]
    public void RecordPipelineLag_ShouldRecordPerConsumerGroup_WhenAnEventIsConsumed()
    {
        _metrics.RecordPipelineLag("conditional-order", TimeSpan.FromMilliseconds(85));

        For(ExecutionMetrics.PipelineLag).Should().ContainSingle()
            .Which.Tags["consumer_group"].Should().Be("conditional-order");
    }

    [Fact]
    public void Dimensions_ShouldBeBounded_ForEveryRecordedMeasurement()
    {
        // Cardinality is a safety property here: an unbounded label set (an account id, an order id) takes
        // Prometheus down, which would mean instrumentation causing the very outage it exists to reveal.
        _metrics.RecordGateDecision(GateOutcome.Resized, RiskLayer.SanityCap);
        _metrics.RecordFlattenDeadline(FlattenTier.Watchdog, ExecutionMetrics.FlattenEscalated);
        _metrics.RecordRetentionGap("stop-promotion");
        _metrics.RecordPipelineLag("stop-promotion", TimeSpan.FromMilliseconds(5));

        string[] permitted = ["outcome", "binding_layer", "tier", "consumer_group"];

        _measurements.SelectMany(measurement => measurement.Tags.Keys).Distinct()
            .Should().OnlyContain(tag => permitted.Contains(tag),
                "every dimension must come from a small closed set — no id may ever become a label");
    }
}
