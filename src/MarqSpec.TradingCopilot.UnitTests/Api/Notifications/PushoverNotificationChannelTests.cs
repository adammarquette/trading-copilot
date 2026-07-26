using System.Net;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// The Pushover adapter behind the <see cref="INotificationChannel"/> seam (gh#243, ADR-0019) — Layer 1 of the
/// alerting model: the push the app sends itself, because it knows first.
/// </summary>
/// <remarks>
/// <para>
/// The severity mapping is the load-bearing part. A <see cref="NotificationSeverity.Page"/> must arrive as
/// Pushover <b>Emergency</b> (priority 2), which is the only priority that repeats until <i>acknowledged</i> and
/// bypasses Do Not Disturb. A P1 delivered as an ordinary notification has silently failed at the one job the
/// channel exists for — waking someone who is away from the desk.
/// </para>
/// <para>
/// And it must never throw or hang into its caller: this sits beside the auto-flatten, and alerting that breaks
/// trading is the self-inflicted wound ADR-0019 explicitly rules out.
/// </para>
/// </remarks>
public class PushoverNotificationChannelTests
{
    private const string Token = "app-token-secret";
    private const string UserKey = "user-key-secret";

    private static PushoverOptions Configured() => new() { AppToken = Token, UserKey = UserKey };

    private static PushoverNotificationChannel Channel(PushoverOptions options, StubHandler handler, ILogger<PushoverNotificationChannel>? logger = null) =>
        new(new HttpClient(handler), Options.Create(options), logger ?? NullLogger<PushoverNotificationChannel>.Instance);

    private static Notification Note(NotificationSeverity severity = NotificationSeverity.Page) =>
        new(severity, "Auto-flatten escalated", "ES still exposed after 3 close attempts.", "flatten:9001:ES");

    // --- Severity mapping ---

    [Fact]
    public async Task SendAsync_ShouldUseEmergencyPriority_WhenSeverityIsPage()
    {
        StubHandler handler = new();

        await Channel(Configured(), handler).SendAsync(Note(NotificationSeverity.Page), CancellationToken.None);

        handler.LastForm!["priority"].Should().Be("2");
        handler.LastForm.Should().ContainKey("retry", "an Emergency page repeats until acknowledged");
        handler.LastForm.Should().ContainKey("expire");
    }

    [Fact]
    public async Task SendAsync_ShouldUseNormalPriority_WhenSeverityIsNotify()
    {
        StubHandler handler = new();

        await Channel(Configured(), handler).SendAsync(Note(NotificationSeverity.Notify), CancellationToken.None);

        handler.LastForm!["priority"].Should().Be("0");
        handler.LastForm.Should().NotContainKey("retry", "only an Emergency page nags");
    }

    [Fact]
    public async Task SendAsync_ShouldUseQuietPriority_WhenSeverityIsQuiet()
    {
        // The flatten CONFIRMATION PRD P1 asks for: delivered, no sound. Anything louder trains the operator to
        // mute the channel, and a muted pager is worse than none.
        StubHandler handler = new();

        await Channel(Configured(), handler).SendAsync(Note(NotificationSeverity.Quiet), CancellationToken.None);

        handler.LastForm!["priority"].Should().Be("-1");
    }

    [Fact]
    public async Task SendAsync_ShouldCarryTheTitleAndBody()
    {
        StubHandler handler = new();

        await Channel(Configured(), handler).SendAsync(Note(), CancellationToken.None);

        handler.LastForm!["title"].Should().Be("Auto-flatten escalated");
        handler.LastForm["message"].Should().Contain("ES still exposed");
    }

    // --- Unconfigured is allowed, but inert ---

    [Fact]
    public async Task SendAsync_ShouldNotSend_WhenTheChannelIsUnconfigured()
    {
        StubHandler handler = new();

        bool sent = await Channel(new PushoverOptions(), handler).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    // --- It cannot break trading ---

    [Fact]
    public async Task SendAsync_ShouldReturnFalseRatherThanThrow_WhenTheRequestFaults()
    {
        StubHandler handler = new() { Throw = new HttpRequestException("no route") };

        bool sent = await Channel(Configured(), handler).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFalseRatherThanThrow_WhenTheRequestTimesOut()
    {
        StubHandler handler = new() { Throw = new TaskCanceledException("timeout") };

        bool sent = await Channel(Configured(), handler).SendAsync(Note(), CancellationToken.None);

        sent.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        // Swallowing a timeout must not also swallow host shutdown, or the loop cannot exit.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        StubHandler handler = new() { Throw = new TaskCanceledException("stopping") };

        Func<Task> act = () => Channel(Configured(), handler).SendAsync(Note(), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFalse_WhenPushoverRejectsTheRequest()
    {
        StubHandler handler = new() { Status = HttpStatusCode.BadRequest };

        (await Channel(Configured(), handler).SendAsync(Note(), CancellationToken.None)).Should().BeFalse();
    }

    // --- Credentials are secrets ---

    [Fact]
    public async Task SendAsync_ShouldKeepCredentialsOutOfTheLog_WhenSendingFails()
    {
        CapturingLogger logger = new();

        await Channel(Configured(), new StubHandler { Throw = new HttpRequestException("boom") }, logger)
            .SendAsync(Note(), CancellationToken.None);

        string logged = string.Join("\n", logger.Messages);
        logged.Should().NotContain(Token);
        logged.Should().NotContain(UserKey);
    }

    // --- Resolving an outstanding page ---

    [Fact]
    public async Task ResolveAsync_ShouldCancelTheOutstandingPage_WhenTheConditionClears()
    {
        // An Emergency page nags until acknowledged. When the watchdog saves the flatten, the operator must not
        // still be woken for a condition that no longer exists.
        StubHandler handler = new() { ReceiptJson = """{"status":1,"receipt":"rcpt-123"}""" };
        PushoverNotificationChannel channel = Channel(Configured(), handler);
        await channel.SendAsync(Note(NotificationSeverity.Page), CancellationToken.None);

        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].RequestUri!.ToString().Should().Contain("rcpt-123");
    }

    [Fact]
    public async Task ResolveAsync_ShouldDoNothing_WhenNoPageIsOutstandingForThatKey()
    {
        StubHandler handler = new();
        PushoverNotificationChannel channel = Channel(Configured(), handler);

        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotThrow_WhenTheCancelRequestFaults()
    {
        StubHandler handler = new() { ReceiptJson = """{"status":1,"receipt":"rcpt-123"}""" };
        PushoverNotificationChannel channel = Channel(Configured(), handler);
        await channel.SendAsync(Note(NotificationSeverity.Page), CancellationToken.None);
        handler.Throw = new HttpRequestException("boom");

        Func<Task> act = () => channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // --- Doubles ---

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Dictionary<string, string>? LastForm { get; private set; }

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public string ReceiptJson { get; init; } = """{"status":1}""";

        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);

            if (request.Content is FormUrlEncodedContent form)
            {
                string body = await form.ReadAsStringAsync(cancellationToken);
                // form-urlencoded uses '+' for space, which UnescapeDataString does not decode.
                LastForm = body.Split('&')
                    .Select(pair => pair.Split('=', 2))
                    .ToDictionary(
                        parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                        parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')));
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            return new HttpResponseMessage(Status) { Content = new StringContent(ReceiptJson) };
        }
    }

    private sealed class CapturingLogger : ILogger<PushoverNotificationChannel>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, error));
        }
    }
}
