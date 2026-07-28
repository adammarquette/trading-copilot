using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Binds the operator-notification chain — <b>queue → dedup → transport</b> (gh#243, gh#289, ADR-0019).
/// </summary>
/// <remarks>
/// Extracted from <c>Program.cs</c> (gh#320) so the binding can be <b>asserted</b> rather than only read. The
/// property that matters is not any one class's behaviour but the <i>shape of the composition</i>: the auto-flatten
/// calls <see cref="INotificationChannel"/> on the R-13 hot path, so whatever is bound there must return without
/// waiting on a network. Constructing the pieces directly in a test cannot witness that, and a regression in the
/// binding would leave every existing test green — see <c>NotificationRegistrationTests</c>.
/// </remarks>
public static class NotificationRegistration
{
    /// <summary>Adds the notification chain and the pump that drains it.</summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static WebApplicationBuilder AddTradingCopilotNotifications(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The notification channel (gh#243, ADR-0019): LAYER 1 of alerting -- the push the app sends itself, because
        // it knows first. Routing a P1 through a metrics scrape and a rule evaluation would cost tens of seconds on a
        // path where CL and GC have ~15 minutes of margin in total. Pushover because an on-call-of-one needs a PAGER:
        // its Emergency priority is the only one that repeats until ACKNOWLEDGED and bypasses Do Not Disturb.
        //
        // Registered as a SINGLETON: the dedup decorator holds the open-incident set, and a scoped one would forget
        // it every pass -- turning "one page per incident" into one page per 15-second poll. Unconfigured falls back
        // to a channel that logs what it would have sent, so a fork or a fresh deployment still boots (loudly, never
        // silently).
        builder.Services.Configure<PushoverOptions>(builder.Configuration.GetSection(PushoverOptions.SectionName));
        builder.Services.AddHttpClient<PushoverNotificationChannel>(
            client => { client.Timeout = TimeSpan.FromSeconds(10); });

        // The chain, outermost first: QUEUE -> dedup -> transport.
        //
        // The queue is outermost because the caller is the auto-flatten, and gh#289 showed what awaiting a transport
        // there costs: a 5-second channel made a flatten pass take 5.15 s, on the R-13 path, at the exact moment a
        // position was already failing to close. Enqueue returns immediately; a background pump does the network work.
        //
        // Dedup sits BELOW the queue, not above it, and that ordering is load-bearing: the pump is single-threaded,
        // so the "already reported?" check and the record of reporting can no longer interleave and double-page, and
        // dedup sees the REAL delivery result rather than the queue's "accepted".
        builder.Services.AddSingleton<NullNotificationChannel>();
        builder.Services.AddSingleton<QueuedNotificationChannel>(provider =>
        {
            PushoverOptions pushover = provider.GetRequiredService<IOptions<PushoverOptions>>().Value;
            INotificationChannel transport = pushover.IsConfigured
                ? provider.GetRequiredService<PushoverNotificationChannel>()
                : provider.GetRequiredService<NullNotificationChannel>();

            DedupingNotificationChannel deduping = new(
                transport, provider.GetRequiredService<ILogger<DedupingNotificationChannel>>());

            return new QueuedNotificationChannel(deduping, provider.GetRequiredService<ILogger<QueuedNotificationChannel>>());
        });
        builder.Services.AddSingleton<INotificationChannel>(provider => provider.GetRequiredService<QueuedNotificationChannel>());

        // Enqueue-and-return is only safe because THIS drains the queue. Without the pump every page is accepted and
        // silently discarded -- the failure mode is invisible, because the caller still sees success.
        builder.Services.AddHostedService<NotificationPumpHost>();

        return builder;
    }
}
