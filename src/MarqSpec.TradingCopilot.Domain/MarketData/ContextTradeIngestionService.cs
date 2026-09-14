using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// The event backbone's <b>context-price</b> producer (gh#496, of gh#411; ADR-0001, R-1): appends a cross-asset
/// source's last-trade prints to the append-only log under their own event type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not <see cref="QuoteIngestionService"/> with a different source.</b> That producer's stream is
/// <c>market.quote</c> — the one <c>StopPromotionService</c> and <c>ConditionalFiringService</c> consume to
/// promote stops and fire entries. A context provider publishes no book (Finnhub's free tier carries prints only),
/// so anything it wrote there would have to invent a bid and an ask. Giving context prints their own event type
/// makes the separation structural: an executable-price consumer filters on the quote type and cannot read one of
/// these even by accident (operator decision, 2026-08-02; engineering §9).
/// </para>
/// <para>
/// Otherwise the contract is the quote producer's, deliberately: normalise and append, compute nothing, and derive
/// the event id from the print itself so a reconnect that replays an overlapping window produces byte-identical
/// ids and consumers collapse them naturally (ADR-0001's at-least-once contract).
/// </para>
/// </remarks>
public sealed class ContextTradeIngestionService
{
    /// <summary>
    /// The event type every context print is appended under — deliberately <b>not</b>
    /// <see cref="QuoteIngestionService.QuoteEventType"/>.
    /// </summary>
    public const string ContextTradeEventType = "market.context-trade";

    private readonly IEventLog _eventLog;

    /// <summary>Creates the ingester over the event log.</summary>
    /// <param name="eventLog">The append-only log (ADR-0001).</param>
    public ContextTradeIngestionService(IEventLog eventLog)
    {
        _eventLog = eventLog;
    }

    /// <summary>
    /// Streams context prints for a contract and appends each to the log until the stream ends or the caller
    /// cancels.
    /// </summary>
    /// <param name="source">The cross-asset context source.</param>
    /// <param name="contract">The contract to subscribe to.</param>
    /// <param name="cancellationToken">Stops the subscription.</param>
    /// <returns>How many prints were appended.</returns>
    /// <remarks>
    /// A failing append <b>propagates</b>, as on the quote path: swallowing it would drop data while the process
    /// looks healthy, and surfacing it lets the supervisor restart the subscription.
    /// </remarks>
    public async Task<int> IngestContextTradesAsync(
        IContextMarketDataSource source,
        VenueContractId contract,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        int appended = 0;

        await foreach (ContextTrade trade in source.StreamContextTradesAsync(contract, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _eventLog.AppendAsync(
                new EventDraft(ContextTradeEventType, source.Id.ToString(), trade.Timestamp, Payload(contract, trade))
                {
                    Id = DeterministicId(source.Id, contract, trade),

                    // The log is an async boundary, so the producer's trace rides the envelope and the consumer
                    // continues it via a span link (engineering §7).
                    TraceParent = EventTracing.CurrentTraceParent(),
                },
                cancellationToken);

            appended++;
        }

        return appended;
    }

    /// <summary>
    /// The print as the envelope's JSON body — price and size, and deliberately <b>no bid/ask</b>: there is no book
    /// behind a context print, and a payload shaped like a quote's is how an invented spread would arrive by the
    /// back door.
    /// </summary>
    private static string Payload(VenueContractId contract, ContextTrade trade)
    {
        return JsonSerializer.Serialize(new
        {
            contract = contract.ToString(),
            price = trade.Price.Value,
            size = trade.Size,
            timestamp = trade.Timestamp,
        });
    }

    /// <summary>
    /// A stable id for one print: same venue, contract, instant, price and size ⇒ same id, so a replayed print is
    /// dedupable by a consumer (ADR-0001's at-least-once contract).
    /// </summary>
    private static Guid DeterministicId(VenueId venue, VenueContractId contract, ContextTrade trade)
    {
        string identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{venue}|{contract}|{trade.Timestamp.UtcDateTime:O}|{trade.Price.Value}|{trade.Size}");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

        return new Guid(hash.AsSpan(0, 16));
    }
}
