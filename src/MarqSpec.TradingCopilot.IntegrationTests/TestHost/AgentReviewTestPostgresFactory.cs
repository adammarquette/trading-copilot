using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the agent-review route suite (gh#429 ⇒ gh#402), configured as a deployment that <b>has an LLM</b>:
/// <c>Llm:ApiKey</c> is set, so <c>AiRegistration</c> binds the real <c>LlmTriggerReviewer</c> and its fail-closed
/// parsing is under test rather than stubbed. Only the outbound <see cref="ILlmProvider"/> is doubled, by
/// <see cref="AdversarialLlmProvider"/>.
/// </summary>
/// <remarks>
/// Two further intercepts, both for determinism: every always-on <see cref="IHostedService"/> is stripped (above
/// all <c>TriggerScanHost</c>, which polls every 60s and would fire passes underneath the assertions — the suite's
/// only pass is its own explicit <c>ScanAsync(now, …)</c>); and the notification transport is replaced by a
/// <see cref="RecordingNotificationChannel"/> while <b>keeping production's queue → dedup chain</b>, so
/// "one advisory per incident" stays assertable. Draining is explicit, since nothing pumps the queue here.
/// </remarks>
public sealed class AgentReviewTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The doubled model provider — feeds completions, never decisions.</summary>
    public AdversarialLlmProvider Llm { get; } = new();

    /// <summary>The recording transport every advisory lands in.</summary>
    public RecordingNotificationChannel Notifications { get; } = new();

    /// <summary>Delivers whatever the last pass enqueued — nothing pumps the queue with the hosts stripped.</summary>
    public Task DrainNotificationsAsync() =>
        Services.GetRequiredService<QueuedNotificationChannel>().DrainPendingAsync(CancellationToken.None);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // The presence of a key is THE reviewer switch (LlmOptions.IsConfigured): with it, the deployment binds
        // LlmTriggerReviewer. Not a real secret and never used — the provider behind it is doubled.
        builder.UseSetting("Llm:ApiKey", "test-key-not-a-secret");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // The one seam doubled: the outbound provider. The reviewer, the geometry check, the debounce and the whole
        // evaluation service remain production code.
        services.RemoveAll<ILlmProvider>();
        services.AddSingleton<ILlmProvider>(Llm);

        // Substitute the recorder for the TRANSPORT ONLY, mirroring production's queue -> dedup -> transport chain.
        // Replacing the whole INotificationChannel would delete the dedup decorator and make "one advisory per
        // incident" vacuous (the gh#382 harness-bypass trap).
        foreach (ServiceDescriptor channel in services
            .Where(descriptor => descriptor.ServiceType == typeof(INotificationChannel)).ToList())
        {
            services.Remove(channel);
        }

        services.AddSingleton(provider => new QueuedNotificationChannel(
            new DedupingNotificationChannel(
                Notifications, provider.GetRequiredService<ILogger<DedupingNotificationChannel>>()),
            provider.GetRequiredService<ILogger<QueuedNotificationChannel>>()));
        services.AddSingleton<INotificationChannel>(provider =>
            provider.GetRequiredService<QueuedNotificationChannel>());

        // Deterministic: the only trigger scan is the test's explicit ScanAsync.
        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }
    }
}

/// <summary>
/// The same host configured as the <b>default deployment</b>: <c>Llm:ApiKey</c> is <b>absent</b>, so
/// <c>AiRegistration</c> binds <c>NullTriggerReviewer</c> (gh#429). The provider is still doubled — not to feed it,
/// but to prove it is <b>never called</b> when no reviewer is configured.
/// </summary>
public sealed class NoReviewerTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The doubled provider — expected to receive nothing at all on this host.</summary>
    public AdversarialLlmProvider Llm { get; } = new();

    /// <summary>The recording transport every advisory lands in.</summary>
    public RecordingNotificationChannel Notifications { get; } = new();

    /// <summary>Delivers whatever the last pass enqueued.</summary>
    public Task DrainNotificationsAsync() =>
        Services.GetRequiredService<QueuedNotificationChannel>().DrainPendingAsync(CancellationToken.None);

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        services.RemoveAll<ILlmProvider>();
        services.AddSingleton<ILlmProvider>(Llm);

        foreach (ServiceDescriptor channel in services
            .Where(descriptor => descriptor.ServiceType == typeof(INotificationChannel)).ToList())
        {
            services.Remove(channel);
        }

        services.AddSingleton(provider => new QueuedNotificationChannel(
            new DedupingNotificationChannel(
                Notifications, provider.GetRequiredService<ILogger<DedupingNotificationChannel>>()),
            provider.GetRequiredService<ILogger<QueuedNotificationChannel>>()));
        services.AddSingleton<INotificationChannel>(provider =>
            provider.GetRequiredService<QueuedNotificationChannel>());

        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }
    }
}
