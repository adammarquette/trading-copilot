using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        // The AIUsage spend ledger (gh#431): always the real one (stateless, safe unconfigured -- it is only reached
        // when a reviewer produced a non-null Cost, and it fails open). Singleton -- it holds only the context options.
        services.AddSingleton<IAiUsageLedger, AiUsageLedger>();

        return services;
    }
}
