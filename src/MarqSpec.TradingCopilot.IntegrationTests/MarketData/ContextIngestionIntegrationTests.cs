using System.Text.Json;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.MarketData;

/// <summary>
/// Independent QA coverage for the cross-asset <b>context</b> price path (gh#608, of gh#496 / gh#411; R-1, R-17,
/// ADR-0001). Written from the issue and the operator decision recorded on gh#496, not from the implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property under test.</b> A context source publishes last-trade prints and no book, so its prints must
/// reach the event log under <b>their own type</b> — never <c>market.quote</c>, the stream
/// <c>StopPromotionService</c> and <c>ConditionalFiringService</c> act on. Anything that leaked a context print
/// into that stream would let an equity print promote a stop or fire an entry on a futures contract, off a price
/// with no book behind it.
/// </para>
/// <para>
/// <b>Why the container tier.</b> The unit suite covers the adapter's mapping against fakes. What it cannot
/// witness is that the append reaches the <i>real</i> <c>Events</c> table under the right type, and that the
/// deterministic id survives a round trip through Postgres — that is a storage question, so it runs against the
/// shipped migrations.
/// </para>
/// <para>
/// <b>The negative half is the point.</b> Every case here asserts what did <i>not</i> happen as well as what did:
/// a passing "the print landed" tells you nothing on its own, because the defect this guards is a print landing
/// in the <i>wrong</i> stream — which also lands.
/// </para>
/// <para>
/// <b>Scope: cases 1 and 4 of gh#608.</b> Cases 2 (execution watchers blind, with the live
/// <c>StopPromotionHost</c> / <c>ConditionalOrderHost</c>), 3 (a context outage does not disturb tradeable
/// ingestion) and 5 (capability refusal at the seam) need the live-host harness and are tracked separately —
/// see the PR. Nothing here should be read as covering them.
/// </para>
/// </remarks>
public class ContextIngestionIntegrationTests : IClassFixture<PostgresApiFactory>
{
    /// <summary>
    /// The context source's own contract — an equity symbol, deliberately not a futures contract. Each test uses
    /// its <b>own</b> symbol: the fixture is one container shared by the whole class, so a shared key would let one
    /// case's appends satisfy another's count assertions.
    /// </summary>
    private const string TypeCaseSymbol = "SPY";

    /// <summary>The deterministic-id case's own symbol, for the same isolation reason.</summary>
    private const string IdCaseSymbol = "QQQ";

    private readonly PostgresApiFactory _factory;

    public ContextIngestionIntegrationTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ContextPrint_ShouldLandUnderItsOwnEventType_AndNeverAsAQuote()
    {
        // gh#608 case 1. The whole cross-asset design rests on the type split (operator decision, 2026-08-02):
        // context prints carry no bid/ask, so a consumer of executable prices must be unable to read one even by
        // accident. Asserting only "a row appeared" would pass on the defect -- a print written as market.quote
        // also appears -- so the negative half below is what actually guards it.
        StubContextSource source = new(new ContextTrade(DateTimeOffset.UtcNow, new Price(512.34m), Size: 100m));
        VenueContractId contract = VenueContractId.Create(source.Id, TypeCaseSymbol);

        int appended = await IngestAsync(source, contract);

        appended.Should().Be(1, "the stub streamed exactly one print");

        IReadOnlyList<EventRow> rows = await ReadEventsAsync(TypeCaseSymbol);

        rows.Should().ContainSingle("one print was streamed").Which
            .Type.Should().Be(
                ContextTradeIngestionService.ContextTradeEventType,
                "a context print must reach the log under its own type (gh#496)");

        rows.Should().NotContain(
            row => row.Type == QuoteIngestionService.QuoteEventType,
            "a context print in the quote stream would let an equity print promote a stop or fire an entry off a "
            + "price with no book behind it -- the failure the type split exists to make structurally impossible");

        // The payload carries no book either. A quote-shaped body is how an invented spread would arrive by the
        // back door even under the correct type, so the absence is asserted rather than assumed.
        using JsonDocument payload = JsonDocument.Parse(rows[0].Payload);
        payload.RootElement.TryGetProperty("bid", out _).Should().BeFalse("a context print has no book");
        payload.RootElement.TryGetProperty("ask", out _).Should().BeFalse("a context print has no book");
        payload.RootElement.GetProperty("price").GetDecimal().Should().Be(512.34m);
    }

    [Fact]
    public async Task ReplayedContextPrint_ShouldKeepTheSameId_AndADifferentPriceShouldNotCollide()
    {
        // gh#608 case 4. ADR-0001's at-least-once contract: a reconnect that replays an overlapping window must
        // produce byte-identical ids so consumers collapse the duplicates naturally. Both halves matter -- an id
        // derived from too little (say, venue + timestamp alone) would dedupe two genuinely different prints and
        // silently lose one, so the second assertion is the one that catches an over-eager key.
        DateTimeOffset at = DateTimeOffset.UtcNow;
        StubContextSource source = new(new ContextTrade(at, new Price(512.34m), Size: 100m));
        VenueContractId contract = VenueContractId.Create(source.Id, IdCaseSymbol);

        await IngestAsync(source, contract);
        Guid first = (await ReadEventsAsync(IdCaseSymbol)).Single().Id;

        // The same print again -- the reconnect-replay case.
        await IngestAsync(new StubContextSource(new ContextTrade(at, new Price(512.34m), Size: 100m)), contract);

        // ADR-0001 is at-least-once and DEDUPABLE, not dedup-ON-WRITE: the log appends the replay as its own row
        // (Sequence is the key, Id is not) and the identical Id is what lets a consumer collapse the pair. Asserting
        // one row here would be asserting a guarantee the backbone deliberately does not make, and would go red on
        // correct behaviour.
        IReadOnlyList<EventRow> afterReplay = await ReadEventsAsync(IdCaseSymbol);
        afterReplay.Should().HaveCount(2, "the replay is appended -- the log does not dedupe on write");
        afterReplay.Select(row => row.Id).Distinct().Should().ContainSingle(
            "a replayed print must derive a BYTE-IDENTICAL id, which is what makes it dedupable downstream (ADR-0001)");
        afterReplay[1].Id.Should().Be(first, "same venue, contract, instant, price and size => same id");

        // Same instant and contract, different price -- a genuinely different print that must NOT collapse.
        await IngestAsync(new StubContextSource(new ContextTrade(at, new Price(512.35m), Size: 100m)), contract);

        IReadOnlyList<EventRow> afterSecondPrice = await ReadEventsAsync(IdCaseSymbol);
        afterSecondPrice.Should().HaveCount(3, "three prints were appended in total");
        afterSecondPrice.Select(row => row.Id).Distinct().Should().HaveCount(
            2,
            "two prints differing only in price are DIFFERENT facts and must derive different ids -- an id keyed on "
            + "too little (venue + contract + instant, say) would collapse them and silently drop a real print");
    }

    /// <summary>Runs the producer against the real event log in its own scope, as the host does per subscription.</summary>
    private async Task<int> IngestAsync(IContextMarketDataSource source, VenueContractId contract)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ContextTradeIngestionService ingestion =
            scope.ServiceProvider.GetRequiredService<ContextTradeIngestionService>();

        return await ingestion.IngestContextTradesAsync(source, contract, CancellationToken.None);
    }

    /// <summary>
    /// Reads the appended envelopes back from the real table, filtered to the contract under test so a parallel
    /// suite's rows cannot satisfy an assertion here.
    /// </summary>
    private async Task<IReadOnlyList<EventRow>> ReadEventsAsync(string contractKey)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

        // Filter by TYPE in SQL and by contract in memory: Payload is `jsonb`, so a `.Contains` predicate
        // translates to a string LIKE that Postgres refuses on that column type. Both market-data types are read
        // rather than just the context one -- the negative assertions need to see a quote row if one wrongly
        // appeared, and a query that could not return one could not fail on the defect.
        List<EventRow> rows = await database.Events
            .Where(row => row.Type == ContextTradeIngestionService.ContextTradeEventType
                || row.Type == QuoteIngestionService.QuoteEventType)
            .OrderBy(row => row.Sequence)
            .Select(row => new EventRow(row.Id, row.Type, row.Payload))
            .ToListAsync();

        return rows.Where(row => row.Payload.Contains(contractKey, StringComparison.Ordinal)).ToList();
    }

    private sealed record EventRow(Guid Id, string Type, string Payload);

    /// <summary>
    /// A context source that streams exactly the prints it is handed. It <b>feeds inputs and computes nothing</b>
    /// (QA contract §1): it never reports the event type, the id, or whether the append happened, so every
    /// assertion above is read back from Postgres rather than from the double.
    /// </summary>
    private sealed class StubContextSource : IContextMarketDataSource
    {
        private readonly ContextTrade[] _prints;

        public StubContextSource(params ContextTrade[] prints)
        {
            _prints = prints;
        }

        public VenueId Id { get; } = VenueId.Parse("finnhub");

        public VenueCapabilities Capabilities => VenueCapabilities.None;

        public int AdapterLogicVersion => 1;

        public Task<ResolvedContract> ResolveContractAsync(
            InstrumentId instrument,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("gh#608's cases drive the stream directly with an already-resolved contract.");

        public async IAsyncEnumerable<ContextTrade> StreamContextTradesAsync(
            VenueContractId contract,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (ContextTrade print in _prints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return print;
                await Task.Yield();
            }
        }
    }
}
