using System.Runtime.CompilerServices;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue.Fakes;

/// <summary>
/// A <b>data-only</b> provider — it implements the market-data slice and nothing else, the shape R-17 reserves
/// for sources such as Finnhub that carry no accounts and cannot execute.
/// </summary>
internal sealed class FakeQuoteFeed : IMarketDataSource
{
    public VenueId Id { get; } = VenueId.Parse("fakefeed");

    public VenueCapabilities Capabilities { get; } = VenueCapabilities.Of(VenueCapability.Quotes);

    public Task<VenueContractId> ResolveContractAsync(InstrumentId instrument, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(VenueContractId.Create(Id, instrument.Symbol));
    }

    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        VenueContractId contract,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan barSize,
        CancellationToken cancellationToken = default)
    {
        // Historical bars are a paid tier on this provider -- the capability is not granted.
        Capabilities.Require(VenueCapability.HistoricalBars);

        IReadOnlyList<Bar> bars = [];
        return Task.FromResult(bars);
    }

    public async IAsyncEnumerable<Quote> StreamQuotesAsync(
        VenueContractId contract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new Quote(DateTimeOffset.UnixEpoch, new Price(500m), new Price(500.01m), 100, 90);
    }
}
