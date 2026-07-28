using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Drives the deterministic trigger scan (gh#385, A1) — the periodic host that evaluates every enabled trigger and
/// fires a mechanical alert on the arming edge. Mirrors <c>IndicatorProjectionHost</c>: always on, a fresh DI scope
/// per pass, and injected clock/delay seams so the cadence is deterministic under test.
/// </summary>
/// <remarks>
/// Always on (like the indicator projection it consumes) — the scan's work list is whatever enabled triggers the
/// operator holds, so there is nothing to opt into; with no triggers the pass is a cheap no-op. A per-pass fault
/// is logged and the loop continues; a missed pass self-heals on the next one because the debounce state is
/// persisted.
/// </remarks>
public sealed class TriggerScanHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TriggerOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<TriggerScanHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">The scan cadence.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock, injected for tests; defaults to the real UTC wall clock.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public TriggerScanHost(
        IServiceProvider services,
        IOptions<TriggerOptions> options,
        ILogger<TriggerScanHost> logger,
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
        TimeSpan interval = _options.PollInterval;
        _logger.LogInformation("Trigger scan started, evaluating every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    TriggerEvaluationService service =
                        scope.ServiceProvider.GetRequiredService<TriggerEvaluationService>();
                    int fired = await service.EvaluateAsync(_now(), stoppingToken);
                    if (fired > 0)
                    {
                        _logger.LogDebug("Trigger scan fired {Count} trigger(s).", fired);
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
                _logger.LogError(error, "Trigger scan pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
