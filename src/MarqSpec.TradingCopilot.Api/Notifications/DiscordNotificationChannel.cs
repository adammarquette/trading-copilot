using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>Delivers the advisory to Discord (gh#1157, ADR-0019) — a second adapter behind <see cref="INotificationChannel"/>.</summary>
public sealed class DiscordNotificationChannel : INotificationChannel
{
    /// <summary>Creates the channel.</summary>
    /// <param name="client">The client; its timeout bounds how long a hung Discord can hold the caller.</param>
    /// <param name="options">The operator's Discord targets.</param>
    /// <param name="logger">The logger. Credentials are never written to it.</param>
    public DiscordNotificationChannel(
        HttpClient client,
        IOptions<DiscordOptions> options,
        ILogger<DiscordNotificationChannel> logger)
    {
    }

    /// <inheritdoc />
    public Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
