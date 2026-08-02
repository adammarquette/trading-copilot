using FakeItEasy;
using MarqSpec.Client.Finnhub;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Finnhub;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Finnhub;

/// <summary>
/// The Finnhub → <see cref="IContextMarketDataSource"/> adapter (gh#496, of gh#411). It translates Finnhub's raw
/// <see cref="FinnhubTrade"/> into the venue-neutral <see cref="ContextTrade"/> the context ingestion path
/// consumes — the mapping and the capability boundary are the whole job, so that is what these guard, against a
/// faked stream (no network).
/// </summary>
public class FinnhubMarketDataSourceTests
{
    private const string Symbol = "SPY";

    private readonly IFinnhubQuoteStream _stream = A.Fake<IFinnhubQuoteStream>();

    private FinnhubMarketDataSource Source() => new(_stream);

    private static VenueContractId Contract(string symbol = Symbol) =>
        VenueContractId.Create(VenueId.Parse("finnhub"), symbol);

    private static FinnhubTrade Trade(
        string symbol = Symbol,
        decimal price = 512.34m,
        decimal volume = 250m,
        long epochMs = 1_700_000_000_000) =>
        new(symbol, price, volume, DateTimeOffset.FromUnixTimeMilliseconds(epochMs));

    private void StreamYields(params FinnhubTrade[] trades) =>
        A.CallTo(() => _stream.ReadAsync(A<CancellationToken>._)).Returns(ToAsync(trades));

    private static async IAsyncEnumerable<FinnhubTrade> ToAsync(IEnumerable<FinnhubTrade> trades)
    {
        foreach (FinnhubTrade trade in trades)
        {
            yield return trade;
        }

        await Task.CompletedTask;
    }

    private async Task<List<ContextTrade>> DrainAsync(VenueContractId? contract = null)
    {
        List<ContextTrade> drained = [];
        await foreach (ContextTrade trade in Source().StreamContextTradesAsync(contract ?? Contract(), CancellationToken.None))
        {
            drained.Add(trade);
        }

        return drained;
    }

    [Fact]
    public void Id_ShouldBeFinnhub_SoAContextPrintTagsTheRightSource()
    {
        // Provenance is recorded from source.Id, and it is what keeps a Finnhub SPY print distinguishable from a
        // ProjectX ES quote once both are in the log.
        Source().Id.ToString().Should().Be("finnhub");
    }

    [Fact]
    public void Capabilities_ShouldGrantContextTrades_ButNeverQuotes()
    {
        // THE capability boundary this adapter exists to hold (gh#496, operator decision 2026-08-02). Finnhub's
        // free tier publishes no book, so granting Quotes would advertise executable top-of-book prices this
        // source cannot produce without inventing a zero spread — in the one stream the execution watchers act on.
        VenueCapabilities capabilities = Source().Capabilities;

        capabilities.Supports(VenueCapability.ContextTrades).Should().BeTrue();
        capabilities.Supports(VenueCapability.Quotes).Should().BeFalse(
            "a context source has no book; claiming Quotes would mean publishing a spread nobody observed");
    }

    [Fact]
    public void Capabilities_ShouldNotGrantHistoricalBars_BecauseFreeTierCandlesArePaid()
    {
        // Refusing at the seam beats returning an empty series: a caller that asked for history and got nothing
        // cannot tell "no bars exist" from "this source will never have any" (R-17).
        Source().Capabilities.Supports(VenueCapability.HistoricalBars).Should().BeFalse();
    }

    [Fact]
    public void Capabilities_ShouldGrantNothingExecutionShaped_SoADataOnlySourceCanNeverBeTraded()
    {
        VenueCapabilities capabilities = Source().Capabilities;

        capabilities.Supports(VenueCapability.BracketOrders).Should().BeFalse();
        capabilities.Supports(VenueCapability.ClosePosition).Should().BeFalse();
        capabilities.Supports(VenueCapability.AccountStreaming).Should().BeFalse();
        capabilities.Supports(VenueCapability.ModifyOrder).Should().BeFalse();
    }

    [Fact]
    public async Task ResolveContract_ShouldMapTheInstrumentToAFinnhubContract_PairedWithWhatItResolvedFor()
    {
        ResolvedContract resolved = await Source().ResolveContractAsync(InstrumentId.Parse(Symbol), CancellationToken.None);

        resolved.Contract.Should().Be(Contract());
        resolved.Instrument.Symbol.Should().Be(Symbol,
            "the pairing is what lets a caller detect a context symbol being used as a tradeable contract");
    }

    [Fact]
    public async Task StreamContextTrades_ShouldSubscribeTheSymbol_BeforeAnythingCanArrive()
    {
        StreamYields(Trade());

        await DrainAsync();

        A.CallTo(() => _stream.SubscribeAsync(Symbol, A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task StreamContextTrades_ShouldMapPriceSizeAndTimestamp_FromThePrint()
    {
        StreamYields(Trade(price: 512.34m, volume: 250m, epochMs: 1_700_000_000_000));

        List<ContextTrade> drained = await DrainAsync();

        drained.Should().ContainSingle();
        drained[0].Price.Value.Should().Be(512.34m);
        drained[0].Size.Should().Be(250m);
        drained[0].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
    }

    [Fact]
    public async Task StreamContextTrades_ShouldYieldOnlyTheRequestedSymbol_WhenTheSocketIsMultiplexed()
    {
        // One websocket carries every subscribed symbol, so without a per-contract filter a caller asking for SPY
        // would be fed QQQ's prints as SPY's — two instruments conflated at the seam, which is the exact failure
        // the venue/source tagging exists to prevent. The control print is what makes this non-vacuous.
        StreamYields(
            Trade(symbol: "QQQ", price: 444.00m),
            Trade(symbol: Symbol, price: 512.34m),
            Trade(symbol: "QQQ", price: 445.00m));

        List<ContextTrade> drained = await DrainAsync(Contract(Symbol));

        drained.Should().ContainSingle("only the requested symbol's prints belong to this subscription");
        drained[0].Price.Value.Should().Be(512.34m);
    }

    [Fact]
    public async Task StreamContextTrades_ShouldSurfaceTheFreeTierCap_RatherThanStreamingSilentlyNothing()
    {
        // Finnhub ignores an over-cap subscribe, so the symbol would simply never tick and the gap would be found
        // later by its absence. The client turns that into a refusal; the adapter must let it out (an acceptance
        // criterion of gh#496: exceeding the cap fails loudly at the seam).
        A.CallTo(() => _stream.SubscribeAsync(A<string>._, A<CancellationToken>._))
            .Throws(new FinnhubSubscriptionLimitException(50, Symbol));

        Func<Task> drain = () => DrainAsync();

        await drain.Should().ThrowAsync<FinnhubSubscriptionLimitException>();
    }
}
