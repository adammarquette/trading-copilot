using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
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
    /// Delivers whatever the last pass recorded to the outbox. The hosted services are removed for determinism,
    /// so nothing drains it on its own — a test that asserts on what was sent must drain first.
    /// </summary>
    /// <remarks>
    /// gh#400 replaced the in-process queue with a durable outbox, so this drives one <b>relay</b> pass rather
    /// than pumping a queue. The affordance is unchanged: exactly one deliberate delivery pass, no timer to race.
    /// </remarks>
    public async Task DrainNotificationsAsync()
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();

        await Services.GetRequiredService<NotificationRelayHost>().DrainAsync(scope, CancellationToken.None);
    }

    /// <summary>
    /// How many notifications are recorded but not yet delivered — the outbox's own backlog.
    /// </summary>
    /// <remarks>
    /// Added while diagnosing gh#400: it separates <i>never recorded</i> from <i>never delivered</i>, which no
    /// assertion on the recorder can distinguish. Kept, because that ambiguity is inherent to a two-stage
    /// delivery path and the next failure will pose the same question.
    /// </remarks>
    public async Task<int> OwedNotificationCountAsync()
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>()
            .NotificationOutbox.CountAsync(row => row.DeliveredAt == null);
    }

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
        // production wraps around it. Replacing the whole chain would delete dedup -- the very behaviour "one page
        // per incident" exists to prove -- and the suite would then pass against a system that pages 120 times.
        // The double stands where Pushover stands, never where the logic stands.
        //
        // gh#400: the seam itself (INotificationChannel -> OutboxNotificationChannel) is left PRODUCTION-BOUND.
        // Substituting it would bypass the outbox, and the durability the suite is here to observe would never be
        // exercised. Only the leaf changes.
        foreach (ServiceDescriptor dedup in services
            .Where(descriptor => descriptor.ServiceType == typeof(DedupingNotificationChannel)).ToList())
        {
            services.Remove(dedup);
        }

        services.AddSingleton(provider => new DedupingNotificationChannel(
            Notifications, provider.GetRequiredService<ILogger<DedupingNotificationChannel>>()));

        // Deterministic: the only flatten pass is the test's explicit RunPassAsync.
        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }

        // The relay is removed above with the other hosted services, so nothing delivers on a timer. It is
        // re-registered as a PLAIN singleton purely so the suite can drive exactly one pass (see
        // DrainNotificationsAsync) -- the same explicit-drain stance this factory already took with the queue.
        services.AddSingleton<NotificationRelayHost>();
    }
}
