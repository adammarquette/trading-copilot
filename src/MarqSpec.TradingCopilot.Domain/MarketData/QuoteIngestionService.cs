using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// The event backbone's first producer (ADR-0001, R-1): appends a venue's live quote stream to the append-only
/// log, from which every consumer — indicators, the stop-promotion watcher, the UI — reads independently.
/// </summary>
/// <remarks>
/// <para>
/// The log is the durable store <i>and</i> the replayable stream, so ingestion does exactly one thing: normalise
/// the venue's quote into an envelope and append it. It computes nothing and interprets nothing — projections
/// are consumers' work.
/// </para>
/// <para>
/// Delivery is <b>at-least-once</b> and ADR-0001 asks consumers to dedupe by event id, which is only possible if
/// a replayed quote carries the <i>same</i> id. So the id is <b>derived from the quote itself</b> — venue,
/// contract, timestamp, and the bid/ask it reports — rather than freshly generated: a websocket reconnect that
/// replays an overlapping window produces byte-identical ids and consumers collapse them naturally.
/// </para>
/// </remarks>
public sealed class QuoteIngestionService
{
    /// <summary>The event type every quote is appended under.</summary>
    public const string QuoteEventType = "market.quote";

    private readonly IEventLog _eventLog;

    /// <summary>Creates the ingester over the event log.</summary>
    /// <param name="eventLog">The append-only log (ADR-0001).</param>
    public QuoteIngestionService(IEventLog eventLog)
    {
        _eventLog = eventLog;
    }

    /// <summary>
    /// Streams quotes for a contract and appends each to the log until the stream ends or the caller cancels.
    /// </summary>
    /// <param name="source">The venue's market-data slice.</param>
    /// <param name="contract">The contract to subscribe to.</param>
    /// <param name="cancellationToken">Stops the subscription.</param>
    /// <returns>How many quotes were appended.</returns>
    /// <remarks>
    /// A failing append <b>propagates</b>. Swallowing it would drop market data while the process looks healthy;
    /// surfacing it lets the host restart the subscription, and the log's own replay covers the gap.
    /// </remarks>
    public async Task<int> IngestQuotesAsync(
        IMarketDataSource source,
        VenueContractId contract,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        int appended = 0;

        await foreach (Quote quote in source.StreamQuotesAsync(contract, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _eventLog.AppendAsync(
                new EventDraft(QuoteEventType, source.Id.ToString(), quote.Timestamp, Payload(contract, quote))
                {
                    Id = DeterministicId(source.Id, contract, quote),

                    // The log is an async boundary, so the producer's trace rides the envelope and the consumer
                    // continues it via a span link (engineering §7).
                    TraceParent = EventTracing.CurrentTraceParent(),
                },
                cancellationToken);

            appended++;
        }

        return appended;
    }

    /// <summary>The quote as the envelope's JSON body. The log stores it; it does not interpret it.</summary>
    private static string Payload(VenueContractId contract, Quote quote)
    {
        return JsonSerializer.Serialize(new
        {
            contract = contract.ToString(),
            bid = quote.Bid.Value,
            ask = quote.Ask.Value,
            bidSize = quote.BidSize,
            askSize = quote.AskSize,
            timestamp = quote.Timestamp,
        });
    }

    /// <summary>
    /// A stable id for one market state: same venue, contract, instant, and bid/ask ⇒ same id, so a replayed
    /// quote is dedupable by a consumer (ADR-0001's at-least-once contract).
    /// </summary>
    private static Guid DeterministicId(VenueId venue, VenueContractId contract, Quote quote)
    {
        string identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{venue}|{contract}|{quote.Timestamp.UtcDateTime:O}|{quote.Bid.Value}|{quote.Ask.Value}");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

        return new Guid(hash.AsSpan(0, 16));
    }
}
