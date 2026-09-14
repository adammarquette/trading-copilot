namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the confirm-before-live gate suite (gh#486 ⇒ gh#470) — deliberately the <b>plainest</b>
/// <see cref="NotificationHarnessPostgresFactory"/> there is: no suite settings, no suite doubles.
/// </summary>
/// <remarks>
/// <para>
/// The gate under test is the <b>mechanical</b> route, so no <c>Llm:ApiKey</c> is set and nothing about the reviewer
/// is configured. What that buys is the point of this type: an unconfirmed trigger's inertness is asserted against a
/// composition that has had <b>nothing</b> removed from it, so a green result cannot be an artifact of the harness
/// having quietly deleted the thing that would have fired.
/// </para>
/// <para>
/// The full production notification chain (outbox → relay → queue → dedup → the real Pushover adapter) is intact and
/// only the wire beneath it is recorded, per gh#442. That matters here more than usual: this suite's central claim is
/// a <b>negative</b> one — that nothing was sent — and a negative is worthless if the sending machinery was never
/// present to begin with. The paired positive case fires the same setup through the same chain and observes a real
/// page land on <see cref="RecordingPushoverTransport"/>, which is what makes the negative meaningful.
/// </para>
/// </remarks>
public sealed class ConfirmationGateTestPostgresFactory : NotificationHarnessPostgresFactory
{
}
