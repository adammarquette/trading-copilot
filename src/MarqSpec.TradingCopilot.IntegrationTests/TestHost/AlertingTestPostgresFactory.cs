using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the alerting suite (gh#246): the adversarial venue stub, plus a
/// <see cref="RecordingNotificationChannel"/> in place of the real channel so no test can page the operator.
/// </summary>
/// <remarks>
/// The hosted services are stripped for the same reason the flatten suites strip them (gh#186/#188): the only
/// flatten pass must be the test's own explicit one, at an instant the test chooses. A background scheduler
/// firing on its own timer would make "exactly one page" unassertable.
/// </remarks>
public sealed class AlertingTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The recording channel every notification lands in.</summary>
    public RecordingNotificationChannel Notifications { get; } = new();

    /// <summary>
    /// Delivers whatever the last pass enqueued. The hosted services are removed for determinism, so nothing
    /// drains the notification queue on its own — a test that asserts on what was sent must pump first.
    /// </summary>
    public Task DrainNotificationsAsync() =>
        Services.GetRequiredService<QueuedNotificationChannel>().DrainPendingAsync(CancellationToken.None);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // ES is the market under test; CL is deliberately DISABLED so the suite also covers the "a market the
        // operator turned off must not page" direction (R-13's warned override).
        builder.UseSetting("Flatten:Instruments:0:Symbol", "CL");
        builder.UseSetting("Flatten:Instruments:0:Enabled", "false");
        builder.UseSetting("Flatten:Instruments:0:SessionClose", "13:15");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Substitute the recorder for the TRANSPORT ONLY, keeping the DedupingNotificationChannel decorator that
        // production wraps around it. Replacing the whole INotificationChannel registration would delete dedup --
        // the very behaviour "one page per incident" exists to prove -- and the suite would then pass against a
        // system that pages 120 times. The double stands where Pushover stands, never where the logic stands.
        foreach (ServiceDescriptor channel in services
            .Where(descriptor => descriptor.ServiceType == typeof(INotificationChannel)).ToList())
        {
            services.Remove(channel);
        }

        // Mirror PRODUCTION's chain exactly, minus the transport: queue -> dedup -> recorder. The queue is what
        // keeps the send off the flatten hot path (gh#289); leaving it out here would build a two-layer stack the
        // suite could never observe that property in -- and would let a future regression back onto the hot path
        // unnoticed, which is precisely what gh#246 caught the first time.
        //
        // Draining is EXPLICIT (see Drain), matching this factory's existing stance that the only flatten pass is
        // the test's own: with the hosted services removed there is no pump, so the suite pumps.
        services.AddSingleton(provider => new QueuedNotificationChannel(
            new DedupingNotificationChannel(
                Notifications, provider.GetRequiredService<ILogger<DedupingNotificationChannel>>()),
            provider.GetRequiredService<ILogger<QueuedNotificationChannel>>()));
        services.AddSingleton<INotificationChannel>(provider =>
            provider.GetRequiredService<QueuedNotificationChannel>());

        // Deterministic: the only flatten pass is the test's explicit RunPassAsync.
        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }
    }
}
