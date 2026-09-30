using MarqSpec.TradingCopilot.Domain.Notifications;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>Delivers one notification through several transports, each with its own dedup memory (gh#1157).</summary>
public sealed class FanOutNotificationChannel : INotificationChannel, IIncidentKeyRegistry
{
    /// <summary>Creates the fan-out.</summary>
    /// <param name="lanes">One dedup-wrapped transport per destination.</param>
    public FanOutNotificationChannel(IReadOnlyList<DedupingNotificationChannel> lanes)
    {
    }

    /// <inheritdoc />
    public Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public bool ReleaseIncident(string dedupKey) => throw new NotImplementedException();
}
