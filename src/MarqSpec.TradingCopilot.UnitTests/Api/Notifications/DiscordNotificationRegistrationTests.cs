using System.Net;
using System.Reflection;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// How the Discord transport is <b>bound</b> into the notification chain (gh#1157, ADR-0019). The adapter's own
/// behaviour is in <see cref="DiscordNotificationChannelTests"/>; what this asserts is the composition root — that
/// a keyless host registers <b>no</b> Discord channel and says so, that a keyed one is present in the same
/// outbox → queue → dedup chain Pushover uses (not a parallel path), and that dedup still collapses a repeat.
/// </summary>
/// <remarks>
/// Asserted against the <b>real</b> <c>AddTradingCopilotNotifications</c>, as the sibling registration suite
/// argues (gh#320): a hand-built chain would pass while production drifted. All credentials are fake.
/// </remarks>
public class DiscordNotificationRegistrationTests
{
    private const string WebhookUrl = "https://discord.com/api/webhooks/111111111111111111/fake-webhook-token-do-not-use";
    private const string BotToken = "fake-bot-token-do-not-use";
    private const string OperatorId = "222222222222222222";

    private static readonly Dictionary<string, string?> WebhookConfig = new() { ["Discord:WebhookUrl"] = WebhookUrl };

    private static readonly Dictionary<string, string?> PushoverConfig = new()
    {
        ["Pushover:AppToken"] = "fake-pushover-app-token",
        ["Pushover:UserKey"] = "fake-pushover-user-key",
    };

    // --- Keyless: absent, and says so ---

    [Fact]
    public void AddTradingCopilotNotifications_ShouldRegisterNoDiscordChannel_WhenTheHostIsKeyless()
    {
        WebApplicationBuilder builder = Builder(config: []);

        builder.AddTradingCopilotNotifications();

        builder.Services.Any(d => d.ServiceType == typeof(DiscordNotificationChannel)).Should().BeFalse(
            "a keyless host must not carry a Discord channel it can never use, nor fall back to a credential");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldLeaveTheKeylessChainOnTheNullTransport_ForAHostWithNoChannelAtAll()
    {
        using WebApplication app = Compose(config: []);

        object transport = TransportBeneathDedup(app.Services.GetRequiredService<QueuedNotificationChannel>());

        transport.Should().BeOfType<NullNotificationChannel>("with no transport configured the chain logs instead of sending");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldLogThatDiscordIsAbsent_WhenTheHostIsKeyless()
    {
        CapturingLoggerProvider logs = new();
        using WebApplication app = Compose(config: [], logs: logs);

        _ = app.Services.GetRequiredService<QueuedNotificationChannel>();

        logs.Entries.Should().Contain(e =>
            e.Message.Contains("Discord", StringComparison.Ordinal)
            && e.Message.Contains("not configured", StringComparison.Ordinal)
            && e.Level == LogLevel.Information,
            "running without Discord is allowed, running without Discord silently is not");
    }

    // --- Keyed: present, in the same chain ---

    [Fact]
    public void AddTradingCopilotNotifications_ShouldRegisterTheDiscordChannel_WhenAWebhookIsSet()
    {
        WebApplicationBuilder builder = Builder(WebhookConfig);

        builder.AddTradingCopilotNotifications();

        builder.Services.Any(d => d.ServiceType == typeof(DiscordNotificationChannel)).Should().BeTrue();
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldRegisterTheDiscordChannel_WhenTheBotTokenAndOperatorAreSet()
    {
        WebApplicationBuilder builder = Builder(new() { ["Discord:BotToken"] = BotToken, ["Discord:OperatorUserId"] = OperatorId });

        builder.AddTradingCopilotNotifications();

        builder.Services.Any(d => d.ServiceType == typeof(DiscordNotificationChannel)).Should().BeTrue();
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldPutDiscordBeneathDedup_WhenItIsTheOnlyTransport()
    {
        using WebApplication app = Compose(WebhookConfig);

        TransportBeneathDedup(app.Services.GetRequiredService<QueuedNotificationChannel>())
            .Should().BeOfType<DiscordNotificationChannel>("Discord rides the SAME queue -> dedup chain, not a parallel path");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldStillBeBoundToTheOutbox_WhenDiscordIsKeyed()
    {
        using WebApplication app = Compose(WebhookConfig);
        using IServiceScope scope = app.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<INotificationChannel>().Should().BeOfType<OutboxNotificationChannel>(
            "adding a transport must not move the seam the auto-flatten awaits (gh#437)");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldFanOutToPushoverThenDiscord_WhenBothAreKeyed()
    {
        using WebApplication app = Compose(Merge(PushoverConfig, WebhookConfig));
        QueuedNotificationChannel queue = app.Services.GetRequiredService<QueuedNotificationChannel>();

        FanOutNotificationChannel fanOut = InnerOf(queue).Should().BeOfType<FanOutNotificationChannel>().Subject;

        Lanes(fanOut).Select(lane => Field(lane, "_inner").GetType()).Should().Equal(
            typeof(PushoverNotificationChannel), typeof(DiscordNotificationChannel));
        Field(queue, "_incidents").Should().BeSameAs(fanOut,
            "the queue's out-of-band key release must reach the SAME lanes the suppression reads (gh#1077)");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldLeavePushoverAloneBeneathDedup_WhenDiscordIsKeyless()
    {
        using WebApplication app = Compose(PushoverConfig);

        TransportBeneathDedup(app.Services.GetRequiredService<QueuedNotificationChannel>())
            .Should().BeOfType<PushoverNotificationChannel>("Discord being absent must not change the existing chain");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldLogThatDiscordIsEnabled_WithoutAnyCredential()
    {
        CapturingLoggerProvider logs = new();
        using WebApplication app = Compose(new() { ["Discord:WebhookUrl"] = WebhookUrl, ["Discord:BotToken"] = BotToken, ["Discord:OperatorUserId"] = OperatorId }, logs: logs);

        _ = app.Services.GetRequiredService<QueuedNotificationChannel>();

        logs.Entries.Should().Contain(e => e.Message.Contains("Discord", StringComparison.Ordinal)
            && e.Message.Contains("enabled", StringComparison.Ordinal));
        string everything = string.Join('\n', logs.Entries.Select(e => e.Message));
        everything.Should().NotContain("fake-webhook-token").And.NotContain(BotToken).And.NotContain(OperatorId);
    }

    // --- Dedup across both transports, through the real wiring ---

    [Fact]
    public async Task TheComposedChain_ShouldCollapseARepeatedIncidentKeyAcrossBothTransports_UntilItIsResolved()
    {
        CountingHandler pushover = new(HttpStatusCode.OK, """{"status":1}""");
        CountingHandler discord = new(HttpStatusCode.NoContent, string.Empty);
        using WebApplication app = Compose(Merge(PushoverConfig, WebhookConfig), configure: services =>
        {
            services.AddHttpClient<PushoverNotificationChannel>().ConfigurePrimaryHttpMessageHandler(() => pushover);
            services.AddHttpClient<DiscordNotificationChannel>().ConfigurePrimaryHttpMessageHandler(() => discord);
        });
        INotificationChannel chain = (INotificationChannel)InnerOf(app.Services.GetRequiredService<QueuedNotificationChannel>());
        Notification note = new(NotificationSeverity.Notify, "Reviewed setup available", "ES long setup.", "suggestion:ES:1");

        bool first = await chain.SendAsync(note, CancellationToken.None);
        bool repeat = await chain.SendAsync(note, CancellationToken.None);

        first.Should().BeTrue();
        repeat.Should().BeFalse();
        pushover.Calls.Should().Be(1);
        discord.Calls.Should().Be(1);

        await chain.ResolveAsync(note.DedupKey, CancellationToken.None);
        bool recurrence = await chain.SendAsync(note, CancellationToken.None);

        recurrence.Should().BeTrue("resolving re-arms the key on both transports");
        pushover.Calls.Should().Be(2);
        discord.Calls.Should().Be(2);
    }

    // --- Options: validated on start, no secret in the default HTTP logging ---

    [Fact]
    public void AddTradingCopilotNotifications_ShouldFailStartupValidation_WhenTheBotTokenHasNoOperator()
    {
        using WebApplication app = Compose(new() { ["Discord:BotToken"] = BotToken });

        Action validate = () => app.Services.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Discord:OperatorUserId");
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldPassStartupValidation_WhenKeylessOrFullyKeyed()
    {
        using WebApplication keyless = Compose(config: []);
        using WebApplication keyed = Compose(WebhookConfig);

        keyless.Services.GetRequiredService<IStartupValidator>().Validate();
        keyed.Services.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void AddTradingCopilotNotifications_ShouldSuppressTheDefaultHttpClientLogging_ForTheDiscordClient()
    {
        // The webhook token lives in the request PATH, and the default HttpClient logging writes the full
        // request URI at Information -- which would put the credential in the log.
        using WebApplication app = Compose(Merge(PushoverConfig, WebhookConfig));
        IHttpMessageHandlerFactory handlers = app.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        // The Pushover client is the control: its chain DOES carry the logging handlers, so this can fail.
        HandlerTypes(handlers.CreateHandler(nameof(PushoverNotificationChannel)))
            .Should().Contain(name => name.StartsWith("Logging", StringComparison.Ordinal));
        HandlerTypes(handlers.CreateHandler(nameof(DiscordNotificationChannel)))
            .Should().NotContain(name => name.StartsWith("Logging", StringComparison.Ordinal),
                "the request URI carries the webhook token and must never be logged");
    }

    private static List<string> HandlerTypes(HttpMessageHandler handler)
    {
        List<string> names = [];
        for (HttpMessageHandler? current = handler; current is not null;
            current = (current as DelegatingHandler)?.InnerHandler)
        {
            names.Add(current.GetType().Name);
        }

        return names;
    }

    // --- Harness ---

    private static Dictionary<string, string?> Merge(params Dictionary<string, string?>[] parts) =>
        parts.SelectMany(p => p).ToDictionary(p => p.Key, p => p.Value);

    private static WebApplicationBuilder Builder(Dictionary<string, string?> config, CapturingLoggerProvider? logs = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddScoped<ICurrentUser>(_ => new FixedUser(Guid.Empty));
        builder.Services.AddDbContext<TradingCopilotDbContext>(
            options => options.UseInMemoryDatabase($"discord-registration-{Guid.NewGuid()}"));
        builder.Services.AddSingleton<IExecutionMetrics>(NullExecutionMetrics.Instance);
        if (logs is not null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
        }

        return builder;
    }

    private static WebApplication Compose(
        Dictionary<string, string?> config,
        CapturingLoggerProvider? logs = null,
        Action<IServiceCollection>? configure = null)
    {
        WebApplicationBuilder builder = Builder(config, logs);
        builder.AddTradingCopilotNotifications();
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    private static object InnerOf(QueuedNotificationChannel queue) => Field(queue, "_inner");

    private static object TransportBeneathDedup(QueuedNotificationChannel queue)
    {
        object dedup = InnerOf(queue);
        dedup.Should().BeOfType<DedupingNotificationChannel>();
        return Field(dedup, "_inner");
    }

    private static IReadOnlyList<object> Lanes(FanOutNotificationChannel fanOut) =>
        ((IEnumerable<object>)Field(fanOut, "_lanes")).ToList();

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Sink(Entries);

        public void Dispose()
        {
        }

        private sealed class Sink(List<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                lock (entries)
                {
                    entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
