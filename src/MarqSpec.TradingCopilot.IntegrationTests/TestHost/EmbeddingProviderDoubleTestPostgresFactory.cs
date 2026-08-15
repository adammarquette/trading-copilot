using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The shared host for the gh#888 model-scoped-read suite and the gh#855 nearest-N-by-model rewire it drives
/// (paired QA for gh#881, sub-issue of gh#763). Strips every always-on <see cref="IHostedService"/>, mirroring
/// <see cref="EmbeddingReadTestPostgresFactory"/> (that type is <c>sealed</c>, so this duplicates its few lines
/// rather than touching it) — above all <c>NewsEmbeddingHost</c>, which would otherwise run its own passes on
/// the wall clock underneath a test's own explicit <c>EmbedPendingAsync</c> call. The one seam doubled on top —
/// <see cref="IEmbeddingProvider"/> — is <see cref="AdversarialEmbeddingProvider"/>, whose <c>Model</c> a test
/// can move mid-run (see that type's remarks for why a real <c>CohereEmbeddingProvider</c> cannot serve the
/// gh#888 self-heal case). Everything downstream of the seam — <c>PgVectorNewsSimilarity</c>'s reads and
/// <c>NewsEmbeddingService</c>'s candidacy / upsert logic — stays production code under test.
/// </summary>
public sealed class EmbeddingProviderDoubleTestPostgresFactory : PostgresApiFactory
{
    /// <summary>The doubled embedding provider — feeds vectors and reports the current model, never a decision.</summary>
    public AdversarialEmbeddingProvider Provider { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        foreach (ServiceDescriptor hosted in services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                     .ToList())
        {
            services.Remove(hosted);
        }

        services.RemoveAll<IEmbeddingProvider>();
        services.AddSingleton<IEmbeddingProvider>(Provider);
    }
}
