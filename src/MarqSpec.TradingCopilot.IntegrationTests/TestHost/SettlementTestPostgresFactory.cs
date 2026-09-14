using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the EOD settlement-reconcile suite (gh#194). It <b>strips every always-on
/// <see cref="IHostedService"/></b> so the auto-flatten / watchdog hosts can't close the seeded open position out
/// from under the reconcile. The reconcile itself is a request-scoped service (<c>PositionReconciliationService</c>)
/// driven directly at a chosen instant; the <b>default</b> per-market flatten schedule (ES/NQ 14:30, CL 13:15,
/// GC 12:15 CT) is left intact — those session closes derive the per-instrument settlement windows under test.
/// Inherits the adversarial venue stub from <see cref="StubbedVenuePostgresFactory"/>.
/// </summary>
public sealed class SettlementTestPostgresFactory : StubbedVenuePostgresFactory
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        foreach (ServiceDescriptor hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }
    }
}
