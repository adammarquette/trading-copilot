using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// The channel used when none is configured (gh#243, ADR-0019): logs what it would have sent and reports that it
/// did not send it.
/// </summary>
/// <remarks>
/// A fresh deployment, a fork, and every local dev run start without a Pushover account, and none of them should
/// fail to boot over it — but the operator must not be able to believe they are covered when they are not. So the
/// notification still reaches the log at a severity matching its urgency, and <see cref="SendAsync"/> returns
/// <see langword="false"/>, which is the honest answer.
/// </remarks>
public sealed class NullNotificationChannel : INotificationChannel
{
    private readonly ILogger<NullNotificationChannel> _logger;

    /// <summary>Creates the channel.</summary>
    /// <param name="logger">The logger the notification is written to instead.</param>
    public NullNotificationChannel(ILogger<NullNotificationChannel> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // A Page that reaches nobody is the thing worth shouting about, so it logs as an error rather than
        // quietly matching the notification's own tone.
        LogLevel level = notification.Severity == NotificationSeverity.Page ? LogLevel.Error : LogLevel.Information;
        _logger.Log(
            level,
            "No notification channel configured — {Severity} not delivered: {Title} — {Body}",
            notification.Severity,
            notification.Title,
            notification.Body);

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    // True: an unconfigured channel never paged anyone, so there is nothing outstanding to cancel and nothing
    // for a retrying caller to chase (gh#300).
    public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken) => Task.FromResult(true);
}
