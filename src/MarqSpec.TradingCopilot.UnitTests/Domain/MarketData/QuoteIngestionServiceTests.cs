using System.Diagnostics;
using System.Text.Json;
using FakeItEasy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.MarketData;

/// <summary>
/// The event backbone's <b>first producer</b> (ADR-0001, gh#13/R-1): the venue's live quote stream appended to
/// the append-only log. The behaviours that matter are the ones ADR-0001's at-least-once contract rests on —
/// a <b>deterministic event id</b> so a reconnect that replays overlapping quotes is dedupable, the producer's
/// trace riding the envelope across the async boundary, and a failing append surfacing rather than silently
/// dropping market data.
/// </summary>
public class QuoteIngestionServiceTests
{
    private readonly IEventLog _log = A.Fake<IEventLog>();
    private readonly IMarketDataSource _source = A.Fake<IMarketDataSource>();
    private readonly QuoteIngestionService _service;

    private static VenueId Venue => VenueId.Parse("projectx");
    private static VenueContractId Contract => VenueContractId.Create(Venue, "CON.F.US.MES.U26");

    public QuoteIngestionServiceTests()
    {
        A.CallTo(() => _source.Id).Returns(Venue);
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .ReturnsLazily((EventDraft d, CancellationToken _) => Task.FromResult(
                new EventEnvelope(d.Id ?? Guid.NewGuid(), 1, d.Type, d.Source, d.OccurredAt, DateTimeOffset.UnixEpoch, d.Payload, d.TraceParent)));

        _service = new QuoteIngestionService(_log);
    }

    private static Quote QuoteAt(DateTimeOffset at, decimal bid = 5_300.25m, decimal ask = 5_300.50m) =>
        new(at, new Price(bid), new Price(ask), BidSize: 12, AskSize: 8);

    private void SourceStreams(params Quote[] quotes)
    {
        A.CallTo(() => _source.StreamQuotesAsync(A<VenueContractId>._, A<CancellationToken>._))
            .Returns(ToAsyncEnumerable(quotes));
    }

    private static async IAsyncEnumerable<Quote> ToAsyncEnumerable(IEnumerable<Quote> quotes)
    {
        foreach (Quote quote in quotes)
        {
            yield return quote;
            await Task.Yield();
        }
    }

    private List<EventDraft> CapturedDrafts()
    {
        List<EventDraft> drafts = [];
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .Invokes((EventDraft d, CancellationToken _) => drafts.Add(d))
            .ReturnsLazily((EventDraft d, CancellationToken _) => Task.FromResult(
                new EventEnvelope(d.Id ?? Guid.NewGuid(), 1, d.Type, d.Source, d.OccurredAt, DateTimeOffset.UnixEpoch, d.Payload, d.TraceParent)));
        return drafts;
    }

    [Fact]
    public async Task IngestQuotesAsync_ShouldAppendOneEventPerQuote_TypedAndSourced()
    {
        List<EventDraft> drafts = CapturedDrafts();
        SourceStreams(QuoteAt(DateTimeOffset.UnixEpoch), QuoteAt(DateTimeOffset.UnixEpoch.AddSeconds(1)));

        int appended = await _service.IngestQuotesAsync(_source, Contract, CancellationToken.None);

        appended.Should().Be(2);
        drafts.Should().HaveCount(2);
        drafts[0].Type.Should().Be(QuoteIngestionService.QuoteEventType);
        drafts[0].Source.Should().Be("projectx");           // the venue that produced it
        drafts[0].OccurredAt.Should().Be(DateTimeOffset.UnixEpoch); // the venue's timestamp, not ours
    }

    [Fact]
    public async Task IngestQuotesAsync_ShouldCarryTheQuoteInThePayload()
    {
        List<EventDraft> drafts = CapturedDrafts();
        SourceStreams(QuoteAt(DateTimeOffset.UnixEpoch, bid: 5_300.25m, ask: 5_300.50m));

        await _service.IngestQuotesAsync(_source, Contract, CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(drafts.Single().Payload);
        payload.RootElement.GetProperty("contract").GetString().Should().Be(Contract.ToString());
        payload.RootElement.GetProperty("bid").GetDecimal().Should().Be(5_300.25m);
        payload.RootElement.GetProperty("ask").GetDecimal().Should().Be(5_300.50m);
        payload.RootElement.GetProperty("bidSize").GetInt64().Should().Be(12);
    }

    [Fact]
    public async Task IngestQuotesAsync_ShouldAssignADeterministicId_SoAReplayedQuoteIsDedupable()
    {
        // ADR-0001 is at-least-once and tells consumers to dedupe by event id. A websocket reconnect replays
        // overlapping quotes -- with a random id per append those duplicates would be undedupable, so the id is
        // derived from the quote itself: same market state in, same id out.
        List<EventDraft> first = CapturedDrafts();
        SourceStreams(QuoteAt(DateTimeOffset.UnixEpoch));
        await _service.IngestQuotesAsync(_source, Contract, CancellationToken.None);

        List<EventDraft> replay = CapturedDrafts();
        SourceStreams(QuoteAt(DateTimeOffset.UnixEpoch));
        await _service.IngestQuotesAsync(_source, Contract, CancellationToken.None);

        first.Single().Id.Should().NotBeNull();
        replay.Single().Id.Should().Be(first.Single().Id!.Value);
    }

    [Fact]
    public async Task IngestQuotesAsync_ShouldAssignDifferentIds_ToDifferentMarketStates()
    {
        List<EventDraft> drafts = CapturedDrafts();
        SourceStreams(
            QuoteAt(DateTimeOffset.UnixEpoch, bid: 5_300.25m),
            QuoteAt(DateTimeOffset.UnixEpoch, bid: 5_300.75m),                 // same instant, different price
            QuoteAt(DateTimeOffset.UnixEpoch.AddSeconds(1), bid: 5_300.25m));  // same price, different instant

        await _service.IngestQuotesAsync(_source, Contract, CancellationToken.None);

        drafts.Select(d => d.Id).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task IngestQuotesAsync_ShouldStampTheProducersTraceParent()
    {
        using ActivitySource source = new("test");
        using ActivityListener listener = new()
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        List<EventDraft> drafts = CapturedDrafts();
        SourceStreams(QuoteAt(DateTimeOffset.UnixEpoch));

        using (Activity? activity = source.StartActivity("ingest"))
        {
            await _service.IngestQuotesAsync(_source, Contract, CancellationToken.None);

            // The log is an async boundary: the consumer continues this trace via a span link (engineering §7).
            drafts.Single().TraceParent.Should().Be(activity!.Id);
        }
    }

    [Fact]
    public async Task IngestQuotesAsync_ShouldSurfaceAFailingAppend_RatherThanDropMarketData()
    {
        SourceStreams(QuoteAt(DateTimeOffset.UnixEpoch));
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("log is down"));

        Func<Task> act = () => _service.IngestQuotesAsync(_source, Contract, CancellationToken.None);

        // Silently swallowing means losing market data with a green log line. Fail loud; the host restarts.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task IngestQuotesAsync_ShouldStopCleanly_WhenCancelled()
    {
        using CancellationTokenSource cancellation = new();
        List<EventDraft> drafts = [];
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .Invokes((EventDraft d, CancellationToken _) =>
            {
                drafts.Add(d);
                cancellation.Cancel(); // cancel after the first append
            })
            .ReturnsLazily((EventDraft d, CancellationToken _) => Task.FromResult(
                new EventEnvelope(Guid.NewGuid(), 1, d.Type, d.Source, d.OccurredAt, DateTimeOffset.UnixEpoch, d.Payload, d.TraceParent)));
        SourceStreams(QuoteAt(DateTimeOffset.UnixEpoch), QuoteAt(DateTimeOffset.UnixEpoch.AddSeconds(1)));

        Func<Task> act = () => _service.IngestQuotesAsync(_source, Contract, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        drafts.Should().HaveCount(1); // stopped at the cancellation, did not drain the stream
    }
}
