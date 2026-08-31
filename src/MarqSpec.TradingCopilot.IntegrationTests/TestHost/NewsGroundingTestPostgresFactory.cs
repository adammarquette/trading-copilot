using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the always-on news-grounding suite (gh#996, verifying gh#995 / R-6, ADR-0027) — the real API
/// pipeline over a throwaway Postgres container, with only the two <b>outbound third-party</b> seams doubled: the
/// model (<see cref="ScriptedChatLlmProvider"/>) and the embedding provider (<see cref="AdversarialEmbeddingProvider"/>,
/// the same QA-sanctioned double <c>EmbeddingProviderDoubleTestPostgresFactory</c> uses — Cohere cannot exist
/// pre-merge; no key, no egress). <b>Everything downstream stays production code</b>: <c>PgVectorNewsSimilarity</c>'s
/// real pgvector read, <c>UnavailableReranker</c>'s real keyless passthrough (no Cohere key is configured, so the
/// production switch itself resolves it — never forced), <c>NewsRetrievalService</c>, <c>ChatTurnService</c>'s
/// grounding envelope, and the endpoint's own fail-open catch around retrieval.
/// </summary>
/// <remarks>
/// <b>No spend budget is configured</b>, so the governor is inert and grounding is never suppressed short of the
/// embedding provider / pgvector read themselves — the threshold-skip and the blocked-turn cases are the sibling
/// <see cref="NewsGroundingSpendTestPostgresFactory"/>'s subject (a budget is read once per host, gh#479's "one
/// budget per factory" constraint).
/// </remarks>
public sealed class NewsGroundingTestPostgresFactory : NotificationHarnessPostgresFactory
{
    /// <summary>The scripted model — the one doubled outbound LLM seam.</summary>
    public ScriptedChatLlmProvider Llm { get; } = new();

    /// <summary>
    /// The doubled embedding provider (gh#888's double, reused here): feeds a deterministic vector per exact text and
    /// reports <see cref="AdversarialEmbeddingProvider.IsAvailable"/> / <see cref="AdversarialEmbeddingProvider.Model"/>
    /// — never a retrieval decision. <c>PgVectorNewsSimilarity</c>'s real <c>Model ==</c> filter and cosine-distance
    /// read run as production code against whatever this double reports.
    /// </summary>
    public AdversarialEmbeddingProvider EmbeddingProvider { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureSuiteSettings(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // A configured deployment is the shape under test: with a key present, production binds the real Anthropic
        // client — exactly the seam replaced below. Fabricated, never a secret.
        builder.UseSetting("Llm:ApiKey", "integration-test-key-not-a-secret");
    }

    /// <inheritdoc />
    protected override void ConfigureSuiteServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<ILlmProvider>();
        services.AddSingleton<ILlmProvider>(Llm);

        services.RemoveAll<IEmbeddingProvider>();
        services.AddSingleton<IEmbeddingProvider>(EmbeddingProvider);

        // IReranker and INewsEmbeddingSimilarity are left exactly as AiRegistration / Program.cs compose them: no
        // Cohere key is set above, so the production IsConfigured switch itself resolves UnavailableReranker (the
        // real keyless passthrough) rather than this harness forcing it — and PgVectorNewsSimilarity stays the real
        // pgvector read throughout, the suite's whole point.
    }
}
