using MarqSpec.Client.Finnhub;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Integration.Finnhub;

/// <summary>
/// The Finnhub adapter for the R-17 <see cref="IContextMarketDataSource"/> seam (gh#496, of gh#411): a data-only
/// <b>cross-asset context</b> price source that translates Finnhub's raw <see cref="FinnhubTrade"/> into the
/// venue-neutral <see cref="ContextTrade"/>. It declares and requires <see cref="VenueCapability.ContextTrades"/>;
/// it holds no account, executes nothing, and publishes no book.
/// </summary>
/// <remarks>
/// <para>
/// <b>It grants <see cref="VenueCapability.ContextTrades"/> and not <see cref="VenueCapability.Quotes"/></b>,
/// because Finnhub's free tier carries neither side of the top of book — only prints. Claiming <c>Quotes</c> would
/// mean publishing <c>Bid == Ask</c>, a spread nobody observed, into the stream the execution watchers act on.
/// It also does <b>not</b> grant <see cref="VenueCapability.HistoricalBars"/>: free-tier candles are a paid
/// feature, so a caller asking this source for history is refused at the seam rather than handed an empty series.
/// </para>
/// <para>
/// <b>The stream is multiplexed; a subscription is not.</b> One websocket carries every subscribed symbol, so a
/// caller asking for <c>SPY</c> must never be handed <c>QQQ</c>'s prints — the per-contract filter below is what
/// keeps two context symbols from conflating, the same discipline that keeps a context symbol away from a
/// tradeable contract.
/// </para>
/// </remarks>
public sealed class FinnhubMarketDataSource : IContextMarketDataSource
{
    private readonly IFinnhubQuoteStream _stream;

    /// <summary>Creates the adapter.</summary>
    /// <param name="stream">The Finnhub websocket trade feed.</param>
    public FinnhubMarketDataSource(IFinnhubQuoteStream stream) => _stream = stream;

    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("finnhub");

    /// <inheritdoc />
    public VenueCapabilities Capabilities => VenueCapabilities.Of(VenueCapability.ContextTrades);

    /// <inheritdoc />
    public int AdapterLogicVersion => 1;

    /// <inheritdoc />
    /// <remarks>
    /// Finnhub addresses equities by their plain ticker, so the contract handle <i>is</i> the symbol — but it is
    /// still minted through <see cref="VenueContractId"/> with this source's own <see cref="Id"/>, so a Finnhub
    /// <c>SPY</c> and a ProjectX contract can never compare equal however similar their keys look.
    /// </remarks>
    public Task<ResolvedContract> ResolveContractAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken = default)
    {
        Capabilities.Require(VenueCapability.ContextTrades);

        return Task.FromResult(new ResolvedContract(VenueContractId.Create(Id, instrument.Symbol), instrument));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ContextTrade> StreamContextTradesAsync(
        VenueContractId contract,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Capabilities.Require(VenueCapability.ContextTrades);

        // Subscribe before reading, and let a refusal OUT. The free-tier cap is enforced at this call precisely
        // because Finnhub ignores an over-cap subscribe: swallowing it here would turn a loud failure back into
        // the silent never-ticks gap the client's exception exists to prevent (gh#496).
        await _stream.SubscribeAsync(contract.Key, cancellationToken);

        await foreach (FinnhubTrade trade in _stream.ReadAsync(cancellationToken))
        {
            // One socket carries every subscribed symbol, so this filter is what makes a subscription mean one
            // instrument. Without it a SPY caller is fed QQQ's prints as SPY's.
            if (!string.Equals(trade.Symbol, contract.Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new ContextTrade(trade.TimestampUtc, new Price(trade.Price), trade.Volume);
        }
    }
}
