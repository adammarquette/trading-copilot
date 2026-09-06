using MarqSpec.TradingCopilot.Api.Chat;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Binds the AI seam for the agent-review trigger route (R-4 / ADR-0008): the provider-neutral
/// <see cref="ILlmProvider"/> and the <see cref="ITriggerReviewer"/> the scan invokes on a fired agent-review trigger.
/// </summary>
/// <remarks>
/// <para>
/// The reviewer is <b>always bound</b>, never optional. When <see cref="LlmOptions.IsConfigured"/> is false the
/// binding resolves the honest inert <see cref="NullTriggerReviewer"/> (a fired setup is still journaled and the
/// operator is still told), and when a key is present it resolves the real <see cref="LlmTriggerReviewer"/>. That
/// single switch keeps the route from ever silently vanishing — the same posture as the null notification channel.
/// </para>
/// <para>
/// <see cref="ILlmProvider"/> resolves to the real <see cref="AnthropicLlmProvider"/> when a key is present (A2,
/// gh#423) and the no-I/O <see cref="StubLlmProvider"/> otherwise — the <i>same</i> switch that picks the reviewer,
/// so a configured deployment gets a live model and an unconfigured one cannot fabricate a suggestion. <b>Enforcement
/// lives below the model:</b> nothing bound here can place or size an order — the reviewer only proposes.
/// </para>
/// </remarks>
public static class AiRegistration
{
    /// <summary>Adds the LLM provider and the always-bound trigger reviewer.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">The configuration the <c>Llm</c> section is bound from.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddTradingCopilotAi(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.Configure<LlmOptions>(config.GetSection(LlmOptions.SectionName));

        // The provider seam (ADR-0008). The real Anthropic client is a typed HttpClient (so the factory owns handler
        // rotation + the timeout); the stub does NO I/O and only ever suppresses. The INTERFACE resolves to exactly
        // one of them by whether a key is present -- a stub build cannot fabricate a suggestion, and a configured one
        // wakes a live model. The key never leaves the provider (header only).
        services.AddHttpClient<AnthropicLlmProvider>((provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<LlmOptions>>().Value.TimeoutSeconds))
            // Keep "the key is never in a log" true by construction: the HttpClient factory's request-header logging
            // (active at Trace) would otherwise emit x-api-key in cleartext. Redact it regardless of log level.
            .RedactLoggedHeaders(["x-api-key"]);
        services.AddSingleton<StubLlmProvider>();
        services.AddTransient<ILlmProvider>(provider =>
            provider.GetRequiredService<IOptions<LlmOptions>>().Value.IsConfigured
                ? provider.GetRequiredService<AnthropicLlmProvider>()
                : provider.GetRequiredService<StubLlmProvider>());

        // Both concrete reviewers are registered; the INTERFACE resolves to exactly one of them by whether a key is
        // present. The reviewer is ALWAYS bound -- an unconfigured deployment gets the inert-but-announced one, not a
        // missing dependency, so the route can never be silently absent (the null-notification-channel posture).
        services.AddScoped<LlmTriggerReviewer>();
        services.AddScoped<NullTriggerReviewer>();
        services.AddScoped<ITriggerReviewer>(provider =>
            provider.GetRequiredService<IOptions<LlmOptions>>().Value.IsConfigured
                ? provider.GetRequiredService<LlmTriggerReviewer>()
                : provider.GetRequiredService<NullTriggerReviewer>());

        // The deep-tier enrichment source (gh#476): the scan-side seam that assembles the numeric market context the
        // escalated deep call reads. SCOPED, not singleton -- it injects the *scoped* TradingCopilotDbContext, so a
        // singleton would be a captive dependency failing ValidateScopes at startup (the AiUsageLedger lesson). Always
        // bound: it is safe unconfigured (only the deep render reads its output, and it fails open on the scan side).
        services.AddScoped<IReviewEnrichmentSource, ReviewEnrichmentSource>();

        // The AIUsage spend ledger (gh#431): always the real one (stateless, safe unconfigured -- it is only reached
        // when a reviewer produced a non-null Cost, and it fails open). SCOPED, not singleton: it injects the
        // *scoped* DbContextOptions<TradingCopilotDbContext>, so a singleton would be a CAPTIVE DEPENDENCY that fails
        // the host's ValidateScopes / ValidateOnBuild at startup (Development) -- the API and the safety-critical
        // trigger scan would never boot. Matches every other DbContextOptions consumer (the trigger scan, OCO-exit,
        // ...); its only consumer, the scoped TriggerEvaluationService, resolves it per host scope, and the ledger
        // still builds a fresh owner-scoped context per write.
        services.AddScoped<IAiUsageLedger, AiUsageLedger>();

        // The platform-level AI-spend governor (gh#448, ADR-0008): a PURE gate the trigger scan consults before an
        // agent-review LLM call, capping deployment-wide daily spend against the operator's budget -- the R-5 daily
        // risk-governor mirror, one layer above the model (it caps WHETHER a call is made, not what it proposes).
        // Singleton and safe: it is stateless (the caller hands it the windowed spend), so unlike the ledger it holds
        // no scoped DbContextOptions and is no captive dependency. Validated on start (the FlattenOptions /
        // SalienceOptions idiom); opt-in and INERT until a budget is configured (a fresh deploy keeps the no-cap
        // status quo, never a surprise pause).
        services.AddOptions<GovernorOptions>()
            .Bind(config.GetSection(GovernorOptions.SectionName))
            .Validate(
                options => options.DailyBudgetUsd is null or > 0m && options.AlertThresholdFraction is > 0m and <= 1m,
                "Governor: DailyBudgetUsd must be null or positive; AlertThresholdFraction must be in (0, 1].")
            .ValidateOnStart();
        services.AddSingleton<IAiSpendGovernor, AiSpendGovernor>();

        // The R-4 suggestion throttle (gh#551): a pure, stateless policy the scan consults per fire — like the
        // AI-spend governor beside it, a singleton with no state of its own. Inert until SuggestionOptions.Throttle*
        // opts an account in; the scan reads the account's headroom and decides Full / Throttled / Suppressed.
        services.AddSingleton<ISuggestionThrottle, SuggestionThrottle>();

        // The read-only chat tools (gh#925): the co-pilot may call these to ground its reply in the operator's real
        // data. SCOPED -- each holds the scoped DbContext (R-20 owner-filtered), so a singleton would be a captive
        // dependency failing ValidateScopes at startup. Every tool is read-only by construction (IChatTool): the model
        // reads the journal or a quote, but the tool layer reaches no order / write path (enforcement below the model).
        // ChatTurnService resolves IEnumerable<IChatTool> -- the full registered set -- and offers it to the model.
        services.AddScoped<IChatTool, QueryJournalTool>();
        services.AddScoped<IChatTool, GetQuoteTool>();
        services.AddScoped<IChatTool, ReadPositionsTool>();

        // The FIRST write chat tool (gh#1134 of gh#1059, ADR-0025): the co-pilot may now PROPOSE a setup. It is still
        // not an execution path -- generate_suggestion stages an Active Suggestion the operator must take themselves,
        // and the risk gate runs then, below the model. Like its read siblings it reaches no order / venue / gate
        // type; ChatToolBoundaryTests pins that over EVERY IChatTool by reflection, and pins this one's constructor
        // set exactly, so a future tool is covered by construction rather than by remembering to add it. SCOPED: it
        // holds the request's ICurrentUser (R-20) and the scoped DbContextOptions, so a singleton would be a captive
        // dependency failing ValidateScopes at startup. It builds its OWN owner-scoped context per call (the
        // AiUsageLedger idiom), so a staged proposal never enrols in the chat endpoint's conversation transaction.
        services.AddScoped<IChatTool, GenerateSuggestionTool>();

        // The shared CROSS-KIND retrieval pipeline (gh#1065, generalising gh#995; ADR-0027 / ADR-0025 / ADR-0008):
        // embed the query once -> recall each asked kind -> hydrate -> merge nearest-first -> rerank, ledgering its own
        // embed (Embed) + rerank (Chat) spend stamped to the operator. The FIRST IReranker consumer's core (gh#987),
        // shared by the search_news tool AND always-on chat grounding rather than two copies. SCOPED -- it injects the
        // scoped DbContext (R-20 owner-filtered) + the scoped ledger, so a singleton would be a captive dependency
        // failing ValidateScopes at startup. That scoped, tenant-filtered DbContext is not incidental: the embedding
        // store is deployment-global, so the owner-scoped kinds (suggestions, journal entries) are scoped by the
        // HYDRATE, and a context registered any other way would defeat R-20. Read-only by construction: it injects only
        // read / compute seams (embed provider, ranked recall, reranker), the read-only DbContext, and the fail-open
        // spend ledger, reaching no order / write path. The clock is the shared TimeProvider (TryAdd so this
        // registration stands alone even without the notification host that also adds it).
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IContextRetrievalService, ContextRetrievalService>();

        // search_news (gh#987, ADR-0025): a thin read-only IChatTool adapter over the pipeline above. SCOPED like its
        // siblings -- it holds the scoped pipeline, so a singleton would be a captive dependency. ChatTurnService
        // resolves IEnumerable<IChatTool> -- the full registered set -- and offers it to the model.
        services.AddScoped<IChatTool, SearchNewsTool>();

        // The grounded chat turn (gh#906 / gh#925, R-6): runs the model over a conversation's history, runs any
        // read-only tool calls in a bounded loop, and prices every call. Scoped like the reviewer beside it — it wraps
        // the transient ILlmProvider and holds no state of its own; the chat endpoint resolves it per request.
        // Enforcement lives below the model: it converses and reads, never proposes an order.
        services.AddScoped<IChatTurnService, ChatTurnService>();

        // The rerank seam (gh#975, ADR-0008, engineering §2): the provider-neutral IReranker — Cohere's cross-encoder
        // reranker when a Cohere key is present, the KEYLESS passthrough UnavailableReranker otherwise. The SAME switch
        // shape as the embed provider (register concrete + keyless default + interface-by-IsConfigured) and the LLM
        // provider above — placed HERE, not Program.cs where the embed switch sits, so the default-vs-provider
        // selection is unit-testable (the LlmProvider posture). It lands AHEAD of any consumer: nothing calls
        // RerankAsync yet and no AIUsage row is written (deferred to the first retrieval consumer, exactly as the embed
        // ledger write was deferred to gh#377 / gh#436). CohereOptions is bound here so the switch reads IsConfigured;
        // the embed seam in Program.cs binds the same "Cohere" section too, which is idempotent. The rerank provider
        // reuses the named "cohere" HttpClient the embed seam registers. Metrics are REQUIRED, never optional — an
        // unmetered call is invisible spend on the operator's own key (the gh#403 posture). Singletons like the embed
        // provider + meter: stateless with no scoped dependency, so no captive-dependency risk.
        services.Configure<CohereOptions>(config.GetSection(CohereOptions.SectionName));
        services.AddSingleton<RerankMetrics>();
        services.AddSingleton<IRerankMetrics>(provider => provider.GetRequiredService<RerankMetrics>());
        services.AddSingleton<UnavailableReranker>();
        services.AddSingleton<CohereRerankProvider>();
        services.AddSingleton<IReranker>(provider =>
            provider.GetRequiredService<IOptions<CohereOptions>>().Value.IsConfigured
                ? provider.GetRequiredService<CohereRerankProvider>()
                : provider.GetRequiredService<UnavailableReranker>());

        return services;
    }
}
