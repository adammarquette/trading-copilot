using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the adopted-round-trip trade suite (gh#960, the end-to-end QA card for gh#770): the same
/// adversarial-venue-plus-real-stream shape <see cref="FlatBeforeFillAdverseOrderPostgresFactory"/> and
/// <see cref="TradeJournalWriteFaultPostgresFactory"/> use, dedicated to this suite so its throwaway Postgres
/// container (gh#121) is not shared with any other class.
/// </summary>
/// <remarks>
/// <para>
/// <b>(1) Deterministic by default.</b> Every always-on <see cref="IHostedService"/> is stripped; the suite's one
/// case constructs and starts its own <see cref="AccountEventStreamHost"/> so nothing else is consuming the
/// seeded surface while it runs. The schema still migrates via <c>StartupTasks.MigrateAndBootstrapAsync</c>,
/// invoked directly by <c>Program</c>, not through a hosted service.
/// </para>
/// <para>
/// <b>(2) The account-event seam, doubled adversarially.</b> <see cref="TestAccountEventStream"/> replaces the
/// real ProjectX stream so the case can arm a closing <see cref="Domain.Venue.FillEvent"/>, the flat
/// <see cref="Domain.Venue.PositionEvent"/>, and a REPLAYED entry <see cref="Domain.Venue.FillEvent"/> and deliver
/// each through the <b>real</b> <see cref="AccountEventIngestionService"/> / <see cref="TradeJournalService"/>
/// pipeline — the same requirement <see cref="FlatBeforeFillAdverseOrderPostgresFactory"/> states: hand-writing the
/// closing <c>Fill</c> row, or the replay, would assume away the delivery hazard under test.
/// <see cref="AdversarialTestTradingVenue.MakeAccountStreamingSupported"/> grants the capability
/// <see cref="AccountEventStreamHost"/> requires before it will stream at all.
/// </para>
/// <para>
/// <b>(3) The execution-SLI sink, on a private meter</b> (gh#330's isolation technique): the composed outcome
/// (<c>journalled</c>) is only observable as a measurement. The swap replaces the concrete sink, never the
/// <see cref="IExecutionMetrics"/> registration (gh#343).
/// </para>
/// <para>
/// <b>(4) Captured logs.</b> The replayed entry fill's idempotent skip
/// (<c>AccountEventIngestionService.ProcessFillAsync</c>) has no other durable trace once the unique-index rejects
/// it — no new row, no changed status — so the suite's one observable signal that the replay was actually
/// PROCESSED (rather than merely enqueued) is its own log line.
/// </para>
/// </remarks>
public sealed class AdoptedRoundTripTradePostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The meter this host records to — private to this fixture (gh#330's isolation technique).</summary>
    public string MeterName { get; } = ExecutionMetrics.MeterName + ".AdoptedRoundTripTrade." + Guid.NewGuid().ToString("N");

    /// <summary>Every execution-SLI measurement this fixture's host emitted.</summary>
    public SliCapture Capture { get; } = new();

    /// <summary>The captured log stream — the replayed entry fill's idempotent skip has no other durable trace.</summary>
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
