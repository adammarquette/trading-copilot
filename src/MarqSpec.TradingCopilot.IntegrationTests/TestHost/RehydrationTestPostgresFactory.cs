using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the decision-state rehydration suite (gh#223). It <b>strips every always-on
/// <see cref="IHostedService"/></b> — most of all the 5-second <c>VenueConnectionMonitorHost</c> and the
/// quote-driven promotion / conditional-firing consumers — so nothing perturbs the seeded decision surface before
/// or after the explicit <c>CreateHost()</c> "restart". Startup rehydration itself runs in <c>StartupTasks</c>
/// (a <c>Program</c> top-level statement, not a hosted service), so it still fires on every host build — which is
/// exactly what the "restart" exercises. Inherits the adversarial venue stub from
/// <see cref="StubbedVenuePostgresFactory"/> (the one sanctioned double, gh#223).
/// </summary>
public sealed class RehydrationTestPostgresFactory : StubbedVenuePostgresFactory
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
