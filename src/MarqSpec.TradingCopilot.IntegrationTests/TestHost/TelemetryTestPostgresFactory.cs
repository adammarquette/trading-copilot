namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the telemetry trace-continuity cases (gh#329). Like
/// <see cref="RetentionConsumerTestPostgresFactory"/> it <b>keeps the always-on hosted services running</b>, because
/// the <b>live</b> <c>StopPromotionHost</c> / <c>ConditionalOrderHost</c> are precisely the subject: the property
/// under test is that a <i>real</i> consumer span-links back to the producer across the event log's async gap
/// (ADR-0002, engineering §7), which a stripped host cannot demonstrate.
/// </summary>
/// <remarks>
/// Inherits the adversarial venue stub from <see cref="StubbedVenuePostgresFactory"/> — a promotion pass builds a
/// venue — and gets its own throwaway Postgres container, so a live consumer polling in the background can never
/// interfere with another suite's rows.
/// </remarks>
public sealed class TelemetryTestPostgresFactory : StubbedVenuePostgresFactory
{
}
