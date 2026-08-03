using Microsoft.AspNetCore.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the suggestion-drift consumer suite (gh#632, gh#546): unlike the flatten/orphan factories, it
/// <b>keeps every always-on hosted service running</b> — in particular the live <c>SuggestionDriftHost</c> and
/// <c>SuggestionExpiryHost</c> — because the consumer's fresh-scope/cursor/teardown lifecycle (the DoD's explicit
/// ask) and the sibling-writer interleaving with the expire sweep are both live-host questions. Adds log capture
/// (mirroring <see cref="RetentionConsumerTestPostgresFactory"/>) so the teardown case can assert the host neither
/// swallows nor crash-loops on shutdown, and inherits the adversarial venue stub from
/// <see cref="StubbedVenuePostgresFactory"/>.
/// </summary>
public sealed class SuggestionDriftTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The captured log stream — asserted for a clean (uncaught-exception-free) host teardown.</summary>
    public InMemoryLogCollector Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }
}
