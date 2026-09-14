using System.Text.Json;
using FakeItEasy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.MarketData;

/// <summary>
/// The context-price producer (gh#496, of gh#411; ADR-0001, R-1): a cross-asset source's last-trade prints
/// appended to the log under <b>their own event type</b>.
/// </summary>
/// <remarks>
/// The guard that matters most here is the <b>separation</b> (operator decision, 2026-08-02): context prints must
/// never reach the log as <c>market.quote</c>, because that is the stream <c>StopPromotionService</c> and
/// <c>ConditionalFiringService</c> act on to promote stops and fire entries — and a context source has no book, so
/// anything it published there would carry an invented spread. The rest mirror the quote producer's contract:
/// a deterministic id so a reconnect's replay is dedupable, and a failing append surfacing rather than silently
/// dropping data.
/// </remarks>
public class ContextTradeIngestionServiceTests
{
    private readonly IEventLog _log = A.Fake<IEventLog>();
    private readonly IContextMarketDataSource _source = A.Fake<IContextMarketDataSource>();
    private readonly ContextTradeIngestionService _service;

    private static VenueId Venue => VenueId.Parse("finnhub");
    private static VenueContractId Contract => VenueContractId.Create(Venue, "SPY");

    public ContextTradeIngestionServiceTests()
    {
        A.CallTo(() => _source.Id).Returns(Venue);
        StubAppend();
        _service = new ContextTradeIngestionService(_log);
    }

    private void StubAppend() =>
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .ReturnsLazily((EventDraft d, CancellationToken _) => Task.FromResult(
                new EventEnvelope(d.Id ?? Guid.NewGuid(), 1, d.Type, d.Source, d.OccurredAt, DateTimeOffset.UnixEpoch, d.Payload, d.TraceParent)));

    private static ContextTrade TradeAt(DateTimeOffset at, decimal price = 512.34m, decimal? size = 250m) =>
        new(at, new Price(price), size);

    private void SourceStreams(params ContextTrade[] trades) =>
        A.CallTo(() => _source.StreamContextTradesAsync(A<VenueContractId>._, A<CancellationToken>._))
            .Returns(ToAsyncEnumerable(trades));

    private static async IAsyncEnumerable<ContextTrade> ToAsyncEnumerable(IEnumerable<ContextTrade> trades)
    {
        foreach (ContextTrade trade in trades)
        {
            yield return trade;
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
    public async Task IngestContextTradesAsync_ShouldAppendUnderItsOwnEventType_NeverTheExecutableQuoteStream()
    {
        // THE isolation this producer exists for. If a context print ever landed as `market.quote`, the
        // stop-promotion and conditional-firing watchers would read it as an executable price — and it carries no
        // book at all. Asserting the exact type (and explicitly NOT the quote type) is what keeps that structural.
        List<EventDraft> drafts = CapturedDrafts();
        SourceStreams(TradeAt(DateTimeOffset.UnixEpoch));

        await _service.IngestContextTradesAsync(_source, Contract, CancellationToken.None);

        drafts.Should().ContainSingle();
        drafts[0].Type.Should().Be(ContextTradeIngestionService.ContextTradeEventType);
        drafts[0].Type.Should().NotBe(QuoteIngestionService.QuoteEventType,
            "a context print must never be readable by a consumer of the executable quote stream");
    }

    [Fact]
    public async Task IngestContextTradesAsync_ShouldTagSourceAndContract_SoAContextPrintCannotBeReadAsAFuturesQuote()
    {
        List<EventDraft> drafts = CapturedDrafts();
        SourceStreams(TradeAt(DateTimeOffset.UnixEpoch, price: 512.34m, size: 250m));

        await _service.IngestContextTradesAsync(_source, Contract, CancellationToken.None);

        drafts[0].Source.Should().Be("finnhub", "provenance is what distinguishes this feed in the log");

        using JsonDocument payload = JsonDocument.Parse(drafts[0].Payload);
        payload.RootElement.GetProperty("contract").GetString().Should().Be(Contract.ToString(),
            "the venue-qualified contract is the tagging that stops Finnhub SPY and ProjectX ES conflating");
        payload.RootElement.GetProperty("price").GetDecimal().Should().Be(512.34m);
        payload.RootElement.GetProperty("size").GetDecimal().Should().Be(250m);
    }

    [Fact]
    public async Task IngestContextTradesAsync_ShouldCarryNoBidOrAsk_BecauseAContextSourceHasNoBook()
    {
        // The payload must not grow a bid/ask shape later "for symmetry" — that is exactly how an invented spread
        // would arrive by the back door.
        List<EventDraft> drafts = CapturedDrafts();
        SourceStreams(TradeAt(DateTimeOffset.UnixEpoch));

        await _service.IngestContextTradesAsync(_source, Contract, CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(drafts[0].Payload);
        payload.RootElement.TryGetProperty("bid", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("ask", out _).Should().BeFalse();
    }

    [Fact]
    public async Task IngestContextTradesAsync_ShouldMintTheSameId_WhenAReconnectReplaysThePrint()
    {
        // ADR-0001 is at-least-once and asks consumers to dedupe by id, which only works if a replayed print
        // carries the SAME id — so it is derived from the print, never freshly generated.
        List<EventDraft> drafts = CapturedDrafts();
        ContextTrade print = TradeAt(DateTimeOffset.UnixEpoch);
        SourceStreams(print, print);

        await _service.IngestContextTradesAsync(_source, Contract, CancellationToken.None);

        drafts.Should().HaveCount(2);
        drafts[0].Id!.Value.Should().Be(drafts[1].Id!.Value);
    }

    [Fact]
    public async Task IngestContextTradesAsync_ShouldMintDifferentIds_ForPrintsThatDifferOnlyInPrice()
    {
        // The control on the case above: an id that collapsed everything would also make the replay test pass.
        List<EventDraft> drafts = CapturedDrafts();
        SourceStreams(
            TradeAt(DateTimeOffset.UnixEpoch, price: 512.34m),
            TradeAt(DateTimeOffset.UnixEpoch, price: 512.35m));

        await _service.IngestContextTradesAsync(_source, Contract, CancellationToken.None);

        drafts[0].Id!.Value.Should().NotBe(drafts[1].Id!.Value);
    }

    [Fact]
    public async Task IngestContextTradesAsync_ShouldPropagateAFailingAppend_RatherThanDropDataWhileLookingHealthy()
    {
        SourceStreams(TradeAt(DateTimeOffset.UnixEpoch));
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("log unavailable (test)"));

        Func<Task> ingest = () => _service.IngestContextTradesAsync(_source, Contract, CancellationToken.None);

        await ingest.Should().ThrowAsync<InvalidOperationException>(
            "surfacing it lets the supervisor restart the subscription; swallowing it loses data silently");
    }

    [Fact]
    public async Task IngestContextTradesAsync_ShouldReturnHowManyPrintsWereAppended()
    {
        SourceStreams(TradeAt(DateTimeOffset.UnixEpoch), TradeAt(DateTimeOffset.UnixEpoch.AddSeconds(1)));

        int appended = await _service.IngestContextTradesAsync(_source, Contract, CancellationToken.None);

        appended.Should().Be(2);
    }
}
