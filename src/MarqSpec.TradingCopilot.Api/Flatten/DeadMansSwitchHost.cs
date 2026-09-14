using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// Drives the dead-man's switch (gh#244, R-13, ADR-0019): the liveness heartbeat, and the daily per-instrument
/// flatten check-in.
/// </summary>
/// <remarks>
/// <para>
/// This host is the only part of the alerting story that reports to somewhere <b>outside</b> this process, which
/// is what makes it the one that survives the process dying — its silence is the signal. Everything else here
/// (the in-process watchdog, any in-stack rule engine) dies with the host it is meant to report on.
/// </para>
/// <para>
/// It owns the "already reported today" map rather than the scoped service, because the service is rebuilt per
/// pass. The map is <b>in-memory on purpose</b>: a restart simply re-sends a check-in, which is idempotent at the
/// monitor, and that costs far less than a migration and a table for state that is worthless tomorrow.
/// </para>
/// <para>
/// Follows the established host shape (gh#168/gh#169): a fresh scope per pass, clean exits on cancellation and on
/// root-provider teardown, and injected clock/delay seams so the cadence is deterministic under test.
/// </para>
/// </remarks>
public sealed class DeadMansSwitchHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly CheckInOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<DeadMansSwitchHost> _logger;
    private readonly Dictionary<InstrumentId, DateOnly> _reported = [];

    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per pass.</param>
    /// <param name="options">The monitor's check URLs and cadences.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock, injected for tests; defaults to the real UTC wall clock.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public DeadMansSwitchHost(
        IServiceProvider services,
        IOptions<CheckInOptions> options,
        ILogger<DeadMansSwitchHost> logger,
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
            // Running unmonitored is allowed -- a fork or a fresh deployment has no monitor account yet. Running
            // unmonitored SILENTLY is not: this is the one safety net that covers the host dying, and its absence
            // must be a deliberate, visible choice rather than a default nobody noticed.
            _logger.LogWarning(
                "Dead-man's switch is NOT configured — no external monitor will notice if this process dies before "
                + "an auto-flatten deadline. Set the CheckIn configuration to arm it (ADR-0019, gh#244).");
            return;
        }

        TimeSpan interval = _options.PollInterval;
        _logger.LogInformation(
            "Dead-man's switch started; evaluating every {Interval}, heartbeat every {Heartbeat}.",
            interval, _options.HeartbeatInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken);
                await _delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop -- the host is shutting down
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down
            }
            catch (Exception error)
            {
                // A fault here must not kill the loop: the deadline arrives regardless, and a switch that stopped
                // reporting because of a transient error would page for a system that is fine.
                _logger.LogError(error, "Dead-man's switch pass failed; retrying after {Interval}.", interval);
                await _delay(interval, stoppingToken);
            }
        }
    }

    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset now = _now();

        // A FRESH scope per pass: the service holds a scoped DbContext, so keeping one for the host's lifetime
        // would leak tracked entities and leave this loop on disposed services at teardown.
        await using AsyncServiceScope scope = _services.CreateAsyncScope();

        if (now - _lastHeartbeat >= _options.HeartbeatInterval)
        {
            IDeadMansSwitch heartbeat = scope.ServiceProvider.GetRequiredService<IDeadMansSwitch>();
            if (await heartbeat.ReportAliveAsync(stoppingToken))
            {
                _lastHeartbeat = now;
            }
        }

        FlattenCheckInService service = scope.ServiceProvider.GetRequiredService<FlattenCheckInService>();
        IReadOnlyDictionary<InstrumentId, DateOnly> sent = await service.RunPassAsync(now, _reported, stoppingToken);

        foreach (KeyValuePair<InstrumentId, DateOnly> entry in sent)
        {
            _reported[entry.Key] = entry.Value;
        }
    }
}
