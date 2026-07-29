using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Drives <see cref="NotificationOutboxRelay"/> on a loop (gh#437 ⇒ gh#400), so a page persisted by one process
/// is delivered even if that process never got the chance.
/// </summary>
/// <remarks>
/// <para>
/// <b>A fresh scope per pass.</b> The relay depends on the scoped <c>DbContext</c>; holding one for the host's
/// lifetime leaks a context across every pass and cascades <c>ObjectDisposedException</c> once the root provider
/// is torn down — the gh#168 shape the other hosts here already avoid.
/// </para>
/// <para>
/// <b>It sweeps rather than waits for a signal.</b> The point of the outbox is the case where the process that
/// wrote the row is gone, so there is no in-memory event left to react to. A sweep is the only thing that finds
/// work left by a previous life.
/// </para>
/// <para>
/// <b>The interval is a constant, not configuration</b> — deliberately. A new configuration key must be added to
/// <c>.env.example</c> AND the compose <c>environment:</c> allowlist or it is silently dropped (Platform
/// contract, gh#236 / gh#325), and nothing here needs operator tuning: the relay is a backstop behind the
/// in-process queue, which already delivers the common case in milliseconds. Fifteen seconds bounds how long a
/// page can sit after a crash while costing one indexed query per interval.
/// </para>
/// </remarks>
public sealed class NotificationOutboxRelayHost : BackgroundService
{
    /// <summary>How long a page can sit unsent after a crash before the sweep finds it.</summary>
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _services;
    private readonly ILogger<NotificationOutboxRelayHost> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The scope factory — one scope per pass.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="delay">The loop delay, injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public NotificationOutboxRelayHost(
        IServiceScopeFactory services,
        ILogger<NotificationOutboxRelayHost> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _services = services;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notification outbox relay started (sweeping every {Interval}).", _sweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    NotificationOutboxRelay relay = scope.ServiceProvider.GetRequiredService<NotificationOutboxRelay>();
                    await relay.DrainAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // ordered shutdown
            }
            catch (ObjectDisposedException)
            {
                // The root provider is being torn down (a WebApplicationFactory between test classes). Exit
                // cleanly rather than crash the host -- the gh#168 shape.
                break;
            }
            catch (Exception error)
            {
                // The relay isolates each delivery, so reaching here means something structural -- a broken
                // database, a bad scope. Log loudly and keep sweeping: the alternative is a dead relay and an
                // outbox that silently stops delivering.
                _logger.LogError(error, "An outbox relay pass failed; retrying in {Interval}.", _sweepInterval);
            }

            try
            {
                await _delay(_sweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Notification outbox relay stopped.");
    }
}
