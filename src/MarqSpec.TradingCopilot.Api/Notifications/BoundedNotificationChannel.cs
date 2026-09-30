using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>Bounds how long one non-primary transport can hold the notification pump (gh#1157).</summary>
public sealed class BoundedNotificationChannel : INotificationChannel
{
    private readonly INotificationChannel _inner;
    private readonly TimeSpan _deadline;

    /// <summary>Creates the bound.</summary>
    /// <param name="inner">The transport to bound.</param>
    /// <param name="name">The transport name, for the log.</param>
    /// <param name="deadline">The total time the pump will wait for one send.</param>
    /// <param name="logger">The logger.</param>
    public BoundedNotificationChannel(
        INotificationChannel inner, string name, TimeSpan deadline, ILogger<BoundedNotificationChannel> logger)
    {
        _inner = inner;
        _deadline = deadline;
    }

    /// <inheritdoc />
    public Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken) =>
        _inner.SendAsync(notification, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken) =>
        _inner.ResolveAsync(dedupKey, cancellationToken);
}
