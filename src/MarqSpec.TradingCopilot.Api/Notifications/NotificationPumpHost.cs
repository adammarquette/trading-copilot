using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Runs the notification pump (gh#289): drains what the flatten path enqueued, off the flatten path.
/// </summary>
/// <remarks>
/// A hosted service rather than a task started in a constructor, so shutdown is <b>ordered</b> — the host stops
/// the pump, the pump flushes what is still queued, and a page raised in the final seconds of a session is
/// delivered rather than dropped. The channel it drives is a singleton, so no scope is opened per pass: unlike
/// the other hosts here there is no scoped dependency to leak.
/// </remarks>
public sealed class NotificationPumpHost : BackgroundService
{
    private readonly QueuedNotificationChannel _queue;
    private readonly ILogger<NotificationPumpHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="queue">The queueing channel to drain.</param>
    /// <param name="logger">The logger.</param>
    public NotificationPumpHost(QueuedNotificationChannel queue, ILogger<NotificationPumpHost> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notification pump started.");

        try
        {
            await _queue.PumpAsync(stoppingToken);
        }
        catch (ObjectDisposedException)
        {
            // The root provider is being torn down (a WebApplicationFactory between test classes). Exit cleanly
            // rather than crash the host -- the gh#168 shape.
        }
        catch (Exception error)
        {
            // The pump owns its own per-delivery try/catch, so reaching here means something structural. Log it
            // loudly: from this point notifications are queued and never drained.
            _logger.LogCritical(error, "The notification pump stopped — queued notifications will NOT be delivered.");
        }
    }
}
