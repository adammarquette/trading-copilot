using System.Diagnostics;
using MarqSpec.TradingCopilot.Domain.Events;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Events;

/// <summary>
/// Trace context across the event backbone's async gap (gh#230, ADR-0002, ADR-0001). A producer stamps its
/// <c>traceparent</c> on the envelope and a consumer <b>span-links</b> to it, so
/// <c>websocket → append → consume → DB write</c> is <b>one queryable trace</c> rather than two unrelated ones.
/// </summary>
/// <remarks>
/// The failure mode these guard is the one that matters on a lossy, optional field: telemetry must never be able
/// to break trading. A missing or malformed <c>traceparent</c> is <b>normal</b> — pre-migration rows carry none,
/// and a future producer may not stamp one — so every path here degrades to "start a fresh trace" and none of
/// them throws.
/// </remarks>
public class EventTracingTests
{
    private static readonly ActivitySource _source = new("MarqSpec.TradingCopilot.Tests");

    /// <summary>Forces sampling, otherwise <see cref="Activity.Current"/> is null and the tests prove nothing.</summary>
    private static ActivityListener AlwaysOn() => new()
    {
        ShouldListenTo = source => source.Name == "MarqSpec.TradingCopilot.Tests",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    [Fact]
    public void CurrentTraceParent_ShouldReturnTheActiveTraceParent_WhenInsideASpan()
    {
        using ActivityListener listener = AlwaysOn();
        ActivitySource.AddActivityListener(listener);

        using Activity? activity = _source.StartActivity("append");
        activity.Should().NotBeNull("the listener must sample, or this test cannot prove anything");

        EventTracing.CurrentTraceParent().Should().Be(activity!.Id);
    }

    [Fact]
    public void CurrentTraceParent_ShouldReturnNull_WhenThereIsNoActiveSpan()
    {
        // Not an error: a producer running outside a trace simply stamps nothing, and the consumer starts fresh.
        Activity.Current = null;

        EventTracing.CurrentTraceParent().Should().BeNull();
    }

    [Fact]
    public void TryCreateLink_ShouldLinkToTheProducer_WhenTheTraceParentIsWellFormed()
    {
        using ActivityListener listener = AlwaysOn();
        ActivitySource.AddActivityListener(listener);
        using Activity? producer = _source.StartActivity("produce");
        string traceParent = producer!.Id!;

        EventTracing.TryCreateLink(traceParent, out ActivityLink link).Should().BeTrue();

        // The link must point at the PRODUCER's trace -- that is the whole point: one trace across the gap.
        link.Context.TraceId.Should().Be(producer.TraceId);
        link.Context.SpanId.Should().Be(producer.SpanId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreateLink_ShouldReportNoLink_WhenTheTraceParentIsAbsent(string? traceParent)
    {
        // Absence is the pre-migration and never-stamped case. It is ordinary, so it must not be an error.
        EventTracing.TryCreateLink(traceParent, out ActivityLink link).Should().BeFalse();
        link.Context.TraceId.Should().Be(default(ActivityTraceId));
    }

    [Fact]
    public void TryCreateLink_ShouldStillLink_WhenTheTraceParentDeclaresAFutureVersion()
    {
        // NOT malformed, though it looks it. W3C trace-context is deliberately forward-compatible: a parser that
        // recognises the shape must accept an unknown version rather than drop the trace. Asserting a rejection
        // here would have pinned a bug -- my first draft did exactly that and this is the corrected expectation.
        EventTracing.TryCreateLink("99-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", out ActivityLink link)
            .Should().BeTrue();

        link.Context.TraceId.ToString().Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
    }

    [Theory]
    [InlineData("not-a-traceparent")]
    [InlineData("00-tooshort-0000000000000001-01")]
    [InlineData("00-00000000000000000000000000000000-0000000000000000-00")]
    public void TryCreateLink_ShouldReportNoLinkWithoutThrowing_WhenTheTraceParentIsMalformed(string traceParent)
    {
        // A corrupt field must never take down a consumer on a safety-critical path. Losing the trace is a
        // reporting loss; throwing here would stop stop-promotion or conditional firing outright.
        Action attempt = () => EventTracing.TryCreateLink(traceParent, out _);

        attempt.Should().NotThrow();
        EventTracing.TryCreateLink(traceParent, out _).Should().BeFalse();
    }
}
