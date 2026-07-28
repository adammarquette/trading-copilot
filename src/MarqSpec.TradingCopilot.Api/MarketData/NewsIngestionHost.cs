using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Polls every registered news source into the store of record (gh#358, R-2) — the non-market sibling of the bar
/// backfill host, following the same shape: opt-in, a fresh scope per pass, clean exits, injected clock/delay seams.
/// </summary>
/// <remarks>
/// <b>Opt-in.</b> With <c>News:Enabled</c> off it logs once and stops. The sources are DI-registered
/// <see cref="INewsSource"/> implementations resolved per pass through <see cref="NewsIngestionService"/>; until a
/// provider adapter (its own <c>MarqSpec.Client.*</c> submodule, a follow-on) is registered, an enabled poller
/// finds no sources and writes nothing.
/// </remarks>
public sealed class NewsIngestionHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly NewsIngestionOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<NewsIngestionHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">Whether news ingestion runs, and how often.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock, injected for tests; defaults to the real UTC wall clock.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public NewsIngestionHost(
        IServiceProvider services,
        IOptions<NewsIngestionOptions> options,
        ILogger<NewsIngestionHost> logger,
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
            _logger.LogInformation("News ingestion is disabled — set News:Enabled to turn it on.");
            return;
        }

        TimeSpan interval = _options.PollInterval;
        _logger.LogInformation("News ingestion started, polling every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    NewsIngestionService service = scope.ServiceProvider.GetRequiredService<NewsIngestionService>();
                    int written = await service.IngestAsync(_now(), stoppingToken);
                    if (written > 0)
                    {
                        _logger.LogDebug("News pass stored or updated {Count} item(s).", written);
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
                // The service guards per source, so reaching here is a pass-level fault (the scope). A missed pass
                // is covered by the next one's lookback overlap, so log and carry on rather than losing the poller.
                _logger.LogError(error, "News pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
