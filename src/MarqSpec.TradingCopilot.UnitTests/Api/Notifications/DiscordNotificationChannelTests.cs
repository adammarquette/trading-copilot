using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// The Discord adapter behind the <see cref="INotificationChannel"/> seam (gh#1157, ADR-0019) — a second
/// transport beside Pushover, decision-support only: it posts the advisory the seam already emits and returns.
/// </summary>
/// <remarks>
/// Every value below is an obviously fake placeholder; the tests assert the <b>exact</b> request the adapter
/// puts on the wire (URL, auth header, JSON body), because a test that only counted calls could not tell a
/// correct payload from a broken one.
/// </remarks>
public class DiscordNotificationChannelTests
{
    private const string WebhookToken = "fake-webhook-token-do-not-use";
    private const string WebhookUrl = "https://discord.com/api/webhooks/111111111111111111/" + WebhookToken;
    private const string BotToken = "fake-bot-token-do-not-use";
    private const string OperatorId = "222222222222222222";
    private const string DmChannelId = "333333333333333333";

    private static DiscordOptions WebhookOnly() => new() { WebhookUrl = WebhookUrl };

    private static DiscordOptions DmOnly() => new() { BotToken = BotToken, OperatorUserId = OperatorId };

    private static DiscordOptions Both() =>
        new() { WebhookUrl = WebhookUrl, BotToken = BotToken, OperatorUserId = OperatorId };

    private static DiscordNotificationChannel Channel(
        DiscordOptions options, FakeDiscord handler, CapturingLogger? logger = null) =>
        new(new HttpClient(handler), Options.Create(options), logger ?? new CapturingLogger());

    private static Notification Note(NotificationSeverity severity = NotificationSeverity.Notify) =>
        new(severity, "Reviewed setup available", "ES long setup passed review. Open the app to decide.", "suggestion:ES:1");

    // --- Webhook (channel) path ---

    [Fact]
    public async Task SendAsync_ShouldPostOneEmbedToTheWebhook_WhenOnlyAWebhookIsConfigured()
    {
        FakeDiscord handler = new();

        bool sent = await Channel(WebhookOnly(), handler).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        FakeDiscord.Seen request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(WebhookUrl);
        request.Authorization.Should().BeNull("a webhook URL is its own credential; a bot token must not ride along");
        JsonElement embed = request.Json.GetProperty("embeds").EnumerateArray().Single();
        embed.GetProperty("title").GetString().Should().Be("Reviewed setup available");
        embed.GetProperty("description").GetString().Should().Be("ES long setup passed review. Open the app to decide.");
        embed.GetProperty("footer").GetProperty("text").GetString().Should().Be("Notify");
    }

    [Theory]
    [InlineData(NotificationSeverity.Page, 15158332, "Page", false)]
    [InlineData(NotificationSeverity.Notify, 15105570, "Notify", false)]
    [InlineData(NotificationSeverity.Quiet, 9807270, "Quiet", true)]
    public async Task SendAsync_ShouldRenderSeverityWithoutReinterpretingIt_WhenPostingToTheWebhook(
        NotificationSeverity severity, int color, string label, bool suppressed)
    {
        // Severity is rendered (colour, footer), never reinterpreted: Discord is an advisory transport, so a
        // Page does NOT gain a mention or a repeat here, and a Quiet notification is delivered without a ping.
        FakeDiscord handler = new();

        await Channel(WebhookOnly(), handler).SendAsync(Note(severity), CancellationToken.None);

        JsonElement body = handler.Requests.Single().Json;
        JsonElement embed = body.GetProperty("embeds").EnumerateArray().Single();
        embed.GetProperty("color").GetInt32().Should().Be(color);
        embed.GetProperty("footer").GetProperty("text").GetString().Should().Be(label);
        if (suppressed)
        {
            body.GetProperty("flags").GetInt32().Should().Be(4096, "SUPPRESS_NOTIFICATIONS: delivered without a push");
        }
        else
        {
            body.TryGetProperty("flags", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task SendAsync_ShouldDisableAllMentions_SoTextInTheBodyCannotPingAnyone()
    {
        FakeDiscord handler = new();
        Notification hostile = new(NotificationSeverity.Notify, "@everyone", "hello <@&444444444444444444> @here", "k");

        await Channel(WebhookOnly(), handler).SendAsync(hostile, CancellationToken.None);

        JsonElement mentions = handler.Requests.Single().Json.GetProperty("allowed_mentions");
        mentions.GetProperty("parse").GetArrayLength().Should().Be(0);
        mentions.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["parse"]);
    }

    [Fact]
    public async Task SendAsync_ShouldTruncateToDiscordsLimits_WhenTheTextIsTooLong()
    {
        FakeDiscord handler = new();
        Notification longNote = new(NotificationSeverity.Notify, new string('t', 300), new string('b', 5000), "k");

        await Channel(WebhookOnly(), handler).SendAsync(longNote, CancellationToken.None);

        JsonElement embed = handler.Requests.Single().Json.GetProperty("embeds").EnumerateArray().Single();
        embed.GetProperty("title").GetString().Should().HaveLength(256).And.EndWith("…");
        embed.GetProperty("description").GetString().Should().HaveLength(4000).And.EndWith("…");
    }

    // --- Direct-message path ---

    [Fact]
    public async Task SendAsync_ShouldOpenTheDmChannelThenPostIntoIt_WhenOnlyABotTokenAndOperatorAreConfigured()
    {
        FakeDiscord handler = new() { DmChannelId = DmChannelId };

        bool sent = await Channel(DmOnly(), handler).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);

        FakeDiscord.Seen open = handler.Requests[0];
        open.Method.Should().Be(HttpMethod.Post);
        open.Uri.Should().Be("https://discord.com/api/v10/users/@me/channels");
        open.Authorization.Should().Be("Bot " + BotToken);
        open.Json.GetProperty("recipient_id").GetString().Should().Be(OperatorId);
        open.Json.EnumerateObject().Should().ContainSingle("the DM is opened to the pinned operator and no one else");

        FakeDiscord.Seen post = handler.Requests[1];
        post.Method.Should().Be(HttpMethod.Post);
        post.Uri.Should().Be($"https://discord.com/api/v10/channels/{DmChannelId}/messages");
        post.Authorization.Should().Be("Bot " + BotToken);
        post.Json.GetProperty("embeds").EnumerateArray().Single().GetProperty("title").GetString()
            .Should().Be("Reviewed setup available");
        post.Json.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_ShouldReuseTheDmChannel_OnTheSecondSend()
    {
        FakeDiscord handler = new() { DmChannelId = DmChannelId };
        DiscordNotificationChannel channel = Channel(DmOnly(), handler);

        await channel.SendAsync(Note(), CancellationToken.None);
        await channel.SendAsync(Note(), CancellationToken.None);

        handler.Requests.Select(r => r.Uri).Should().Equal(
            "https://discord.com/api/v10/users/@me/channels",
            $"https://discord.com/api/v10/channels/{DmChannelId}/messages",
            $"https://discord.com/api/v10/channels/{DmChannelId}/messages");
    }

    [Fact]
    public async Task SendAsync_ShouldReopenTheDmChannel_WhenDiscordRefusesTheCachedOne()
    {
        // A revoked / deleted DM channel answers 403/404 forever; holding it would wedge the transport.
        FakeDiscord handler = new() { DmChannelId = DmChannelId };
        DiscordNotificationChannel channel = Channel(DmOnly(), handler);
        await channel.SendAsync(Note(), CancellationToken.None);
        handler.MessageStatus = HttpStatusCode.NotFound;

        bool refused = await channel.SendAsync(Note(), CancellationToken.None);
        handler.MessageStatus = HttpStatusCode.OK;
        bool recovered = await channel.SendAsync(Note(), CancellationToken.None);

        refused.Should().BeFalse();
        recovered.Should().BeTrue();
        handler.Requests.Count(r => r.Uri.EndsWith("/users/@me/channels", StringComparison.Ordinal))
            .Should().Be(2, "the cache is dropped on 404 so the next send opens a fresh DM");
    }

    [Fact]
    public async Task SendAsync_ShouldNotPostAnywhere_WhenDiscordReturnsANonNumericDmChannelId()
    {
        // The id is interpolated into a URL path. Anything that is not a snowflake is refused, never interpolated.
        FakeDiscord handler = new() { DmChannelId = "../../guilds/1" };

        bool sent = await Channel(DmOnly(), handler).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeFalse();
        handler.Requests.Should().ContainSingle("only the open call; no message is posted to a path built from junk");
    }

    // --- Both, and neither ---

    [Fact]
    public async Task SendAsync_ShouldDeliverToBothDestinations_WhenBothAreConfigured()
    {
        FakeDiscord handler = new() { DmChannelId = DmChannelId };

        bool sent = await Channel(Both(), handler).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeTrue();
        handler.Requests.Select(r => r.Uri).Should().BeEquivalentTo(
            WebhookUrl,
            "https://discord.com/api/v10/users/@me/channels",
            $"https://discord.com/api/v10/channels/{DmChannelId}/messages");
    }

    [Fact]
    public async Task SendAsync_ShouldReportAccepted_WhenOneDestinationTakesItAndTheOtherFails()
    {
        // "Accepted" means the operator can see it somewhere. Failing the whole send would make the relay retry
        // and re-post into the destination that already worked.
        FakeDiscord handler = new() { DmChannelId = DmChannelId, WebhookStatus = HttpStatusCode.InternalServerError };

        bool sent = await Channel(Both(), handler).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFalse_WhenBothDestinationsFail()
    {
        FakeDiscord handler = new()
        {
            DmChannelId = DmChannelId,
            WebhookStatus = HttpStatusCode.InternalServerError,
            MessageStatus = HttpStatusCode.InternalServerError,
        };

        (await Channel(Both(), handler).SendAsync(Note(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_ShouldNotSend_WhenNothingIsConfigured()
    {
        FakeDiscord handler = new();

        bool sent = await Channel(new DiscordOptions(), handler).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldNotSend_WhenOnlyHalfOfTheDmPairIsConfigured()
    {
        FakeDiscord handler = new();

        bool sent = await Channel(new DiscordOptions { BotToken = BotToken }, handler)
            .SendAsync(Note(), CancellationToken.None);

        sent.Should().BeFalse();
        handler.Requests.Should().BeEmpty("a bot token with no pinned recipient must never guess one");
    }

    // --- Rate limit is legible, and is not an outage ---

    [Fact]
    public async Task SendAsync_ShouldLogRateLimitingWithRetryAfter_WhenTheWebhookAnswers429()
    {
        CapturingLogger logger = new();
        FakeDiscord handler = new() { WebhookStatus = HttpStatusCode.TooManyRequests, RetryAfterSeconds = 7 };

        bool sent = await Channel(WebhookOnly(), handler, logger).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeFalse("nothing was delivered, so dedup must not record the incident as told");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Message.Should().Contain("rate-limited").And.Contain("7");
        logger.Entries[0].Message.Should().NotContainEquivalentOf("unreachable").And.NotContainEquivalentOf("outage");
    }

    [Fact]
    public async Task SendAsync_ShouldLogRateLimiting_WhenOpeningTheDmChannelAnswers429()
    {
        CapturingLogger logger = new();
        FakeDiscord handler = new() { OpenStatus = HttpStatusCode.TooManyRequests, RetryAfterSeconds = 3 };

        bool sent = await Channel(DmOnly(), handler, logger).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
        logger.Entries.Should().ContainSingle().Which.Message.Should().Contain("rate-limited").And.Contain("3");
    }

    [Fact]
    public async Task SendAsync_ShouldLogAPlainRejection_NotRateLimiting_WhenDiscordAnswers500()
    {
        // The two must be distinguishable in a log: a 429 says slow down, a 5xx says Discord is unwell.
        CapturingLogger logger = new();
        FakeDiscord handler = new() { WebhookStatus = HttpStatusCode.InternalServerError };

        await Channel(WebhookOnly(), handler, logger).SendAsync(Note(), CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Message.Should().Contain("500").And.NotContain("rate-limited");
    }

    // --- It cannot break trading ---

    [Fact]
    public async Task SendAsync_ShouldReturnFalseRatherThanThrow_WhenTheRequestFaults()
    {
        FakeDiscord handler = new() { Throw = new HttpRequestException("no route") };

        (await Channel(WebhookOnly(), handler).SendAsync(Note(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFalseRatherThanThrow_WhenTheRequestTimesOut()
    {
        FakeDiscord handler = new() { Throw = new TaskCanceledException("timeout") };

        (await Channel(WebhookOnly(), handler).SendAsync(Note(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        FakeDiscord handler = new() { Throw = new TaskCanceledException("stopping") };

        Func<Task> act = () => Channel(WebhookOnly(), handler).SendAsync(Note(), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SendAsync_ShouldNeverWriteACredentialToTheLog_OnAnyFailurePath()
    {
        // The webhook URL carries its token in the path, so it is as secret as the bot token. Exercise every
        // failure branch and read back everything the logger saw, exception text included.
        FakeDiscord[] scenarios =
        [
            new() { WebhookStatus = HttpStatusCode.TooManyRequests, RetryAfterSeconds = 1, DmChannelId = DmChannelId, OpenStatus = HttpStatusCode.TooManyRequests },
            new() { WebhookStatus = HttpStatusCode.InternalServerError, DmChannelId = DmChannelId, MessageStatus = HttpStatusCode.Forbidden },
            new() { Throw = new HttpRequestException($"failed calling {WebhookUrl} as Bot {BotToken}") },
            new() { Throw = new TaskCanceledException("timeout") },
            new() { DmChannelId = "not-a-snowflake" },
        ];

        foreach (FakeDiscord scenario in scenarios)
        {
            CapturingLogger logger = new();
            await Channel(Both(), scenario, logger).SendAsync(Note(), CancellationToken.None);

            string everything = string.Join('\n', logger.Entries.Select(e => e.Message + e.ExceptionText));
            everything.Should().NotContain(WebhookToken).And.NotContain(BotToken);
            everything.Should().NotContain("/api/webhooks/");
        }
    }

    // --- Resolve ---

    [Fact]
    public async Task ResolveAsync_ShouldReturnTrueWithoutCallingDiscord_BecauseNothingIsOutstanding()
    {
        // An advisory has no receipt to cancel (unlike a Pushover Emergency page). "Nothing to cancel" is
        // definitively closed, not failed, or a retrying caller would loop over nothing (gh#300).
        FakeDiscord handler = new();

        bool closed = await Channel(Both(), handler).ResolveAsync("suggestion:ES:1", CancellationToken.None);

        closed.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
    }

    /// <summary>A scripted Discord that records exactly what reached the wire.</summary>
    private sealed class FakeDiscord : HttpMessageHandler
    {
        public sealed record Seen(HttpMethod Method, string Uri, string? Authorization, JsonElement Json);

        public List<Seen> Requests { get; } = [];

        public HttpStatusCode WebhookStatus { get; set; } = HttpStatusCode.NoContent;

        public HttpStatusCode OpenStatus { get; set; } = HttpStatusCode.OK;

        public HttpStatusCode MessageStatus { get; set; } = HttpStatusCode.OK;

        public string DmChannelId { get; set; } = "333333333333333333";

        public int? RetryAfterSeconds { get; set; }

        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                JsonDocument.Parse(body).RootElement.Clone()));

            if (Throw is not null)
            {
                throw Throw;
            }

            string path = request.RequestUri.AbsolutePath;
            bool isOpen = path.EndsWith("/users/@me/channels", StringComparison.Ordinal);
            HttpStatusCode status = path.StartsWith("/api/webhooks/", StringComparison.Ordinal)
                ? WebhookStatus
                : isOpen ? OpenStatus : MessageStatus;

            HttpResponseMessage response = new(status)
            {
                Content = new StringContent(isOpen ? JsonSerializer.Serialize(new { id = DmChannelId }) : "{}"),
            };

            if (status == HttpStatusCode.TooManyRequests && RetryAfterSeconds is int seconds)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }
    }

    private sealed class CapturingLogger : ILogger<DiscordNotificationChannel>
    {
        public List<(LogLevel Level, string Message, string ExceptionText)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((level, formatter(state, error), error?.ToString() ?? string.Empty));
        }
    }
}
