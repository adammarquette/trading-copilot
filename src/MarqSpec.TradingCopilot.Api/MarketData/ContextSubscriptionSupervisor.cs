using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Keeps one context symbol's subscription alive (gh#496, R-1) — the same discipline as
/// <see cref="QuoteSubscriptionSupervisor"/>: a stream that <b>ends or throws</b> is a <b>drop</b> (log, back off,
/// re-subscribe), and cancellation is a <b>stop</b> (exit immediately, no backoff).
/// </summary>
/// <remarks>
/// A sibling class rather than a generic one, matching how <c>AccountEventSubscriptionSupervisor</c> already
/// mirrors the quote supervisor: the shapes are the same but the streams are not, and making the tradeable quote
/// path generic to accommodate a context feed would put a data-only concern inside a safety-critical loop.
/// </remarks>
public sealed class ContextSubscriptionSupervisor
{
    private readonly ContextTradeIngestionService _ingestion;
    private readonly TimeSpan _reconnectDelay;
    private readonly ILogger<ContextSubscriptionSupervisor> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates a supervisor.</summary>
    /// <param name="ingestion">The producer that appends the stream to the log.</param>
    /// <param name="reconnectDelay">The backoff between a drop and the next subscription attempt.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="delay">The delay primitive — injectable so the loop's timing is unit-testable without waiting.</param>
    public ContextSubscriptionSupervisor(
        ContextTradeIngestionService ingestion,
        TimeSpan reconnectDelay,
        ILogger<ContextSubscriptionSupervisor> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _ingestion = ingestion;
        _reconnectDelay = reconnectDelay;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Runs the subscription until <paramref name="cancellationToken"/> is signalled.</summary>
    /// <param name="source">The context source.</param>
    /// <param name="contract">The contract to keep subscribed.</param>
    /// <param name="cancellationToken">Stops the supervisor.</param>
    /// <exception cref="OperationCanceledException">Cancellation was requested — a clean stop.</exception>
    public async Task RunAsync(
        IContextMarketDataSource source,
        VenueContractId contract,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _ingestion.IngestContextTradesAsync(source, contract, cancellationToken);

                _logger.LogInformation(
                    "Context stream for {Contract} on {Source} ended; re-subscribing.", contract, source.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // a stop, not a drop -- no backoff, propagate
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Context is cross-asset colour, never a trading dependency: a provider that is down, rate-limited,
                // or over its free-tier cap must be logged and retried, never escalated.
                _logger.LogWarning(
                    error, "Context stream for {Contract} on {Source} dropped; re-subscribing after {Delay}.",
                    contract, source.Id, _reconnectDelay);
            }

            cancellationToken.ThrowIfCancellationRequested();

            await _delay(_reconnectDelay, cancellationToken);
        }
    }
}
