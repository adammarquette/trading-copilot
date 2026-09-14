using Microsoft.AspNetCore.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the alerting suite (gh#246), now on the shared transport-only harness (gh#480 ⇒ gh#442).
/// </summary>
/// <remarks>
/// <para>
/// It used to remove every <c>INotificationChannel</c> registration and rebuild <c>queue → dedup → recorder</c>.
/// Once gh#437 made the seam durable, that block silently deleted the <b>outbox</b> from the composition under
/// test. The migration was deliberately held back while gh#459 was open — with delivery broken, putting the outbox
/// back would have turned this suite's whole positive surface red — and gh#468 removed that reason.
/// </para>
/// <para>
/// Everything notification-shaped now comes from <see cref="NotificationHarnessPostgresFactory"/>: the outbox, the
/// relay, the queue, the dedup decorator and the real <c>PushoverNotificationChannel</c> are production objects,
/// and only the wire beneath them is recorded. The suite delivers with <c>DeliverOutboxAsync()</c>.
/// </para>
/// </remarks>
public sealed class AlertingTestPostgresFactory : NotificationHarnessPostgresFactory
{
    protected override void ConfigureSuiteSettings(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ES is the market under test; CL is deliberately DISABLED so the suite also covers the "a market the
        // operator turned off must not page" direction (R-13's warned override).
        builder.UseSetting("Flatten:Instruments:0:Symbol", "CL");
        builder.UseSetting("Flatten:Instruments:0:Enabled", "false");
        builder.UseSetting("Flatten:Instruments:0:SessionClose", "13:15");
    }
}
