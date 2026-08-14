using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The pre-merge host for the gh#855 news-embedding pgvector-read suite (gh#763, gh#109): the real API pipeline —
/// and therefore the real <c>AddTradingCopilotData</c> / <c>UseNpgsql(... UseVector())</c> registration and
/// <c>StartupTasks</c>' real <c>MigrateAsync()</c> — over a throwaway Postgres container. That migration is the
/// point: it is what creates the <c>vector</c> extension, the <c>Embeddings</c> table, and the HNSW cosine index
/// (<c>AddEmbeddingStore</c>), none of which the in-memory provider that backs the unit tier can express.
/// </summary>
/// <remarks>
/// Every <see cref="IHostedService"/> is stripped, mirroring <see cref="SalienceTestPostgresFactory"/> and
/// <see cref="NewsIngestionTestPostgresFactory"/> for the same determinism reason: this suite writes
/// <c>EmbeddingRecord</c> rows directly and reads them back through the raw <c>CosineDistance</c> query, so a
/// wall-clock <c>NewsEmbeddingHost</c> pass racing a test's own arrangement (or spending against the AI-usage
/// ledger) would make the suite flaky for no reason connected to what it guards.
/// </remarks>
public sealed class EmbeddingReadTestPostgresFactory : PostgresApiFactory
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
