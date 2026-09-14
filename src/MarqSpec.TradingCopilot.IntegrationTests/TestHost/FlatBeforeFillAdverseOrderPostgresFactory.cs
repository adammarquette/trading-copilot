using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the flat-before-fill adverse-order suite (gh#911, of gh#748): the same adversarial-venue-plus-real-
/// stream shape <see cref="TradeFifoPairingPostgresFactory"/> uses for its live-host re-pairing case, dedicated to
/// this suite so its throwaway Postgres container (gh#121) is not shared with any other class.
/// </summary>
/// <remarks>
/// <para>
/// <b>(1) Deterministic by default.</b> Every always-on <see cref="IHostedService"/> is stripped; each case
/// constructs and starts its own <see cref="AccountEventStreamHost"/> so nothing else is consuming the seeded
/// surface while it runs. The schema still migrates via <c>StartupTasks.MigrateAndBootstrapAsync</c>, invoked
/// directly by <c>Program</c>, not through a hosted service.
/// </para>
/// <para>
/// <b>(2) The account-event seam, doubled adversarially.</b> <see cref="TestAccountEventStream"/> replaces the real
/// ProjectX stream so a case can arm a <see cref="Domain.Venue.PositionEvent"/>(flat) and its closing
/// <see cref="Domain.Venue.FillEvent"/> in either order and deliver both through the <b>real</b>
/// <see cref="AccountEventIngestionService"/> / <see cref="TradeJournalService"/> pipeline — gh#911's own
/// requirement that the hazard is about fill <i>delivery</i>, so hand-writing the <c>Fill</c> row would assume away
/// the thing under test. The double feeds venue-neutral events only; it never decides how the host reacts to them.
/// </para>
/// <para>
/// <b>(3) The execution-SLI sink, on a private meter</b> (gh#330's isolation technique), because the outcomes this
/// suite asserts (<c>not-composable</c>, and — once gh#748 lands — <c>deferred</c> / <c>journalled</c>) are only
/// observable as measurements. The swap replaces the concrete sink, never the <see cref="IExecutionMetrics"/>
/// registration (gh#343).
/// </para>
/// </remarks>
public sealed class FlatBeforeFillAdverseOrderPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The meter this host records to — private to this fixture (gh#330's isolation technique).</summary>
    public string MeterName { get; } = ExecutionMetrics.MeterName + ".FlatBeforeFill." + Guid.NewGuid().ToString("N");

    /// <summary>Every execution-SLI measurement this fixture's host emitted.</summary>
    public SliCapture Capture { get; } = new();

    /// <summary>The captured log stream.</summary>
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
