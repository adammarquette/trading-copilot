using System.Diagnostics;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// Trace continuity across the event log's <b>async gap</b> (gh#329 ⇒ gh#230, ADR-0002, engineering §7): a producer
/// stamps its W3C <c>traceparent</c> on the envelope and the consumer <b>span-links</b> back to it, so
/// <c>websocket → append → consume → DB write</c> renders as <b>one</b> queryable trace instead of breaking at the log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this tier, given <c>EventTracingTests</c> already unit-tests the helper.</b> Those tests call
/// <c>TryCreateLink</c> on an in-memory string. Nothing there crosses the gap the property is about: the traceparent
/// is <b>persisted to Postgres</b> (a <c>varchar(64)</c> column) and read back by a <b>different</b>, asynchronous
/// process before it is used. This suite runs the <b>live</b> <c>StopPromotionHost</c> / <c>ConditionalOrderHost</c>
/// against real Postgres, so the assertion is on the span a genuine consumer actually emitted.
/// </para>
/// <para>
/// <b>Inputs are fed; the answer is not.</b> The test supplies the producer's traceparent (exactly as
/// <c>QuoteIngestionService</c> does — its half is unit-covered) and asserts the <i>consumer's</i> link, which is the
/// system's computed answer. Written from ADR-0002 / engineering §7 rather than from the consumer's code.
/// </para>
/// <para>
/// <b>Not covered here — a filed defect, not an omission.</b> The metric→trace <b>exemplar</b> pivot ADR-0002 §19 and
/// engineering §7 both require is <b>not enabled</b> in the metrics pipeline (no exemplar filter is configured, and
/// the SDK default is off), so no exemplar can be observed at any tier. Reported as <c>gh#338</c> rather than fixed
/// (QA contract rule 3); its verification lands with that fix and the staging tier (gh#332).
/// </para>
/// </remarks>
public class TelemetryIntegrationTests : IClassFixture<TelemetryTestPostgresFactory>, IDisposable
{
    /// <summary>The consume spans the two live event-log consumers emit — the subject of every case here.</summary>
    private static string[] ConsumeSpanNames { get; } = ["stop-promotion.consume", "conditional-order.consume"];

    private const string Contract = "projectx:CON.F.US.MES.U26";

    private readonly TelemetryTestPostgresFactory _factory;
    private readonly ActivityListener _listener;
    private readonly List<Activity> _spans = [];
    private readonly Lock _gate = new();

    // Resolved BEFORE the listener is registered, and held as a plain string. Reading TelemetryRegistration.Source
    // from inside ShouldListenTo would re-enter its static initializer — constructing an ActivitySource notifies
    // existing listeners — and observe the field mid-initialisation.
    private readonly string _sourceName = TelemetryRegistration.Source.Name;

    public TelemetryIntegrationTests(TelemetryTestPostgresFactory factory)
    {
        _factory = factory;

        // Listen to the application's OWN source, so what is captured is what the app really emitted. Sampling
        // AllData guarantees the activity is created regardless of the SDK's own sampling, so a missing span means
        // the consumer did not start one — never that the listener declined it.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == _sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_gate)
                {
                    _spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Trace_ShouldLinkTheConsumerSpanToTheProducer_AcrossTheEventLogsAsyncGap()
    {
        // THE HEADLINE GUARD (engineering §7's stated priority: "websocket → record" traceability). The consumer runs
        // in another pass, minutes later in principle, so it LINKS rather than parents — but the link must carry the
        // producer's trace id, or the trace breaks at the log and the waterfall is two disconnected halves.
        using Activity producer = StartProducerSpan();
        string traceParent = Activity.Current!.Id!;
        ActivityTraceId producerTrace = producer.TraceId;

        await AppendQuoteAsync(traceParent);

        Activity linked = await WaitForConsumeSpanAsync(
            span => span.Links.Any(link => link.Context.TraceId == producerTrace),
            "a consume span linked back to the producer's trace");

        linked.Links.Should().Contain(
            link => link.Context.TraceId == producerTrace,
            "the consumer's span must name the producer's trace, so one trace spans the async gap");
    }

    [Fact]
    public async Task Trace_ShouldPreserveTheTraceparentExactly_ThroughThePersistedRow()
    {
        // The link is only as good as the string that survives the round trip. The column is varchar(64) and a W3C
        // traceparent is 55 characters, so it fits TODAY — but a silent truncation (or any normalisation) would
        // break every link downstream while every unit test, which never touches the database, stayed green.
        using Activity producer = StartProducerSpan();
        string traceParent = Activity.Current!.Id!;

        EventEnvelope appended = await AppendQuoteAsync(traceParent);
        EventEnvelope readBack = await ReadBackAsync(appended.Sequence);

        readBack.TraceParent.Should().Be(
            traceParent, "the traceparent must survive Postgres byte-identically or the consumer links to nothing");
    }

    [Fact]
    public async Task Trace_ShouldConsumeWithoutLinking_WhenTheEventCarriesNoTraceparent()
    {
        // The field is optional: pre-migration rows carry none, and a producer outside a trace stamps none. That must
        // start a FRESH trace, not suppress the consumer's span and not raise — losing a trace is a reporting loss,
        // refusing to consume would stop stop-promotion outright (engineering §9).
        long before = await LatestSequenceAsync();

        await AppendQuoteAsync(traceParent: null);

        Activity span = await WaitForConsumeSpanAsync(
            candidate => candidate.StartTimeUtc > DateTime.UtcNow.AddMinutes(-2) && candidate.Links.Count() == 0,
            "a consume span with no link");

        span.Links.Should().BeEmpty("an absent traceparent links to nothing");
        span.TraceId.Should().NotBe(default(ActivityTraceId), "it still starts a trace of its own");
        (await LatestSequenceAsync()).Should().BeGreaterThan(before, "and the event was still consumed, not skipped");
    }

    [Fact]
    public async Task Trace_ShouldKeepConsuming_WhenThePersistedTraceparentIsMalformed()
    {
        // A foreign or corrupted producer writes a traceparent this consumer cannot parse. It must be IGNORED, never
        // thrown on: the consumers ride safety-critical paths, so a malformed reporting field must not stall the loop
        // that promotes stops. (A traceparent with an unknown VERSION is deliberately not exercised as malformed --
        // W3C is forward-compatible and ADR-0002 records that asserting a rejection there would pin a bug.)
        await AppendQuoteAsync(traceParent: "not-a-traceparent");

        Activity span = await WaitForConsumeSpanAsync(
            candidate => candidate.Links.Count() == 0, "a consume span despite the malformed traceparent");
        span.Links.Should().BeEmpty("an unparseable traceparent yields no link");

        // The load-bearing half: the consumer is still alive and still consuming AFTERWARDS. A throw inside the loop
        // would leave this second, well-formed event unconsumed.
        using Activity producer = StartProducerSpan();
        ActivityTraceId producerTrace = producer.TraceId;
        await AppendQuoteAsync(Activity.Current!.Id!);

        await WaitForConsumeSpanAsync(
            candidate => candidate.Links.Any(link => link.Context.TraceId == producerTrace),
            "the consumer to keep working after the malformed row — a reporting fault must never stall a safety consumer");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>A producer span on the application's own source — the trace the consumer must link back to.</summary>
    private static Activity StartProducerSpan()
    {
        Activity? producer = TelemetryRegistration.Source.StartActivity("test.producer", ActivityKind.Producer);
        producer.Should().NotBeNull("the listener samples everything, so the producer span must exist");
        return producer!;
    }

    /// <summary>
    /// Appends a real <c>market.quote</c> event through the live event log, stamping <paramref name="traceParent"/>
    /// exactly as <c>QuoteIngestionService</c> stamps <c>EventTracing.CurrentTraceParent()</c> — an input fed to the
    /// system, never the answer under test.
    /// </summary>
    private async Task<EventEnvelope> AppendQuoteAsync(string? traceParent)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        string payload = JsonSerializer.Serialize(new
        {
            contract = Contract,
            bid = 5_295.00m,
            ask = 5_295.25m,
            bidSize = 10,
            askSize = 12,
            timestamp = DateTimeOffset.UtcNow,
        });

        return await log.AppendAsync(
            new EventDraft(QuoteIngestionService.QuoteEventType, "projectx", DateTimeOffset.UtcNow, payload)
            {
                Id = Guid.NewGuid(), // a distinct market state per append, so nothing is deduped away
                TraceParent = traceParent,
            },
            CancellationToken.None);
    }

    private async Task<EventEnvelope> ReadBackAsync(long sequence)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        EventPage page = await log.ReadAfterAsync(sequence - 1, 10, CancellationToken.None);
        return page.Events.Single(candidate => candidate.Sequence == sequence);
    }

    private async Task<long> LatestSequenceAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        EventPage page = await log.ReadAfterAsync(0, int.MaxValue, CancellationToken.None);
        return page.Events.Count == 0 ? 0 : page.Events.Max(candidate => candidate.Sequence);
    }

    /// <summary>Waits for a consume span from a live consumer matching <paramref name="match"/>; the consumers poll
    /// on a one-second idle cadence, so this polls rather than assuming an interval.</summary>
    private async Task<Activity> WaitForConsumeSpanAsync(Func<Activity, bool> match, string because)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            lock (_gate)
            {
                Activity? found = _spans.FirstOrDefault(
                    span => ConsumeSpanNames.Contains(span.OperationName, StringComparer.Ordinal) && match(span));
                if (found is not null)
                {
                    return found;
                }
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for {because}.");
    }
}
