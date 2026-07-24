using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
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
                    StopPromotionService promotion = scope.ServiceProvider.GetRequiredService<StopPromotionService>();
                    IProjectXVenueFactory venueFactory = scope.ServiceProvider.GetRequiredService<IProjectXVenueFactory>();

                    cursor ??= await log.GetCursorAsync(ConsumerGroup, stoppingToken) ?? 0;
                    if (!announced)
                    {
                        _logger.LogInformation("Stop-promotion watcher started at sequence {Cursor}.", cursor.Value);
                        announced = true;
                    }

                    IReadOnlyList<EventEnvelope> batch = await log.ReadAfterAsync(cursor.Value, BatchSize, stoppingToken);
                    caughtUp = batch.Count == 0;

                    if (!caughtUp)
                    {
                        ITradingVenue venue = venueFactory.Create(FirmConventions.None);
                        foreach (EventEnvelope evt in batch)
                        {
                            if (StopPromotionService.TryDecodeQuote(evt, out StopPromotionService.DecodedQuote quote))
                            {
                                await promotion.PromoteForQuoteAsync(
                                    quote.Venue, quote.ContractKey, quote.Bid, quote.Ask, venue, stoppingToken);
                            }

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
}
