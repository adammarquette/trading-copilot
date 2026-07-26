using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The conditional-order firing watcher (ADR-0007, gh#198): the event log's <b>second consumer</b>. It reads
/// <c>market.quote</c> events from its own cursor and fires / cancels / expires the pending conditional orders
/// each quote resolves.
/// </summary>
/// <remarks>
/// Its own consumer group tracks its own cursor (ADR-0001), independent of the stop-promotion watcher. It only
/// acts when pending conditionals exist, so it is harmless to run with none. A fresh DI scope per pass keeps
/// host shutdown clean (gh#169); the firing service reads across the R-20 boundary deliberately (see its note).
/// The clock is read <b>here</b>, at the boundary, and passed in — the domain's cancel/expire decision stays pure.
/// </remarks>
public sealed class ConditionalOrderHost : BackgroundService
{
    /// <summary>The consumer group name whose cursor this watcher advances.</summary>
    public const string ConsumerGroup = "conditional-order";

    private const int BatchSize = 256;

    /// <summary>How long to wait when caught up before polling the log again (until LISTEN/NOTIFY lands).</summary>
    private static TimeSpan IdlePoll { get; } = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _services;
    private readonly ILogger<ConditionalOrderHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="logger">The logger.</param>
    public ConditionalOrderHost(IServiceProvider services, ILogger<ConditionalOrderHost> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long? cursor = null; // read from the log on the first pass, then carried in memory
        bool announced = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A FRESH scope per pass: the event log and DbContext are scoped, so holding one for the app's
                // lifetime would leak tracked entities and, on host teardown, leave this loop polling disposed
                // services. Per-pass scoping keeps shutdown clean (gh#169).
                bool caughtUp;
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
                    ConditionalFiringService firing = scope.ServiceProvider.GetRequiredService<ConditionalFiringService>();

                    cursor ??= await log.GetCursorAsync(ConsumerGroup, stoppingToken) ?? 0;
                    if (!announced)
                    {
                        _logger.LogInformation("Conditional-order firing watcher started at sequence {Cursor}.", cursor.Value);
                        announced = true;
                    }

                    EventPage page = await log.ReadAfterAsync(cursor.Value, BatchSize, stoppingToken);

                    if (page.Gap is not null)
                    {
                        // The log is lossy past its window (ADR-0001, gh#227). A quote that would have TRIGGERED a
                        // pending conditional -- or drifted it past its cancel band -- may have been dropped
                        // unseen. Nothing fires retroactively: a conditional stays Pending and is re-decided on
                        // the next quote against fresh truth, and its cancel-if / expiry still stand, so the
                        // failure direction is "did not fire" rather than "fired on stale grounds" (ADR-0013).
                        _logger.LogError(
                            "Event-log retention gap on {ConsumerGroup}: cursor {Cursor} fell behind the window; "
                            + "the oldest surviving sequence is {OldestAvailable}. Quotes in between were dropped "
                            + "and are NOT recoverable from this log. Pending conditionals were NOT evaluated "
                            + "against them — they stay Pending and re-decide on the next quote.",
                            ConsumerGroup,
                            page.Gap.RequestedAfterSequence,
                            page.Gap.OldestAvailableSequence);
                    }

                    IReadOnlyList<EventEnvelope> batch = page.Events;
                    caughtUp = batch.Count == 0;

                    if (!caughtUp)
                    {
                        foreach (EventEnvelope evt in batch)
                        {
                            if (StopPromotionService.TryDecodeQuote(evt, out StopPromotionService.DecodedQuote quote))
                            {
                                await firing.ProcessQuoteAsync(
                                    quote.ContractKey, quote.Bid, quote.Ask, DateTimeOffset.UtcNow, stoppingToken);
                            }

                            cursor = evt.Sequence;
                        }

                        // Commit once per batch: at-least-once redelivery on restart is safe because firing is
                        // idempotent (a resolved conditional never re-fires, ADR-0001).
                        await log.CommitCursorAsync(ConsumerGroup, cursor.Value, stoppingToken);
                    }
                }

                if (caughtUp)
                {
                    // Caught up: wait for new events rather than hot-looping the log. LISTEN/NOTIFY replaces
                    // this poll when the push wake-up lands (ADR-0001).
                    await Task.Delay(IdlePoll, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop -- the host is shutting down
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down (host shutdown) -- exit cleanly (gh#169)
            }
            catch (Exception error)
            {
                // A transient log/DB/venue fault must not kill the watcher: log, back off, retry. The cursor is
                // unchanged, so nothing is skipped.
                _logger.LogWarning(error, "Conditional-order firing pass failed; retrying after {Delay}.", IdlePoll);
                await Task.Delay(IdlePoll, stoppingToken);
            }
        }
    }
}
