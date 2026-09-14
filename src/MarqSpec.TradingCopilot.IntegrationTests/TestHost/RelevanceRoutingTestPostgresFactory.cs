using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The pre-merge host for the gh#361 relevance-routing suite: the real API pipeline over a throwaway Postgres
/// container. Relevance is venue-independent by definition (gh#359 never reaches <c>ITradingVenue</c>), so no
/// venue stub is registered here — the only intervention is stripping every <see cref="IHostedService"/>.
/// </summary>
/// <remarks>
/// Most importantly this removes <c>NewsRelevanceHost</c>, which polls on a wall-clock timer (default 60s) and
/// would otherwise race a test's explicit <c>NewsRelevanceService.ResolveAsync</c> call — resolving news against
/// whatever config happens to exist at the instant the timer fires rather than the config a test just finished
/// arranging, and double-counting a pass's "how many resolved" return value. Mirrors
/// <see cref="NewsIngestionTestPostgresFactory"/> (gh#360) and <see cref="FlattenTestPostgresFactory"/> (gh#186 /
/// gh#188), which strip the same class of always-on host for the same determinism reason.
/// </remarks>
public sealed class RelevanceRoutingTestPostgresFactory : PostgresApiFactory
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
