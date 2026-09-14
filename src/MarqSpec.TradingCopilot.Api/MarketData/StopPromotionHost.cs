using System.Diagnostics;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The stop-promotion watcher (ADR-0007, gh#153): the event log's <b>first consumer</b>. It reads
/// <c>market.quote</c> events from its own cursor and promotes any hidden stop the quote brings within its band.
/// </summary>
/// <remarks>
/// A consumer group tracks its own cursor (ADR-0001), so this is independent of every other reader and replays
/// from where it left off after a restart. It only acts when staged stops exist, so it is harmless to run with
/// none — no configuration gate needed. A fresh DI scope is opened per pass (the log and DbContext are scoped),
/// which also keeps host shutdown clean; the promotion service reads across the R-20 boundary deliberately (see
/// its note).
/// </remarks>
public sealed class StopPromotionHost : BackgroundService
{
    /// <summary>The consumer group name whose cursor this watcher advances.</summary>
    public const string ConsumerGroup = "stop-promotion";

    private const int BatchSize = 256;

    /// <summary>How long to wait when caught up before polling the log again (until LISTEN/NOTIFY lands).</summary>
    private static TimeSpan IdlePoll { get; } = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _services;
    private readonly ILogger<StopPromotionHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per run.</param>
    /// <param name="logger">The logger.</param>
    public StopPromotionHost(IServiceProvider services, ILogger<StopPromotionHost> logger)
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
                // lifetime would leak tracked entities and -- when the host is torn down (integration tests) --
                // leave this loop polling disposed services. Per-pass scoping keeps shutdown clean.
                bool caughtUp;
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
                    IExecutionMetrics metrics = scope.ServiceProvider.GetRequiredService<IExecutionMetrics>();
                    StopPromotionService promotion = scope.ServiceProvider.GetRequiredService<StopPromotionService>();
                    IProjectXVenueFactory venueFactory = scope.ServiceProvider.GetRequiredService<IProjectXVenueFactory>();

                    cursor ??= await log.GetCursorAsync(ConsumerGroup, stoppingToken) ?? 0;
                    if (!announced)
                    {
                        _logger.LogInformation("Stop-promotion watcher started at sequence {Cursor}.", cursor.Value);
                        announced = true;
                    }

                    EventPage page = await log.ReadAfterAsync(cursor.Value, BatchSize, stoppingToken);

                    if (page.Gap is not null)
                    {
                        // The silent hole gh#227 made loud in the log, now visible on a dashboard too (gh#295).
                        metrics.RecordRetentionGap(ConsumerGroup);

                        // The log is lossy past its window (ADR-0001, gh#227). Quotes we never saw were dropped,
                        // so a hidden stop may have crossed its promotion band unobserved -- derived state is now
                        // confidently wrong, not merely stale. Say so LOUDLY and resume deliberately: the native
                        // safety stop is the physical floor throughout, and the next quote re-evaluates every
                        // hidden stop from scratch, so resuming at the head is recovery rather than a guess.
                        // The signal stays exactly as loud as gh#227 made it. Backfill is IN ADDITION to telling
                        // the operator, never instead of it -- a recovery that quietened the signal would trade a
                        // known hole for an unknown one.
                        _logger.LogError(
                            "Event-log retention gap on {ConsumerGroup}: cursor {Cursor} fell behind the window; "
                            + "the oldest surviving sequence is {OldestAvailable}. Quotes in between were dropped "
                            + "and are NOT recoverable from this log. Backfilling the window from the "
                            + "clean-historical store; the native safety stop held throughout.",
                            ConsumerGroup,
                            page.Gap.RequestedAfterSequence,
                            page.Gap.OldestAvailableSequence);

                        await BackfillAsync(scope, log, page.Gap, venueFactory, stoppingToken);
                    }

                    IReadOnlyList<EventEnvelope> batch = page.Events;
                    caughtUp = batch.Count == 0;

                    if (!caughtUp)
                    {
                        ITradingVenue venue = venueFactory.Create(FirmConventions.None);
                        foreach (EventEnvelope evt in batch)
                        {
                            // Continue the producer's trace across the async gap (gh#230): a LINK, not a parent --
                            // this consumer may run long after the append and one batch can span many producers.
                            // An absent or malformed traceparent simply yields no link and a fresh trace.
                            using Activity? span = EventTracing.TryCreateLink(evt.TraceParent, out ActivityLink link)
                                ? TelemetryRegistration.Source.StartActivity(
                                    "stop-promotion.consume", ActivityKind.Consumer, default(ActivityContext), links: [link])
                                : TelemetryRegistration.Source.StartActivity("stop-promotion.consume", ActivityKind.Consumer);

                            if (StopPromotionService.TryDecodeQuote(evt, out StopPromotionService.DecodedQuote quote))
                            {
                                await promotion.PromoteForQuoteAsync(
                                    quote.Venue, quote.ContractKey, quote.Bid, quote.Ask, venue, stoppingToken);
                            }

                            metrics.RecordPipelineLag(ConsumerGroup, DateTimeOffset.UtcNow - evt.RecordedAt);
                            cursor = evt.Sequence;
                        }

                        // Commit once per batch: at-least-once redelivery on restart is safe because promotion
                        // is idempotent (an already-Native stop is skipped, ADR-0001).
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
                // The root provider is being torn down (host shutdown may not signal the token first, e.g. a
                // WebApplicationFactory disposing between test classes). Nothing to recover to -- exit cleanly
                // rather than crash the host and cascade into every other test.
                break;
            }
            catch (Exception error)
            {
                // A transient log/DB/venue fault must not kill the watcher: log, back off, and try again. The
                // cursor is unchanged, so nothing is skipped.
                _logger.LogWarning(error, "Stop-promotion pass failed; retrying after {Delay}.", IdlePoll);
                await Task.Delay(IdlePoll, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Recovers what the blind window is still recoverable from — the clean-historical bar store (gh#306, R-1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs inside this consumer's own pass, so a consumer catching up <b>cannot stall the ones that are
    /// current</b>: each rides its own <see cref="BackgroundService"/> loop with its own cursor.
    /// </para>
    /// <para>
    /// <b>Never fatal.</b> A backfill that throws must not take down the watcher or block the live path — the
    /// live path is the one holding the safety-critical duty, and the gap has already been reported at error.
    /// So a failure here degrades recovery to the pre-gh#306 behaviour (resume at the head) rather than
    /// escalating a recovery problem into an outage.
    /// </para>
    /// </remarks>
    private async Task BackfillAsync(
        AsyncServiceScope scope,
        IEventLog log,
        EventRetentionGap gap,
        IProjectXVenueFactory venueFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset? since = await log.GetCursorCommittedAtAsync(ConsumerGroup, cancellationToken);
            if (since is null)
            {
                // No commit row means no known lower bound. Inventing one would be fabricating the window's size,
                // so say what is true instead (engineering §9: never fabricate a number).
                _logger.LogError(
                    "Gap on {ConsumerGroup} cannot be backfilled: no committed cursor, so the start of the blind "
                    + "window is unknown. Resuming at the head without recovery.", ConsumerGroup);
                return;
            }

            GapBackfillService backfill = scope.ServiceProvider.GetRequiredService<GapBackfillService>();
            ITradingVenue venue = venueFactory.Create(FirmConventions.None);

            GapBackfillOutcome outcome = await backfill.ReplayForStopPromotionAsync(
                since.Value, gap.OldestAvailableOccurredAt, venue.Id, venue, cancellationToken);

            _logger.LogWarning(
                "Gap backfill on {ConsumerGroup} re-evaluated hidden stops against {From}–{To} and promoted "
                + "{Promoted}.",
                ConsumerGroup, since.Value, gap.OldestAvailableOccurredAt, outcome.Promoted);

            // The shortfall is reported SEPARATELY and at error, never folded into the line above. A backfill that
            // covered part of a window and read as "recovered" would be worse than the silence gh#227 replaced.
            foreach (InstrumentCoverage shortfall in outcome.Shortfalls)
            {
                _logger.LogError(
                    "Gap backfill on {ConsumerGroup} did NOT fully cover {Contract}: {Shortfall} of {From}–{To} "
                    + "has no bars in the store. Hidden stops on it may have crossed their promotion band "
                    + "unobserved and are NOT recovered — the native safety stop remains the floor.",
                    ConsumerGroup, shortfall.ContractKey, shortfall.Coverage.Shortfall,
                    since.Value, gap.OldestAvailableOccurredAt);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Gap backfill on {ConsumerGroup} failed. The gap itself was already reported; resuming at the head "
                + "without recovery, with the native safety stop as the floor.", ConsumerGroup);
        }
    }
}
