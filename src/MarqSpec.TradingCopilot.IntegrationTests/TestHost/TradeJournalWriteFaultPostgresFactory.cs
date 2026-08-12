using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the write-fault narrowing suite (gh#779, of gh#747 / gh#731): the adversarial venue stub, plus
/// three test-only intercepts layered on <see cref="StubbedVenuePostgresFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>(1) Deterministic by default.</b> Every always-on <see cref="IHostedService"/> is stripped, the same
/// discipline <see cref="OcoExitTestPostgresFactory"/> and <see cref="FlattenTestPostgresFactory"/> use — the two
/// cases that call <c>TradeJournalService.ProcessFlatAsync</c> directly need nothing else touching the seeded
/// surface. The one case that needs a live host (the outer-safety case) constructs its own
/// <c>AccountEventStreamHost</c> and starts/stops it explicitly, rather than leaving it always-on for every test
/// in the class.
/// </para>
/// <para>
/// <b>(2) The account-event seam, doubled adversarially.</b> <see cref="TestAccountEventStream"/> replaces
/// the real <c>ProjectXAccountEventStream</c>, and <see cref="AdversarialTestTradingVenue.MakeAccountStreamingSupported"/>
/// grants the capability <c>AccountEventStreamHost</c> requires before it will stream at all — both opt-in, so no
/// other suite sharing the adversarial venue factory is affected.
/// </para>
/// <para>
/// <b>(3) The execution-SLI sink, on a private meter.</b> Same technique as <c>ExecutionSliTestPostgresFactory</c>
/// (gh#330): the concrete <see cref="ExecutionMetrics"/> is swapped for one on a fixture-private meter name, so a
/// <see cref="SliCapture"/> listener observes only this suite's <c>trading.journal.outcomes</c> measurements. The
/// swap replaces the concrete sink, never the <c>IExecutionMetrics</c> registration, for the same reason gh#343
/// records: replacing the interface would bypass whatever the composition root wraps it in.
/// </para>
/// </remarks>
public sealed class TradeJournalWriteFaultPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The meter this host records to — private to this fixture (gh#330's isolation technique).</summary>
    public string MeterName { get; } = ExecutionMetrics.MeterName + ".JournalWriteFault." + Guid.NewGuid().ToString("N");

    /// <summary>Every execution-SLI measurement this fixture's host emitted.</summary>
    public SliCapture Capture { get; } = new();

    /// <summary>The captured log stream — asserted for the "logs and continues" outer-safety case.</summary>
    public InMemoryLogCollector Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Deterministic host: the direct-call cases drive ProcessFlatAsync themselves, and the one case that wants
        // a live host builds and runs its own AccountEventStreamHost instance rather than relying on this one.
        foreach (ServiceDescriptor hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }

        ServiceDescriptor? streamDescriptor = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IAccountEventStream));
        if (streamDescriptor is not null)
        {
            services.Remove(streamDescriptor);
        }

        // Registered under its OWN type too (not just IAccountEventStream), the same shape
        // AdversarialTestProjectXVenueFactory uses, so a test can fetch the concrete double via
        // GetRequiredService<TestAccountEventStream>() to call Arm() without a public property on this (public)
        // factory exposing an internal test-double type (CS0053).
        services.AddSingleton<TestAccountEventStream>();
        services.AddSingleton<IAccountEventStream>(sp => sp.GetRequiredService<TestAccountEventStream>());

        ServiceDescriptor? concrete = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(ExecutionMetrics));
        if (concrete is not null)
        {
            services.Remove(concrete);
        }

        services.AddSingleton(new ExecutionMetrics(MeterName));
        Capture.ListenTo(MeterName);
    }
}
