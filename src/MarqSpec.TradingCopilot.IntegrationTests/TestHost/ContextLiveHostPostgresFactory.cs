using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Recovery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The live-host fixture for the cross-asset <b>context</b> price cases that need the execution watchers actually
/// running (gh#705, of gh#608 / gh#496) — the <see cref="RetentionConsumerTestPostgresFactory"/> shape from
/// gh#557: the always-on hosted services stay up, so <c>StopPromotionHost</c> and <c>ConditionalOrderHost</c>
/// consume the real event log while the suite appends to it, and the log stream is captured so a host's own
/// account of what it did can be asserted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Context symbols configured, no Finnhub key — deliberately.</b> This is the deployment gh#496's most
/// important acceptance criterion describes: an operator asked for context data and the provider is not there
/// (<c>Finnhub__ApiKey</c> unset ⇒ no <c>IContextMarketDataSource</c> is registered at all, so the source cannot
/// be constructed). The platform must still run, and the tradeable feed must be untouched. The symbols use the
/// <c>ContextIngestion:Symbols:0</c> allowlist shape (gh#304) — its <c>__0..__7</c> environment form under
/// compose — so the host reaches the same code path a real deployment does.
/// </para>
/// <para>
/// <b>Why <see cref="VenueConnectionMonitorHost"/> is the one host stripped.</b> Its first pass reconciles the
/// synthetic stops to the venue's liveness, and a test host's venue is never connected — so left running it would
/// call <c>OrphanGuardService.OrphanAsync</c> and move this suite's seeded <b>hidden</b> stop to
/// <c>Orphaned</c> out from under it (the same collision <see cref="OrphanTestPostgresFactory"/> names). It is
/// unrelated to the property under test: the subject here is which <i>event types</i> the two execution watchers
/// act on, and removing an unrelated writer of <c>StopPlans</c> narrows the fixture rather than weakening the
/// guard — the watchers themselves stay live.
/// </para>
/// </remarks>
public sealed class ContextLiveHostPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The configured context symbol — an equity, deliberately not a futures contract.</summary>
    public const string ContextSymbol = "SPY";

    /// <summary>The captured log stream, so a background host's own account of what it did can be asserted.</summary>
    public InMemoryLogCollector Logs { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // gh#304's allowlist shape: an indexed element, exactly as ContextIngestion__Symbols__0 arrives under
        // compose. No credential accompanies it — that absence IS the scenario (see the class remarks).
        builder.UseSetting($"{ContextIngestionOptions.SectionName}:Symbols:0", ContextSymbol);

        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Only the connection monitor goes; every other hosted service — the two execution watchers above all —
        // stays live, because they are the subject.
        foreach (ServiceDescriptor monitor in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(VenueConnectionMonitorHost))
            .ToList())
        {
            services.Remove(monitor);
        }
    }
}
