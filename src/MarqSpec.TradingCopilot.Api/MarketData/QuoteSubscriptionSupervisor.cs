using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Keeps one contract's quote subscription alive (ADR-0013, R-1): a venue stream is long-lived and
/// <b>drops</b> — reconnects, session resets, transient faults — so the supervisor re-subscribes after a drop,
/// backing off between attempts, until it is told to stop.
/// </summary>
/// <remarks>
/// A stream ending or throwing is a <b>drop</b>: not fatal, reconnect. Cancellation is a <b>stop</b>: exit
/// immediately, no backoff. Data lost across the reconnect gap is recovered by the log's own replay (ADR-0001),
/// so the supervisor's only job is to get the subscription back — it never buffers.
/// </remarks>
public sealed class QuoteSubscriptionSupervisor
{
    private readonly QuoteIngestionService _ingestion;
    private readonly TimeSpan _reconnectDelay;
    private readonly ILogger<QuoteSubscriptionSupervisor> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates a supervisor.</summary>
    /// <param name="ingestion">The producer that appends the stream to the log.</param>
    /// <param name="reconnectDelay">The backoff between a drop and the next subscription attempt.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="delay">
    /// The delay primitive — defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>; injectable so
    /// the loop's timing is unit-testable without waiting.
    /// </param>
    public QuoteSubscriptionSupervisor(
        QuoteIngestionService ingestion,
        TimeSpan reconnectDelay,
        ILogger<QuoteSubscriptionSupervisor> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _ingestion = ingestion;
        _reconnectDelay = reconnectDelay;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Runs the subscription until <paramref name="cancellationToken"/> is signalled, re-subscribing after each
    /// drop.
    /// </summary>
    /// <param name="source">The venue's market-data slice.</param>
    /// <param name="contract">The contract to keep subscribed.</param>
    /// <param name="cancellationToken">Stops the supervisor.</param>
    /// <exception cref="OperationCanceledException">Cancellation was requested — a clean stop.</exception>
    public async Task RunAsync(IMarketDataSource source, VenueContractId contract, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _ingestion.IngestQuotesAsync(source, contract, cancellationToken);

                // The stream ended without error -- the venue closed the session. Reconnect.
                _logger.LogInformation(
                    "Quote stream for {Contract} on {Venue} ended; re-subscribing.", contract, source.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // a stop, not a drop -- no backoff, propagate
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogWarning(
                    error, "Quote stream for {Contract} on {Venue} dropped; re-subscribing after {Delay}.",
                    contract, source.Id, _reconnectDelay);
            }

            // A stop requested during the stream -- even one that then ended cleanly -- is a stop, not a drop:
            // exit before backing off, so cancellation never costs a reconnect delay.
            cancellationToken.ThrowIfCancellationRequested();

            // Back off before re-subscribing so a downed venue is never hot-looped. The delay is cancellable,
            // so a stop during the backoff still exits promptly.
            await _delay(_reconnectDelay, cancellationToken);
        }
    }
}
