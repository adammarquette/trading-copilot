using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Polls the venue's REST history into the clean-historical bar store (gh#302, R-1) — the second of R-1's two
/// deliberately-separate market-data paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in.</b> With no <c>Backfill:Instruments</c> it logs once and stops, the same stance the ingestion host
/// takes: unconfigured means idle, never "archive everything". It is configured independently of
/// <c>Ingestion:Symbols</c> on purpose — an operator may want history without a live stream, or the reverse.
/// </para>
/// <para>
/// Follows the established host shape (gh#168/gh#169): a fresh scope per pass, clean exits on cancellation and
/// on root-provider teardown, and injected clock/delay seams so the cadence is deterministic under test. The
/// venue is resolved <b>inside</b> the loop, not the constructor — building it needs credentials a test host
/// lacks, and eager injection crashed host startup once already (gh#212).
/// </para>
/// </remarks>
public sealed class BarBackfillHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly BarBackfillOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<BarBackfillHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">What to archive, and how often.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock, injected for tests; defaults to the real UTC wall clock.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public BarBackfillHost(
        IServiceProvider services,
        IOptions<BarBackfillOptions> options,
        ILogger<BarBackfillHost> logger,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _services = services;
        _options = options.Value;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogInformation(
                "Clean-historical backfill is disabled — no instruments configured (Backfill:Instruments).");
            return;
        }

        TimeSpan interval = _options.PollInterval;
        _logger.LogInformation(
            "Clean-historical backfill started for {Instruments} at {Resolutions}m, every {Interval}.",
            string.Join(", ", _options.Instruments), string.Join(", ", _options.ResolutionMinutes), interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    // Resolved lazily, per pass: constructing the venue needs credentials, and the store is the
                    // one part of R-1 that a credential-less environment should still be able to host.
                    IProjectXVenueFactory factory = scope.ServiceProvider.GetRequiredService<IProjectXVenueFactory>();
                    ITradingVenue venue = factory.Create(FirmConventions.None);
                    BarBackfillService service = scope.ServiceProvider.GetRequiredService<BarBackfillService>();

                    int written = await service.BackfillAsync(venue, _now(), stoppingToken);
                    if (written > 0)
                    {
                        _logger.LogDebug("Backfill pass stored or updated {Count} bar(s).", written);
                    }
                }

                await _delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down
            }
            catch (Exception error)
            {
                // The service already guards per instrument, so reaching here is a pass-level fault (the venue
                // factory, the scope). History is catchable up -- a missed pass is covered by the next one's
                // lookback window -- so log and carry on rather than losing the poller.
                _logger.LogError(error, "Backfill pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
