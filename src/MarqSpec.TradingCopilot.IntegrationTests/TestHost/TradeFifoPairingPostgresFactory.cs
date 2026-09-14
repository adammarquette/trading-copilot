using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the FIFO per-leg pairing suite (gh#799, of gh#759 / ADR-0022): the adversarial venue stub plus the
/// same three test-only intercepts <see cref="TradeJournalWriteFaultPostgresFactory"/> uses, for the same reasons.
/// </summary>
/// <remarks>
/// <para>
/// <b>(1) Deterministic by default.</b> Every always-on <see cref="IHostedService"/> is stripped: most cases drive
/// <c>TradeJournalService.ProcessFlatAsync</c> directly, and the one case that needs a live stream (the re-pairing
/// fail-closed guard) constructs and starts its own <c>AccountEventStreamHost</c> so nothing else is consuming the
/// seeded surface while it runs. The schema still migrates — <c>StartupTasks.MigrateAndBootstrapAsync</c> is
/// invoked directly by <c>Program</c>, not through a hosted service.
/// </para>
/// <para>
/// <b>(2) The account-event seam, doubled adversarially.</b> <see cref="TestAccountEventStream"/> replaces the real
/// ProjectX stream so the re-pairing case can deliver its <b>late, out-of-order fills through the real ingestion
/// path</b> (gh#799's own requirement: the container-tier proof runs through the account event stream's actual fill
/// delivery, not through hand-written <c>Fill</c> rows). The double feeds venue-neutral events only — it never
/// decides how the writer pairs them, which is the semantics under test.
/// </para>
/// <para>
/// <b>(3) The execution-SLI sink, on a private meter</b> (gh#330's isolation technique). The refusal outcomes this
/// suite asserts — <c>boundary-merge-refused</c>, <c>duplicate-rejected</c>, <c>not-composable</c> — are only
/// observable as measurements, so the concrete <see cref="ExecutionMetrics"/> is swapped for one on a
/// fixture-private meter and a <see cref="SliCapture"/> listens to just that. The swap replaces the concrete sink,
/// never the <c>IExecutionMetrics</c> registration (gh#343).
/// </para>
/// </remarks>
public sealed class TradeFifoPairingPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The meter this host records to — private to this fixture (gh#330's isolation technique).</summary>
    public string MeterName { get; } = ExecutionMetrics.MeterName + ".FifoPairing." + Guid.NewGuid().ToString("N");

    /// <summary>Every execution-SLI measurement this fixture's host emitted.</summary>
    public SliCapture Capture { get; } = new();

    /// <summary>The captured log stream — asserted by the live-host re-pairing case.</summary>
    public InMemoryLogCollector Logs { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToList())
        {
            services.Remove(hosted);
        }

        ServiceDescriptor? streamDescriptor = services
            .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IAccountEventStream));
        if (streamDescriptor is not null)
        {
            services.Remove(streamDescriptor);
        }

        // Registered under its own type too, so a test can fetch the concrete double without this public factory
        // exposing an internal test-double type (CS0053) — the shape AdversarialTestProjectXVenueFactory uses.
        services.AddSingleton<TestAccountEventStream>();
        services.AddSingleton<IAccountEventStream>(provider => provider.GetRequiredService<TestAccountEventStream>());

        ServiceDescriptor? concreteMetrics = services
            .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(ExecutionMetrics));
        if (concreteMetrics is not null)
        {
            services.Remove(concreteMetrics);
        }

        services.AddSingleton(new ExecutionMetrics(MeterName));
        Capture.ListenTo(MeterName);
    }
}
