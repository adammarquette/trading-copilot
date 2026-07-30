using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The shared notification harness (gh#442). It substitutes the <b>transport only</b> — the socket beneath
/// <c>PushoverNotificationChannel</c> — so <c>outbox → queue → dedup</c> and the relay stay exactly what
/// <c>AddTradingCopilotNotifications</c> composed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> Two hosts had independently copied a block that removed every
/// <see cref="INotificationChannel"/> registration and rebuilt the pre-outbox chain. Once gh#437 made the seam
/// durable, that block silently deleted the outbox from the composition under test: nothing failed, and durability
/// simply stopped being covered (the gh#382 shape). A copy-pasted harness diverges once per copy, so the fix is a
/// single shared base rather than two corrected copies.
/// </para>
/// <para>
/// <b>Three guard layers, none of which a future harness can opt out of.</b> (1) <see cref="ConfigureTestServices"/>
/// is <c>sealed</c> — suites get <see cref="ConfigureSuiteServices"/>, and a descriptor count around that hook
/// throws if a suite adds or removes any notification-chain registration. (2)
/// <see cref="AssertProductionChainIntact"/> resolves the seam from a real scope and checks its concrete type — the
/// descriptor cannot be inspected because production registers it with a factory. (3) a named fidelity test states
/// the invariant in CI's language rather than as a constructor throw.
/// </para>
/// <para>
/// Substituting at the wire also exercises the <c>Pushover:IsConfigured == true</c> branch, which no integration
/// test covered before: the real adapter and its severity→priority mapping are now production objects under test.
/// </para>
/// </remarks>
public abstract class NotificationHarnessPostgresFactory : StubbedVenuePostgresFactory
{
    private int _chainAsserted;

    /// <summary>The fake <c>api.pushover.net</c> every notification ultimately lands on.</summary>
    public RecordingPushoverTransport Pushover { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Fabricated, never secrets. Their PRESENCE is what makes production's own factory bind the real
        // PushoverNotificationChannel instead of NullNotificationChannel, putting the adapter under test.
        builder.UseSetting("Pushover:AppToken", "integration-test-app-token-not-a-secret");
        builder.UseSetting("Pushover:UserKey", "integration-test-user-key-not-a-secret");
        ConfigureSuiteSettings(builder);
    }

    /// <summary>Suite-specific host settings. Never notification settings.</summary>
    protected virtual void ConfigureSuiteSettings(IWebHostBuilder builder)
    {
    }

    /// <summary>
    /// Suite-specific doubles — the venue stub, an <c>ILlmProvider</c>, and so on. <b>Never the notification
    /// chain</b>: the surrounding descriptor check throws if this hook touches it.
    /// </summary>
    protected virtual void ConfigureSuiteServices(IServiceCollection services)
    {
    }

    /// <summary>
    /// Sealed on purpose (gh#442 guard layer 1): a future harness cannot reintroduce the copy-pasted block in the
    /// override that matters. Suites extend <see cref="ConfigureSuiteServices"/> instead.
    /// </summary>
    protected sealed override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        int before = CountNotificationChainDescriptors(services);
        ConfigureSuiteServices(services);
        int after = CountNotificationChainDescriptors(services);
        if (after != before)
        {
            throw new InvalidOperationException(
                "gh#442: a suite added or removed a notification-chain descriptor. The double stands where PUSHOVER "
                + "stands, never where the chain stands — substitute the transport, not the seam.");
        }

        // ADDITIVE: AddHttpClient<T>() reuses the client name production registered, so this replaces the primary
        // handler on production's own registration rather than replacing the registration itself. A fresh handler
        // per creation writing into one shared recorder — the factory owns and disposes handlers.
        services.AddHttpClient<PushoverNotificationChannel>()
            .ConfigurePrimaryHttpMessageHandler(() => new FakePushoverHandler(Pushover))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        // Determinism: the only relay/pump pass is the test's own explicit one.
        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }
    }

    /// <summary>Drives exactly one production outbox relay pass; returns how many rows it stamped.</summary>
    public async Task<int> RelayOutboxAsync(CancellationToken cancellationToken = default)
    {
        AssertProductionChainIntact();
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<NotificationOutboxRelay>()
            .DrainAsync(cancellationToken);
    }

    /// <summary>Flushes what the relay handed to the queue — nothing pumps it with the hosts stripped.</summary>
    public Task<int> DrainNotificationsAsync() =>
        Services.GetRequiredService<QueuedNotificationChannel>().DrainPendingAsync(CancellationToken.None);

    /// <summary>The full production delivery path: persist → relay → queue → dedup → wire.</summary>
    public async Task DeliverOutboxAsync()
    {
        await RelayOutboxAsync();
        await DrainNotificationsAsync();
    }

    /// <summary>Reads through a FRESH scope, so an assertion sees what was committed, not what a context tracks.</summary>
    public async Task<T> WithDatabaseAsync<T>(Func<TradingCopilotDbContext, Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }

    /// <summary>Clears the outbox — <c>DedupKey</c> is the primary key, so rows leak across tests otherwise.</summary>
    public async Task ClearOutboxAsync()
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.NotificationOutbox.ExecuteDeleteAsync();
    }

    /// <summary>
    /// gh#442 guard layer 2: the composed seam must still be production's. Latched, and reached from every drive
    /// method, so no suite that asserts on notifications can skip it. A descriptor check cannot do this —
    /// production registers the seam with an implementation factory, so its <c>ImplementationType</c> is null.
    /// </summary>
    public void AssertProductionChainIntact()
    {
        if (Interlocked.Exchange(ref _chainAsserted, 1) == 1)
        {
            return;
        }

        using IServiceScope scope = Services.CreateScope();
        INotificationChannel seam = scope.ServiceProvider.GetRequiredService<INotificationChannel>();
        if (seam is not OutboxNotificationChannel)
        {
            throw new InvalidOperationException(
                $"gh#442: the composed INotificationChannel is {seam.GetType().Name}, not OutboxNotificationChannel. "
                + "Either PRODUCTION moved the seam — update this guard and the durability cases with it — or THIS "
                + "HARNESS rebuilt the chain and durability has silently stopped being covered.");
        }

        // Both must exist for a page to go out at all; a missing one is the gh#437 hole reopened.
        _ = scope.ServiceProvider.GetRequiredService<NotificationOutboxRelay>();
        _ = Services.GetRequiredService<QueuedNotificationChannel>();
    }

    private static int CountNotificationChainDescriptors(IServiceCollection services) => services.Count(descriptor =>
        descriptor.ServiceType == typeof(INotificationChannel)
        || descriptor.ServiceType == typeof(QueuedNotificationChannel)
        || descriptor.ServiceType == typeof(NullNotificationChannel)
        || descriptor.ServiceType == typeof(NotificationOutboxRelay));
}
