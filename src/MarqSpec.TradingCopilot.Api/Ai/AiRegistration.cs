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
/// <see cref="ILlmProvider"/> defaults to the no-I/O <see cref="StubLlmProvider"/> for this increment; A2 swaps the
/// real Anthropic client behind the same seam. <b>Enforcement lives below the model:</b> nothing bound here can place
/// or size an order — the reviewer only proposes.
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

        // The provider seam (ADR-0008): the stub does NO I/O and only ever suppresses, so a stub build cannot
        // fabricate a suggestion. A2 replaces it with the real Anthropic client behind this same interface.
        services.AddSingleton<ILlmProvider, StubLlmProvider>();

        // Both concrete reviewers are registered; the INTERFACE resolves to exactly one of them by whether a key is
        // present. The reviewer is ALWAYS bound -- an unconfigured deployment gets the inert-but-announced one, not a
        // missing dependency, so the route can never be silently absent (the null-notification-channel posture).
        services.AddScoped<LlmTriggerReviewer>();
        services.AddScoped<NullTriggerReviewer>();
        services.AddScoped<ITriggerReviewer>(provider =>
            provider.GetRequiredService<IOptions<LlmOptions>>().Value.IsConfigured
                ? provider.GetRequiredService<LlmTriggerReviewer>()
                : provider.GetRequiredService<NullTriggerReviewer>());

        return services;
    }
}
