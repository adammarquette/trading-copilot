using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// Drives the deterministic trigger scan on a fixed cadence (gh#385, R-4 / R-7, ADR-0008).
/// </summary>
/// <remarks>
/// <b>Always runs:</b> harmless with no triggers, so it needs no configuration of its own. Follows the established
/// host shape (gh#168/gh#169, mirroring <c>IndicatorProjectionHost</c>): fresh scope per pass, clean exits on
/// cancellation and on root-provider teardown, injected clock/delay seams. A pass-level fault is logged and retried
/// after the interval — triggers are recomputable, so a missed pass costs nothing the next one cannot restore.
/// </remarks>
public sealed class TriggerScanHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TriggerOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<TriggerScanHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">The scan cadence.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public TriggerScanHost(
        IServiceProvider services,
        IOptions<TriggerOptions> options,
        ILogger<TriggerScanHost> logger,
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
        _logger.LogInformation("Trigger scan started; evaluating mechanical triggers every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    TriggerEvaluationService service =
                        scope.ServiceProvider.GetRequiredService<TriggerEvaluationService>();

                    int fired = await service.ScanAsync(DateTimeOffset.UtcNow, stoppingToken);
                    if (fired > 0)
                    {
                        _logger.LogDebug("Trigger scan fired {Count} mechanical alert(s).", fired);
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
                // The service guards per owner, so reaching here is a pass-level fault. Triggers are recomputable, so
                // a missed pass costs nothing the next one cannot restore.
                _logger.LogError(error, "Trigger scan pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
