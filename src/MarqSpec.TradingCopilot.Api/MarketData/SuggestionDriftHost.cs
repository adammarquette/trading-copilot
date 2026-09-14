using System.Diagnostics;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The suggestion-drift watcher (gh#546, R-4 / R-12): the event log's <b>third</b> <c>market.quote</c> consumer. It
/// reads quotes from its own cursor and marks any <b>Active</b> suggestion the quote has drifted past its entry
/// tolerance <see cref="Domain.Suggestions.SuggestionState.Stale"/> — so a scratched setup greys out before execution.
/// </summary>
/// <remarks>
/// Its own consumer group tracks its own cursor (ADR-0001), independent of the stop-promotion and conditional-order
/// watchers. A fresh DI scope per pass keeps host shutdown clean (gh#169). Unlike those two, drift must bridge the
/// quote's <b>contract key</b> to a suggestion's <b>neutral symbol</b>, so — like the stop-promotion watcher — it
/// creates a venue per pass (<see cref="SuggestionDriftService"/> resolves each Active symbol once against it). It
/// only acts when Active suggestions exist, so it is harmless to run with none. The clock is read <b>here</b>, at
/// the boundary, and passed in — the drift decision stays pure.
/// </remarks>
public sealed class SuggestionDriftHost : BackgroundService
{
    /// <summary>The consumer group name whose cursor this watcher advances.</summary>
    public const string ConsumerGroup = "suggestion-drift";

    private const int BatchSize = 256;

    /// <summary>How long to wait when caught up before polling the log again (until LISTEN/NOTIFY lands).</summary>
    private static TimeSpan IdlePoll { get; } = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _services;
    private readonly ILogger<SuggestionDriftHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="logger">The logger.</param>
    public SuggestionDriftHost(IServiceProvider services, ILogger<SuggestionDriftHost> logger)
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
                    IExecutionMetrics metrics = scope.ServiceProvider.GetRequiredService<IExecutionMetrics>();
                    SuggestionDriftService drift = scope.ServiceProvider.GetRequiredService<SuggestionDriftService>();
                    IProjectXVenueFactory venueFactory = scope.ServiceProvider.GetRequiredService<IProjectXVenueFactory>();

                    cursor ??= await log.GetCursorAsync(ConsumerGroup, stoppingToken) ?? 0;
                    if (!announced)
                    {
                        _logger.LogInformation("Suggestion-drift watcher started at sequence {Cursor}.", cursor.Value);
                        announced = true;
                    }

                    EventPage page = await log.ReadAfterAsync(cursor.Value, BatchSize, stoppingToken);

                    if (page.Gap is not null)
                    {
                        // The retention gap gh#227 made loud, on a dashboard too (gh#295).
                        metrics.RecordRetentionGap(ConsumerGroup);

                        // Drift needs NO backfill (unlike stop-promotion): a quote missed in the gap just leaves the
                        // row Active until the next quote re-checks against fresh truth, and the take-time synchronous
                        // re-check (gh#548) is the authoritative backstop meanwhile. So resume at the head; the
                        // failure direction is "not yet marked stale", which is self-correcting on the next tick.
                        _logger.LogWarning(
                            "Event-log retention gap on {ConsumerGroup}: cursor {Cursor} fell behind the window; the "
                            + "oldest surviving sequence is {OldestAvailable}. Quotes in between were dropped; drift "
                            + "re-evaluates from the next quote, so no backfill is needed.",
                            ConsumerGroup, page.Gap.RequestedAfterSequence, page.Gap.OldestAvailableSequence);
                    }

                    IReadOnlyList<EventEnvelope> batch = page.Events;
                    caughtUp = batch.Count == 0;

                    if (!caughtUp)
                    {
                        // Advance a LOCAL end marker while collecting -- NOT the shared cursor. Because the work
                        // (ProcessQuotesAsync) runs once AFTER this loop rather than per event, advancing the cursor
                        // here would let a throw in that work skip the whole in-flight batch: the commit below is
                        // skipped on fault, but an already-advanced cursor is not re-read (it is non-null), so the next
                        // pass would read past the batch. The shared cursor is moved only after the work succeeds.
                        List<StopPromotionService.DecodedQuote> quotes = new(batch.Count);
                        long batchEnd = cursor.Value;
                        foreach (EventEnvelope evt in batch)
                        {
                            // Link this consume to the trace that produced the quote (gh#230), as the sibling consumers do.
                            using Activity? span = EventTracing.TryCreateLink(evt.TraceParent, out ActivityLink link)
                                ? TelemetryRegistration.Source.StartActivity(
                                    "suggestion-drift.consume", ActivityKind.Consumer, default(ActivityContext), links: [link])
                                : TelemetryRegistration.Source.StartActivity("suggestion-drift.consume", ActivityKind.Consumer);

                            if (StopPromotionService.TryDecodeQuote(evt, out StopPromotionService.DecodedQuote quote))
                            {
                                quotes.Add(quote);
                            }

                            metrics.RecordPipelineLag(ConsumerGroup, DateTimeOffset.UtcNow - evt.RecordedAt);
                            batchEnd = evt.Sequence;
                        }

                        // Resolve + mark ONCE for the whole batch (the resolve is a venue round-trip, never per quote).
                        if (quotes.Count > 0)
                        {
                            ITradingVenue venue = venueFactory.Create(FirmConventions.None);
                            await drift.ProcessQuotesAsync(quotes, venue, DateTimeOffset.UtcNow, stoppingToken);
                        }

                        // Only now that the batch's work has succeeded is the cursor advanced and committed -- a fault
                        // above leaves the cursor unchanged, so the batch is re-read and re-processed. Commit once per
                        // batch: at-least-once redelivery is safe because the Active→Stale update is idempotent (a
                        // re-seen quote re-marks nothing already Stale, ADR-0001).
                        cursor = batchEnd;
                        await log.CommitCursorAsync(ConsumerGroup, cursor.Value, stoppingToken);
                    }
                }

                if (caughtUp)
                {
                    // Caught up: wait for new events rather than hot-looping the log (LISTEN/NOTIFY replaces this poll).
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
                // A transient log / DB / venue fault must not kill the watcher: log, back off, retry. The cursor is
                // unchanged, so nothing is skipped.
                _logger.LogWarning(error, "Suggestion-drift pass failed; retrying after {Delay}.", IdlePoll);
                await Task.Delay(IdlePoll, stoppingToken);
            }
        }
    }
}
