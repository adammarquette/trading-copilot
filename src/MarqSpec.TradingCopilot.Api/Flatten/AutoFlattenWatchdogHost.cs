using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// The redundant auto-flatten watchdog host (gh#187, R-13, ADR-0013): a <b>separate</b> <see cref="BackgroundService"/>
/// from the primary <see cref="AutoFlattenHost"/>, on its own loop and its own cadence. That separation is the
/// point — a hung, crashed, or exception-looping primary host cannot take this one down, so the flatten still
/// fires when the primary tier is degraded (the independent trigger ADR-0013 requires).
/// </summary>
/// <remarks>
/// It carries the same supervised discipline as the other hosts (fresh scope per pass, clean teardown on
/// cancellation / provider disposal, a transient fault logged and retried), and the same injected clock/delay
/// seams for deterministic tests. Like the primary it <b>always runs</b> (R-13): auto-flatten is on by default and
/// cannot be silently disabled. The watchdog defers to the primary within the grace window, so in the normal case
/// it finds nothing to do.
/// </remarks>
public sealed class AutoFlattenWatchdogHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly FlattenOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<AutoFlattenWatchdogHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">The schedule and the watchdog cadence.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock, injected for tests; defaults to the real UTC wall clock.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public AutoFlattenWatchdogHost(
        IServiceProvider services,
        IOptions<FlattenOptions> options,
        ILogger<AutoFlattenWatchdogHost> logger,
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
        TimeSpan interval = _options.WatchdogPollInterval;
        _logger.LogInformation("Auto-flatten watchdog started; evaluating every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    AutoFlattenWatchdogService service = scope.ServiceProvider.GetRequiredService<AutoFlattenWatchdogService>();
                    await service.RunPassAsync(_now(), stoppingToken);
                }

                await _delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop -- the host is shutting down
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down -- exit cleanly rather than crash the host
            }
            catch (Exception error)
            {
                // The watchdog is the safety net; a transient fault in one pass must not kill it. Log, back off,
                // and try again -- the deadline arrives regardless.
                _logger.LogError(error, "Auto-flatten watchdog pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
