using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Runs the indicator projection over the clean-historical bar store (gh#310, R-1, ADR-0001).
/// </summary>
/// <remarks>
/// <b>Always runs</b>, unlike the ingestion and backfill hosts: it needs no configuration of its own, because
/// its work list is whatever the bar store holds. With an empty store it does nothing and costs nothing — and
/// tying it to a second symbol list would only create a way for bars to exist without their indicators.
/// Follows the established host shape (gh#168/gh#169): fresh scope per pass, clean exits on cancellation and on
/// root-provider teardown, injected clock/delay seams.
/// </remarks>
public sealed class IndicatorProjectionHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IndicatorOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<IndicatorProjectionHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">The indicator parameters and cadence.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public IndicatorProjectionHost(
        IServiceProvider services,
        IOptions<IndicatorOptions> options,
        ILogger<IndicatorProjectionHost> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _services = services;
        _options = options.Value;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = _options.PollInterval;
        _logger.LogInformation(
            "Indicator projection started; ATR({Period}) over the bar store, every {Interval}.",
            _options.AtrPeriod, interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    IndicatorProjectionService service =
                        scope.ServiceProvider.GetRequiredService<IndicatorProjectionService>();

                    int written = await service.ProjectAsync(stoppingToken);
                    if (written > 0)
                    {
                        _logger.LogDebug("Indicator projection wrote or updated {Count} value(s).", written);
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
                // The service guards per series, so reaching here is a pass-level fault. Indicators are
                // recomputable, so a missed pass costs nothing the next one cannot restore.
                _logger.LogError(error, "Indicator projection pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
