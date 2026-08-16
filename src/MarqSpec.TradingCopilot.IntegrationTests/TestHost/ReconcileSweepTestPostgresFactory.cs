using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Recovery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the runtime reconcile-strand detection sweep suite (gh#923, of gh#722). It keeps the
/// <b>production-composed</b> <see cref="ReconcileSweepHost"/> registration — configured <c>Enabled = false</c> —
/// and strips every <i>other</i> always-on <see cref="IHostedService"/>, which gives the suite both halves it needs:
/// the <b>disabled = inert</b> criterion is asserted against the real composition root (config → options → host),
/// and every <i>enabled</i> pass is one the test starts itself, on a clock and an age bound it chose, with nothing
/// else sweeping the same database or the same singleton register underneath it.
/// </summary>
/// <remarks>
/// <para>
/// The configured cadence is deliberately <b>fast</b> (1 s interval, 1 s age bound) even though the sweep is
/// disabled here: it is what makes the disabled criterion a guard rather than a tautology. Were
/// <c>ReconcileSweep:Enabled</c> to flip to <c>true</c>, this configuration would discover and alert a seeded strand
/// within a couple of seconds — well inside the suite's wait — so the "no discovery, no alert" assertions go red on
/// the defect instead of passing because nothing had time to happen.
/// </para>
/// <para>
/// Two observation channels, both real: the <b>log stream</b> is captured (the sweep's operator alert is the
/// rehydrator's interim <c>synthetic_risk</c> <c>LogCritical</c> channel), and the concrete
/// <see cref="ExecutionMetrics"/> sink is swapped for one on a <b>private meter name</b> so a
/// <see cref="System.Diagnostics.Metrics.MeterListener"/> observes this fixture's measurements and never another
/// suite's. The swap replaces the <b>concrete</b> sink, never the <c>IExecutionMetrics</c> registration, so the
/// production <c>FailureTolerantExecutionMetrics</c> decorator the composition root wraps the seam in still applies
/// (the gh#343 false-green ExecutionSliTestPostgresFactory documents).
/// </para>
/// </remarks>
public sealed class ReconcileSweepTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The meter this host records to — private to this fixture.</summary>
    public string MeterName { get; } = ExecutionMetrics.MeterName + ".ReconcileSweep." + Guid.NewGuid().ToString("N");

    /// <summary>Every measurement this fixture's host emitted — the strand-detected counter is read from here.</summary>
    public SliCapture Capture { get; } = new();

    /// <summary>The captured log stream — the <c>synthetic_risk</c> operator alert and the disabled-startup warning.</summary>
    public InMemoryLogCollector Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));

        // The production-registered sweep is present and OFF (gh#923: "disabled = inert"), on a cadence fast enough
        // that flipping Enabled to true would detect within the suite's wait — so the inertness assertions can fail.
        builder.UseSetting("ReconcileSweep:Enabled", "false");
        builder.UseSetting("ReconcileSweep:SweepIntervalSeconds", "1");
        builder.UseSetting("ReconcileSweep:StrandAgeThresholdSeconds", "1");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Every OTHER always-on host is stripped (the flatten / rehydration suites' discipline): the quote-driven
        // conditional-firing consumer and the venue monitor would move the very Taking/Firing rows this suite seeds
        // as strands, and any pass but the test's own would age the shared register on a clock the test does not own.
        // The sweep host itself stays, disabled, because the composition root's wiring of it is under test.
        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType != typeof(ReconcileSweepHost))
            .ToList())
        {
            services.Remove(hosted);
        }

        ServiceDescriptor? concrete = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(ExecutionMetrics));
        if (concrete is not null)
        {
            services.Remove(concrete);
        }

        services.AddSingleton(new ExecutionMetrics(MeterName));
        Capture.ListenTo(MeterName);
    }
}
