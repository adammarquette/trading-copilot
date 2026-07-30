using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Binds the operator-notification chain — <b>outbox → queue → dedup → transport</b> (gh#243, gh#289, gh#437, ADR-0019).
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
        // THE SEAM IS THE OUTBOX (gh#437). The chain is now: outbox -> queue -> dedup -> transport.
        //
        // The queue alone was fast but not durable: gh#289 moved delivery off the R-13 hot path, and a hard crash
        // before the pump drained lost whatever it held. Persisting first closes that -- nothing counts as sent
        // until the relay stamps DeliveredAt -- while keeping the hot path off the network, because the outbox
        // writes one indexed row and returns rather than awaiting a transport.
        //
        // SCOPED, not singleton, because it writes through the scoped DbContext. That is a deliberate change from
        // the singleton the queue was: every consumer of this seam (AutoFlattenService, AutoFlattenWatchdogService,
        // TriggerEvaluationService) is itself scoped, so nothing holds it beyond a pass. The dedup memory that
        // NEEDED a singleton still has one -- DedupingNotificationChannel, below the queue, unchanged.
        //
        // `delivery` resolves the CONCRETE QueuedNotificationChannel rather than INotificationChannel: asking for
        // the interface here would resolve this same registration and stack-overflow on first use.
        builder.Services.AddSingleton(TimeProvider.System);
        // Two database handles, and the difference is the point (gh#452). `Enlist` shares the CALLER's context so
        // intent and state change commit atomically; `SendAsync` opens its OWN scope, so it commits nothing of the
        // caller's and — critically — a failure in the caller's unit of work cannot be swallowed by this channel's
        // never-throw contract and silently eat a page.
        builder.Services.AddScoped<INotificationChannel>(provider => new OutboxNotificationChannel(
            provider.GetRequiredService<TradingCopilotDbContext>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<QueuedNotificationChannel>(),
            provider.GetRequiredService<ILogger<OutboxNotificationChannel>>()));

        // Enqueue-and-return is only safe because THIS drains the queue. Without the pump every page is accepted and
        // silently discarded -- the failure mode is invisible, because the caller still sees success.
        builder.Services.AddHostedService<NotificationPumpHost>();

        // ...and the outbox is only safe because THIS drains it. Same property, one layer out: a durable row
        // nobody reads is a page that was recorded and never sent, which is worse than not recording it, because
        // the caller was told it succeeded.
        //
        // `delivery` is bound EXPLICITLY to the concrete QueuedNotificationChannel -- the chain BELOW the outbox
        // (queue -> dedup -> transport), never the INotificationChannel seam (gh#459, P0). A plain
        // `AddScoped<NotificationOutboxRelay>()` let DI resolve `delivery` from the only INotificationChannel
        // registration -- the OutboxNotificationChannel seam itself -- so the relay drained the outbox INTO the
        // outbox: `Enlist` short-circuited on the row the relay had just loaded (same scope, same DbContext,
        // NotificationOutbox.Local), returned true, and every page was stamped DeliveredAt having reached nothing,
        // incl. the R-13 auto-flatten escalation. Same shape as the outbox seam above: bind the concrete type so
        // the interface never resolves back to this graph.
        builder.Services.AddScoped(provider => new NotificationOutboxRelay(
            provider.GetRequiredService<TradingCopilotDbContext>(),
            provider.GetRequiredService<QueuedNotificationChannel>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<NotificationOutboxRelay>>()));
        builder.Services.AddHostedService<NotificationOutboxRelayHost>();

        return builder;
    }
}
