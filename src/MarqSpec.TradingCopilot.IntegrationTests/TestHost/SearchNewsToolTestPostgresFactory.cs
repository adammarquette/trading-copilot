using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the <c>search_news</c> pgvector recall suite (gh#988, of gh#987) — the real API pipeline over a
/// throwaway Postgres container, with only the outbound <b>embedding</b> seam doubled
/// (<see cref="AdversarialEmbeddingProvider"/>, the same QA-sanctioned double
/// <see cref="EmbeddingProviderDoubleTestPostgresFactory"/> uses — Cohere cannot exist pre-merge; no key, no
/// egress). <b>Everything downstream stays production code</b>: <c>PgVectorNewsSimilarity</c>'s real pgvector read
/// (including the <c>IX_Embeddings_Vector_Cosine_SoftSignal</c> partial index and the current-model filter),
/// <c>NewsRetrievalService</c>'s hydrate + rerank consumption, and <c>SearchNewsTool</c> itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rerank seam is a per-test opt-in</b> (<see cref="Reranker"/>). Left <see langword="null"/> (the default),
/// no Cohere key is configured either, so production's own <c>IsConfigured</c> switch resolves the real, keyless
/// <c>UnavailableReranker</c> — never forced — proving the passthrough (recall-order-preserving) path is what the
/// suite's own composition, not a test double, actually produces. Set before the host is first touched (before any
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}.CreateClient"/> /
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}.Services"/> access) to swap in
/// <see cref="AdversarialReranker"/> for the rerank-consumption case, so the pipeline's returned order can be
/// proven to come from the reranker rather than the untouched recall order.
/// </para>
/// <para>
/// Every always-on <see cref="IHostedService"/> is stripped, mirroring
/// <see cref="EmbeddingProviderDoubleTestPostgresFactory"/> — so no background embedding / relevance / GC pass can
/// race a test's explicit seed-then-call sequence.
/// </para>
/// <para>
/// Extends <see cref="StubbedVenuePostgresFactory"/> (not the bare <see cref="PostgresApiFactory"/>) because
/// resolving the full <c>IEnumerable&lt;IChatTool&gt;</c> set — <c>search_news</c> sits alongside
/// <c>ReadPositionsTool</c> / <c>GetQuoteTool</c>, which need a venue seam — otherwise throws at DI construction
/// time for want of a ProjectX credential; the adversarial venue double it wires is never exercised by this suite.
/// </para>
/// </remarks>
public sealed class SearchNewsToolTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The doubled embedding provider — feeds deterministic-per-text vectors for the query and reports availability.</summary>
    public AdversarialEmbeddingProvider EmbeddingProvider { get; } = new();

    /// <summary>
    /// The optional rerank double. <see langword="null"/> (the default) leaves production's own keyless
    /// <c>UnavailableReranker</c> in place; set to an <see cref="AdversarialReranker"/> before first host access to
    /// prove the pipeline reads a real reranker's order.
    /// </summary>
    public IReranker? Reranker { get; set; }

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

        services.RemoveAll<IEmbeddingProvider>();
        services.AddSingleton<IEmbeddingProvider>(EmbeddingProvider);

        if (Reranker is not null)
        {
            services.RemoveAll<IReranker>();
            services.AddSingleton(Reranker);
        }
    }
}
