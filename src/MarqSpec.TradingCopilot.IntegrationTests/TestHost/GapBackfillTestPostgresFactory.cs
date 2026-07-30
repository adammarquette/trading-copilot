using MarqSpec.TradingCopilot.Api.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the gap-backfill recovery suite (gh#514 ⇒ gh#306, R-1).
/// </summary>
/// <remarks>
/// <para>
/// <c>GapBackfillService</c> has only ever run against unit doubles, so its two <b>relational</b> reads have never
/// been translated by Npgsql: the projection-then-<c>Distinct()</c> over a <i>private nested record</i>, and the
/// <c>Bars</c> hypertable range read. A translation failure in either is swallowed by
/// <c>StopPromotionHost.BackfillAsync</c>'s catch-all into "resuming at the head without recovery" — which would
/// leave R-1's recovery guarantee silently dead in production with every unit test green.
/// </para>
/// <para>
/// <b>Hosted services are stripped</b>, and that is load-bearing rather than hygiene. With <c>StopPromotionHost</c>
/// alive, a promotion count is true whether or not the backfill did anything, so every promotion assertion in this
/// suite would be vacuous. <c>VenueConnectionMonitorHost</c> is the sharper hazard: it flips <b>every</b>
/// <c>Hidden</c> plan to <c>Orphaned</c> on its first pass when the venue reports disconnected, and the backfill
/// only ever reads <c>Hidden</c> — so with it running the subject of this suite quietly ceases to exist.
/// </para>
/// <para>
/// The metrics swap replaces the <b>concrete</b> <see cref="ExecutionMetrics"/> sink and deliberately <b>not</b>
/// the <c>IExecutionMetrics</c> registration: the composition root binds the interface <i>through</i> the concrete
/// type, so the shipped <c>FailureTolerantExecutionMetrics</c> decorator still applies. Replacing the interface
/// descriptor would delete that decorator and test a composition production does not have — the gh#382 false-green.
/// </para>
/// </remarks>
public sealed class GapBackfillTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>This fixture's private meter, so a parallel suite's measurements cannot bleed in.</summary>
    public string MeterName { get; } = ExecutionMetrics.MeterName + ".GapBackfill." + Guid.NewGuid().ToString("N");

    /// <summary>Everything this host recorded.</summary>
    public SliCapture Capture { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        base.ConfigureTestServices(services);

        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToList())
        {
            services.Remove(hosted);
        }

        ServiceDescriptor? concrete = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(ExecutionMetrics));
        if (concrete != null)
        {
            services.Remove(concrete);
        }

        services.AddSingleton(new ExecutionMetrics(MeterName));
        Capture.ListenTo(MeterName);
    }
}
