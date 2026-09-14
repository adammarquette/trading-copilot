using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The pre-merge host for the gh#362 importance-starring suite (gh#27, ADR-0014): the real API pipeline over a
/// throwaway Postgres container. Salience is venue-independent by definition (the read is <c>/api/news</c>, never
/// <c>ITradingVenue</c>), so no venue stub is registered — the only intervention is stripping every
/// <see cref="IHostedService"/>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="NewsIngestionTestPostgresFactory"/> (gh#360) and <see cref="RelevanceRoutingTestPostgresFactory"/>
/// (gh#361) for the same determinism reason: the suite seeds <c>NewsRecord</c> rows directly, already
/// relevance-resolved (<c>MatchedInstruments</c> / <c>MatchedTopics</c> set on the row), so the wall-clock
/// <c>NewsIngestionHost</c> / <c>NewsRelevanceHost</c> / <c>BarBackfillHost</c> timers would otherwise write or
/// re-resolve against a fixture a test is mid-arrangement on.
/// </remarks>
public sealed class SalienceTestPostgresFactory : PostgresApiFactory
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
