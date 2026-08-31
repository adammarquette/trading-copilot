using System.Globalization;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the news-grounding <b>spend</b> suite (gh#996, verifying gh#995's governor interaction, ADR-0027 /
/// ADR-0008): the same two doubled outbound seams as <see cref="NewsGroundingTestPostgresFactory"/>, plus an
/// <b>active spend governor</b> (mirrors <c>AiSpendTestPostgresFactory</c>). <c>Governor:DailyBudgetUsd</c> is set,
/// so <c>ChatEndpoints.TurnAsync</c>'s gate — and the threshold-suppression / hard-block it drives grounding
/// through — is live.
/// </summary>
/// <remarks>
/// <b>One budget per factory (gh#479's constraint).</b> <c>IOptions&lt;GovernorOptions&gt;</c> is bound once at host
/// build, so a suite cannot re-cap mid-run. Every case therefore varies the <b>seeded spend</b> against this fixed
/// <see cref="DailyBudgetUsd"/>, never the config — the default <see cref="GovernorOptions.AlertThresholdFraction"/>
/// (0.8) is left untouched, so the pre-alert threshold sits at 80% of <see cref="DailyBudgetUsd"/>.
/// </remarks>
public sealed class NewsGroundingSpendTestPostgresFactory : NotificationHarnessPostgresFactory
{
    /// <summary>
    /// The one daily budget (USD) this factory declares. Small enough that a couple of seeded rows reach the
    /// pre-alert threshold (80% = 8.00) or the hard cap without needing a realistic volume of spend.
    /// </summary>
    public const decimal DailyBudgetUsd = 10.00m;

    /// <summary>The scripted model — the one doubled outbound LLM seam.</summary>
    public ScriptedChatLlmProvider Llm { get; } = new();

    /// <summary>The doubled embedding provider — feeds a deterministic vector per exact text, never a decision.</summary>
    public AdversarialEmbeddingProvider EmbeddingProvider { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureSuiteSettings(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("Llm:ApiKey", "integration-test-key-not-a-secret");

        // The presence of a POSITIVE budget is the governor switch (GovernorOptions.IsActive). Invariant-formatted so
        // the decimal binds identically regardless of the CI agent's culture.
        builder.UseSetting(
            $"{GovernorOptions.SectionName}:{nameof(GovernorOptions.DailyBudgetUsd)}",
            DailyBudgetUsd.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    protected override void ConfigureSuiteServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<ILlmProvider>();
        services.AddSingleton<ILlmProvider>(Llm);

        services.RemoveAll<IEmbeddingProvider>();
        services.AddSingleton<IEmbeddingProvider>(EmbeddingProvider);
    }
}
