using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Sends notifications through Pushover (gh#243, ADR-0019) — the first adapter behind
/// <see cref="INotificationChannel"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pushover was chosen because an on-call-of-one needs a <b>pager</b>: its <b>Emergency</b> priority (2) is the
/// only one that repeats until <i>acknowledged</i> and bypasses Do Not Disturb. That is the entire reason
/// <see cref="NotificationSeverity.Page"/> exists, so the mapping below is the load-bearing line in this file — a
/// P1 delivered at ordinary priority has silently failed at the one job the channel has.
/// </para>
/// <para>
/// Failure-tolerant by construction: any transport fault returns <see langword="false"/> rather than throwing,
/// because this is called from the auto-flatten path. A genuine caller cancellation still propagates so host
/// shutdown stays clean. Credentials never reach the log.
/// </para>
/// </remarks>
public sealed class PushoverNotificationChannel : INotificationChannel
{
    private const string MessagesUrl = "https://api.pushover.net/1/messages.json";

    private readonly HttpClient _client;
    private readonly PushoverOptions _options;
    private readonly ILogger<PushoverNotificationChannel> _logger;

    // Emergency receipts, so a page that is no longer true can be cancelled. Bounded by the number of concurrently
    // open incidents, which is a handful of instruments -- no eviction policy needed.
    private readonly ConcurrentDictionary<string, string> _receipts = new(StringComparer.Ordinal);

    /// <summary>Creates the channel.</summary>
    /// <param name="client">The client; its timeout bounds how long a hung Pushover can hold the caller.</param>
    /// <param name="options">The operator's Pushover application.</param>
    /// <param name="logger">The logger. Credentials are never written to it.</param>
    public PushoverNotificationChannel(
        HttpClient client,
        IOptions<PushoverOptions> options,
        ILogger<PushoverNotificationChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!_options.IsConfigured)
        {
            return false;
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["token"] = _options.AppToken!,
            ["user"] = _options.UserKey!,
            ["title"] = notification.Title,
            ["message"] = notification.Body,
            ["priority"] = PriorityOf(notification.Severity),
        };

        if (notification.Severity == NotificationSeverity.Page)
        {
            // Emergency is the only priority that repeats until acknowledged; Pushover requires both fields with it.
            form["retry"] = _options.PageRetrySeconds.ToString(CultureInfo.InvariantCulture);
            form["expire"] = _options.PageExpireSeconds.ToString(CultureInfo.InvariantCulture);
        }

        try
        {
            using FormUrlEncodedContent content = new(form);
            using HttpResponseMessage response = await _client.PostAsync(new Uri(MessagesUrl), content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Pushover rejected a {Severity} notification with {Status}: {Title}",
                    notification.Severity, (int)response.StatusCode, notification.Title);
                return false;
            }

            if (notification.Severity == NotificationSeverity.Page)
            {
                string? receipt = await ReadReceiptAsync(response, cancellationToken);
                if (receipt is not null)
                {
                    _receipts[notification.DedupKey] = receipt;
                }
            }

            _logger.LogInformation("Sent a {Severity} notification: {Title}", notification.Severity, notification.Title);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a real shutdown -- do not swallow it as a timeout
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Pushover timed out sending a {Severity} notification: {Title}", notification.Severity, notification.Title);
            return false;
        }
        catch (HttpRequestException error)
        {
            // Never rethrow into the flatten path. The message carries no token or user key.
            _logger.LogWarning(error, "Could not deliver a {Severity} notification: {Title}", notification.Severity, notification.Title);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ResolveAsync(string dedupKey, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured || !_receipts.TryRemove(dedupKey, out string? receipt))
        {
            return; // nothing outstanding for this incident
        }

        try
        {
            Uri cancelEndpoint = new($"https://api.pushover.net/1/receipts/{receipt}/cancel.json");
            using FormUrlEncodedContent content = new(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["token"] = _options.AppToken!,
            });

            using HttpResponseMessage response = await _client.PostAsync(cancelEndpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Pushover would not cancel the page for {Incident} ({Status}).", dedupKey, (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            // A page that keeps nagging after the condition cleared is an annoyance; throwing here would be a
            // fault on the flatten path. The annoyance is the better trade.
            _logger.LogWarning(error, "Could not cancel the outstanding page for {Incident}.", dedupKey);
        }
    }

    private static string PriorityOf(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Page => "2",    // Emergency: repeats until acknowledged, bypasses Do Not Disturb
        NotificationSeverity.Notify => "0",  // normal
        NotificationSeverity.Quiet => "-1",  // delivered, no sound
        _ => "0",
    };

    private async Task<string?> ReadReceiptAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument parsed = JsonDocument.Parse(body);
            return parsed.RootElement.TryGetProperty("receipt", out JsonElement receipt)
                ? receipt.GetString()
                : null;
        }
        catch (JsonException)
        {
            // No receipt just means this page cannot be cancelled early -- not worth failing the send over.
            _logger.LogDebug("Pushover returned no parsable receipt; the page cannot be cancelled early.");
            return null;
        }
    }
}
