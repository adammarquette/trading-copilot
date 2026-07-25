using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// The auto-flatten scheduler (gh#185, R-13, ADR-0013): the supervised background service that evaluates the
/// per-instrument flatten decision on the DST-aware market clock and fires the flatten at each deadline — the
/// <b>primary</b> R-13 trigger.
/// </summary>
/// <remarks>
/// <para>
/// A fresh DI scope is opened per pass (the event log and DbContext are scoped), which also keeps host shutdown
/// clean — the same discipline as the stop-promotion watcher (gh#153/gh#168). Unlike the opt-in ingestion host,
/// this <b>always runs</b>: auto-flatten is on by default and cannot be silently disabled (R-13); a market is
/// turned off per-instrument in configuration, not by omitting the host.
/// </para>
/// <para>
/// The clock and the loop delay are injected seams (defaulting to the real wall clock and
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>) so the cadence is deterministic under test — the timing
/// precedent set by the quote-subscription supervisor.
/// This is the primary tier; the <b>redundant / independent</b> trigger that must fire even when this tier is
/// degraded is a separate slice (gh#187).
/// </para>
/// </remarks>
public sealed class AutoFlattenHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly FlattenOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<AutoFlattenHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">The schedule and poll cadence.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock, injected for tests; defaults to the real UTC wall clock.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public AutoFlattenHost(
        IServiceProvider services,
        IOptions<FlattenOptions> options,
        ILogger<AutoFlattenHost> logger,
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
        _logger.LogInformation("Auto-flatten scheduler started; evaluating every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A FRESH scope per pass: the event log and DbContext are scoped, so holding one for the app's
                // lifetime would leak tracked entities and, on teardown, leave this loop on disposed services.
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
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
                // The root provider is being torn down (host shutdown may not signal the token first, e.g. a
                // WebApplicationFactory disposing between test classes). Exit cleanly rather than crash the host.
                break;
            }
            catch (Exception error)
            {
                // A transient DB / venue fault must not kill the scheduler -- the deadline arrives regardless. Log,
                // back off, and try again. The independent guarantee above this primary tier is the watchdog (gh#187).
                _logger.LogError(error, "Auto-flatten pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }
}
