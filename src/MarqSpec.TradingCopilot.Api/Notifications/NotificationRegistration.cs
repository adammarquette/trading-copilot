using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Binds the operator-notification chain — <b>outbox → relay → dedup → transport</b> (gh#243, gh#289, gh#400,
/// ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>Program.cs</c> (gh#320) so the binding can be <b>asserted</b> rather than only read. The
/// property that matters is not any one class's behaviour but the <i>shape of the composition</i>: the
/// auto-flatten calls <see cref="INotificationChannel"/> on the R-13 hot path, so whatever is bound there must
/// return without waiting on a network. Constructing the pieces directly in a test cannot witness that, and a
/// regression in the binding would leave every existing test green — see <c>NotificationRegistrationTests</c>.
/// </para>
/// <para>
/// <b>What gh#400 changed, and why.</b> The seam used to resolve to an in-process queue: enqueue returned
/// immediately and a pump did the network work. That protected the hot path and left a durability hole — a hard
/// crash before the pump drained lost whatever it held. The seam now resolves to a <b>durable outbox</b>, and a
/// relay delivers from it. The hot path is still never blocked (a local row write, or on the <c>Enlist</c> path
/// no extra write at all), and what it wrote now survives a crash.
/// </para>
/// <para>
/// <b>The lifetimes are load-bearing.</b> The outbox is <b>scoped</b>, because it writes through the scoped
/// <c>DbContext</c> — which is what lets a producer commit intent inside its own transaction. Every consumer of
/// the seam is itself scoped (auto-flatten, watchdog, trigger evaluation), so nothing singleton depends on it.
/// Dedup stays <b>singleton</b> below the relay: it holds the open-incident set, and a scoped one would forget it
/// every pass, turning "one page per incident" into one page per poll.
/// </para>
/// </remarks>
public static class NotificationRegistration
{
    /// <summary>Adds the notification chain and the relay that drains it.</summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static WebApplicationBuilder AddTradingCopilotNotifications(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The transport (gh#243, ADR-0019): LAYER 1 of alerting -- the push the app sends itself, because it knows
        // first. Routing a P1 through a metrics scrape and a rule evaluation would cost tens of seconds on a path
        // where CL and GC have ~15 minutes of margin in total. Pushover because an on-call-of-one needs a PAGER:
        // its Emergency priority is the only one that repeats until ACKNOWLEDGED and bypasses Do Not Disturb.
        //
        // Unconfigured falls back to a channel that logs what it would have sent, so a fork or a fresh deployment
        // still boots (loudly, never silently).
        builder.Services.Configure<PushoverOptions>(builder.Configuration.GetSection(PushoverOptions.SectionName));
        builder.Services.AddHttpClient<PushoverNotificationChannel>(
            client => { client.Timeout = TimeSpan.FromSeconds(10); });
        builder.Services.AddSingleton<NullNotificationChannel>();

        // Dedup sits directly above the transport and is a SINGLETON: it holds the open-incident set, so a scoped
        // one would forget it every pass and page once per poll instead of once per incident. It is driven by the
        // relay -- single-threaded -- so the "already reported?" check and the record of reporting cannot
        // interleave and double-page, and it sees the REAL delivery result rather than an "accepted".
        builder.Services.AddSingleton<DedupingNotificationChannel>(provider =>
        {
            PushoverOptions pushover = provider.GetRequiredService<IOptions<PushoverOptions>>().Value;
            INotificationChannel transport = pushover.IsConfigured
                ? provider.GetRequiredService<PushoverNotificationChannel>()
                : provider.GetRequiredService<NullNotificationChannel>();

            return new DedupingNotificationChannel(
                transport, provider.GetRequiredService<ILogger<DedupingNotificationChannel>>());
        });

        // THE SEAM producers call: the durable outbox (gh#400). Scoped, because it writes through the scoped
        // DbContext -- which is what lets a safety-critical producer commit the intent inside its own transaction
        // via Enlist, so a crash between "it happened" and "the operator was told" cannot exist.
        builder.Services.AddScoped<OutboxNotificationChannel>();
        builder.Services.AddScoped<INotificationChannel>(
            provider => provider.GetRequiredService<OutboxNotificationChannel>());

        // Recording-and-returning is only safe because THIS delivers. Without the relay every page is recorded
        // and never sent -- and the failure would be invisible, because the caller still sees success.
        builder.Services.AddHostedService<NotificationRelayHost>();

        return builder;
    }
}
