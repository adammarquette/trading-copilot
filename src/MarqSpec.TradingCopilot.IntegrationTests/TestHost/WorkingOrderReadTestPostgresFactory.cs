using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the venue-truth resting-orders read suite (gh#534 ⇒ gh#381).
/// </summary>
/// <remarks>
/// <para>
/// Strips every always-on <see cref="IHostedService"/>. That is not hygiene: <c>ProtectionMonitorHost</c> reads
/// the <b>same</b> venue seam (<c>GetWorkingOrdersAsync</c>) on its own schedule, against the shared singleton
/// stub, so a pass landing mid-test would perturb the very reads under assertion.
/// </para>
/// <para>
/// The <b>default</b> flatten schedule is deliberately left intact, unlike the flatten and alerting hosts which
/// override <c>Flatten:Instruments</c>: those schedules are what the reconciliation derives its
/// <c>markBasis</c> from, so replacing them would change the answer this suite reads.
/// </para>
/// </remarks>
public sealed class WorkingOrderReadTestPostgresFactory : StubbedVenuePostgresFactory
{
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
    }
}
