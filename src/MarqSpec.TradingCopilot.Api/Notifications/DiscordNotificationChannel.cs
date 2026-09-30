using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Delivers the advisory to Discord (gh#1157, ADR-0019) — the second adapter behind
/// <see cref="INotificationChannel"/>, beside <see cref="PushoverNotificationChannel"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Publish only.</b> It posts what the seam already emits and returns: no reply is read, no thread is kept,
/// nothing listens (that is a separate, gated card), and nothing here can place, size or modify an order.
/// Severity is <i>rendered</i> — embed colour, a footer label, a silent flag for
/// <see cref="NotificationSeverity.Quiet"/> — and never reinterpreted: Discord is an advisory transport, so a
/// <see cref="NotificationSeverity.Page"/> does not gain a mention or a repeat here. Pushover remains the pager.
/// </para>
/// <para>
/// Two destinations, chosen by which options are set: a <b>webhook</b> posts to a channel, and a <b>bot token
/// plus the operator's user id</b> opens a DM to that one pinned recipient and posts into it. Both configured
/// means both. <see cref="SendAsync"/> reports accepted when <i>either</i> took it, because the operator can see
/// it somewhere. The known cost: a DM failure after a webhook success is not retried by itself, because the lane's
/// dedup memory then records the incident as told.
/// </para>
/// <para>
/// Failure-tolerant by construction, like every transport behind the seam: a fault returns
/// <see langword="false"/> rather than throwing, because the chain sits beside the auto-flatten. A real caller
/// cancellation still propagates. A <b>429</b> is logged as rate-limiting with its <c>Retry-After</c>, not as a
/// generic rejection, so throttling reads differently from Discord being down. <b>No credential is ever logged</b>:
/// the webhook URL carries its token in the path, so no request URI and no exception object is written either.
/// </para>
/// </remarks>
public sealed class DiscordNotificationChannel : INotificationChannel
{
    private const string ApiBase = "https://discord.com/api/v10";
    private const string UserAgent = "DiscordBot (https://github.com/adammarquette/trading-copilot, 1)";

    // Discord's embed limits are 256 (title) and 4096 (description); the description is kept under its cap.
    private const int TitleLimit = 256;
    private const int DescriptionLimit = 4000;

    // SUPPRESS_NOTIFICATIONS: the message is posted without a push or a sound.
    private const int SuppressNotificationsFlag = 4096;

    private readonly HttpClient _client;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordNotificationChannel> _logger;

    // The operator's DM channel id, cached after the first open. The pump is the only caller and is single
    // threaded, so a plain field is enough; it is dropped when Discord refuses it.
    private string? _dmChannelId;

    /// <summary>Creates the channel.</summary>
    /// <param name="client">The client; its timeout bounds how long a hung Discord can hold the caller.</param>
    /// <param name="options">The operator's Discord targets.</param>
    /// <param name="logger">The logger. Credentials are never written to it.</param>
    public DiscordNotificationChannel(
        HttpClient client,
        IOptions<DiscordOptions> options,
        ILogger<DiscordNotificationChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Whether <paramref name="value"/> is a Discord snowflake id: 17 to 20 ASCII digits.</summary>
    /// <param name="value">The candidate id.</param>
    /// <returns><see langword="true"/> if it is safe to treat as an id and interpolate into a path.</returns>
    public static bool IsSnowflake(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Length is >= 17 and <= 20 && value.All(char.IsAsciiDigit);
    }

    /// <inheritdoc />
    public async Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!_options.IsConfigured)
        {
            return false;
        }

        string payload = BuildPayload(notification);
        bool delivered = false;

        if (_options.HasWebhook)
        {
            delivered |= await PostToWebhookAsync(payload, notification, cancellationToken);
        }

        if (_options.HasDirectMessage)
        {
            delivered |= await PostToDirectMessageAsync(payload, notification, cancellationToken);
        }

        return delivered;
    }

    /// <inheritdoc />
    // True: an advisory leaves nothing outstanding (no Emergency receipt to cancel), so there is nothing for a
    // retrying caller to chase (gh#300).
    public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken) => Task.FromResult(true);

    private static string BuildPayload(Notification notification)
    {
        Dictionary<string, object> body = new(StringComparer.Ordinal)
        {
            ["embeds"] = new[]
            {
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["title"] = Truncate(notification.Title, TitleLimit),
                    ["description"] = Truncate(notification.Body, DescriptionLimit),
                    ["color"] = ColorOf(notification.Severity),
                    ["footer"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["text"] = LabelOf(notification.Severity),
                    },
                },
            },

            // Nothing in the title or body may ping anyone: it is data, and @everyone in it must stay text.
            ["allowed_mentions"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["parse"] = Array.Empty<string>(),
            },
        };

        if (notification.Severity == NotificationSeverity.Quiet)
        {
            body["flags"] = SuppressNotificationsFlag;
        }

        return JsonSerializer.Serialize(body);
    }

    private static int ColorOf(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Page => 0xE74C3C,
        NotificationSeverity.Notify => 0xE67E22,
        NotificationSeverity.Quiet => 0x95A5A6,
        _ => 0x95A5A6,
    };

    private static string LabelOf(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Page => "Page",
        NotificationSeverity.Notify => "Notify",
        NotificationSeverity.Quiet => "Quiet",
        _ => severity.ToString(),
    };

    private static string Truncate(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        int cut = limit - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--; // never split a surrogate pair
        }

        return string.Concat(text.AsSpan(0, cut), "…");
    }

    private async Task<bool> PostToWebhookAsync(string payload, Notification notification, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(_options.WebhookUrl!));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        Outcome outcome = await ExchangeAsync(request, "channel", notification, readBody: false, cancellationToken);
        return outcome.Ok;
    }

    private async Task<bool> PostToDirectMessageAsync(string payload, Notification notification, CancellationToken cancellationToken)
    {
        string? channelId = _dmChannelId ?? await OpenDirectMessageAsync(notification, cancellationToken);
        if (channelId is null)
        {
            return false;
        }

        using HttpRequestMessage request = BotRequest(new Uri($"{ApiBase}/channels/{channelId}/messages"));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        Outcome outcome = await ExchangeAsync(request, "DM", notification, readBody: false, cancellationToken);
        if (outcome.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // The cached DM channel is gone or closed to the bot; the next send opens a fresh one.
            _dmChannelId = null;
        }

        return outcome.Ok;
    }

    private async Task<string?> OpenDirectMessageAsync(Notification notification, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = BotRequest(new Uri($"{ApiBase}/users/@me/channels"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["recipient_id"] = _options.OperatorUserId!,
            }),
            Encoding.UTF8,
            "application/json");

        Outcome outcome = await ExchangeAsync(request, "DM", notification, readBody: true, cancellationToken);
        if (!outcome.Ok)
        {
            return null;
        }

        string? id = ReadId(outcome.Body);

        // The id is interpolated into a URL path, so only a genuine snowflake is ever used.
        if (id is null || !IsSnowflake(id))
        {
            _logger.LogWarning("Discord returned no usable DM channel id; the {Severity} notification was not posted: {Title}",
                notification.Severity, notification.Title);
            return null;
        }

        _dmChannelId = id;
        return id;
    }

    private HttpRequestMessage BotRequest(Uri uri)
    {
        HttpRequestMessage request = new(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _options.BotToken);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    private static string? ReadId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(body);
            return parsed.RootElement.ValueKind == JsonValueKind.Object
                && parsed.RootElement.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends one request and turns every outcome into a value, logging it without any credential.</summary>
    private async Task<Outcome> ExchangeAsync(
        HttpRequestMessage request,
        string destination,
        Notification notification,
        bool readBody,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Throttling, not an outage: say so, with how long Discord asked us to wait.
                double? retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                string wait = retryAfter is double seconds
                    ? Math.Ceiling(seconds).ToString(CultureInfo.InvariantCulture) + "s"
                    : "unspecified";
                _logger.LogWarning(
                    "Discord rate-limited a {Severity} notification to the {Destination} (429), retry after {RetryAfter}: {Title}",
                    notification.Severity, destination, wait, notification.Title);
                return new Outcome(false, response.StatusCode, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Discord rejected a {Severity} notification to the {Destination} with {Status}: {Title}",
                    notification.Severity, destination, (int)response.StatusCode, notification.Title);
                return new Outcome(false, response.StatusCode, null);
            }

            string? body = readBody ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
            if (!readBody)
            {
                _logger.LogInformation(
                    "Sent a {Severity} notification to Discord {Destination}: {Title}",
                    notification.Severity, destination, notification.Title);
            }

            return new Outcome(true, response.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a real shutdown -- do not swallow it as a timeout
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Discord timed out sending a {Severity} notification to the {Destination}: {Title}",
                notification.Severity, destination, notification.Title);
            return new Outcome(false, null, null);
        }
        catch (HttpRequestException error)
        {
            // Only the exception TYPE is logged: its message or stack can carry the request URI, and the webhook
            // URL is a credential. Never rethrow into the flatten path.
            _logger.LogWarning(
                "Could not deliver a {Severity} notification to the Discord {Destination} ({ErrorType}): {Title}",
                notification.Severity, destination, error.GetType().Name, notification.Title);
            return new Outcome(false, null, null);
        }
    }

    private readonly record struct Outcome(bool Ok, HttpStatusCode? Status, string? Body);
}
