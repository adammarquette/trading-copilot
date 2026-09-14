using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the retention-gap <b>consumer-side</b> cases (gh#228): unlike the flatten/orphan factories, it
/// <b>keeps the always-on hosted services running</b> — the live <c>StopPromotionHost</c> and
/// <c>ConditionalOrderHost</c> are the subject, since a gap signalled by <c>ReadAfterAsync</c> is only useful if
/// the safety-critical consumers neither swallow it nor crash-loop on it. It adds log capture so the
/// high-severity gap log can be asserted, and inherits the adversarial venue stub (a promotion pass builds a
/// venue) from <see cref="StubbedVenuePostgresFactory"/>.
/// </summary>
/// <remarks>
/// Also the fixture for the <b>gap-recovery</b> host cases (gh#557): which window the host hands the backfill,
/// whether it runs it at all, and whether the consumer survives it throwing are all live-host questions, and the
/// only surface they report through is this log stream. Each test class gets its own instance (and so its own
/// container), so the two suites share the shape without sharing state.
/// </remarks>
public sealed class RetentionConsumerTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The captured log stream — asserted for the consumers' high-severity retention-gap log.</summary>
    public InMemoryLogCollector Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }
}
