using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the suggestion DB-guard suite (gh#552). The guards under test are <b>database</b> constraints —
/// three check constraints and the cross-table mode trigger — so this strips every always-on
/// <see cref="IHostedService"/> (the venue monitor, the quote-driven promotion / conditional-firing / drift
/// consumers): none of them should run while a test deliberately inserts an invalid row, and their absence keeps
/// the failure the test observes unambiguously the constraint's. Inherits the adversarial venue stub from
/// <see cref="StubbedVenuePostgresFactory"/> so the host still boots without reaching a real broker.
/// </summary>
public sealed class SuggestionGuardsTestPostgresFactory : StubbedVenuePostgresFactory
{
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
    }
}
