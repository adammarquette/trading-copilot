using System.Globalization;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the AI-spend suite (gh#479 ⇒ gh#448 / gh#431 / gh#478, ADR-0008): a deployment configured with
/// <b>both</b> an LLM and an <b>active spend governor</b>. <c>Llm:ApiKey</c> is set, so <c>AiRegistration</c> binds
/// the real <c>LlmTriggerReviewer</c> (its fail-closed parsing under test); <c>Governor:DailyBudgetUsd</c> is set,
/// so the <c>AiSpendGovernor</c> enforces. Only the outbound <see cref="ILlmProvider"/> is doubled, by
/// <see cref="AdversarialLlmProvider"/> — the reviewer, the ledger, the scan and the whole notification chain stay
/// production code.
/// </summary>
/// <remarks>
/// <b>One budget per factory.</b> <c>TriggerEvaluationService</c> reads the budget <b>once, in its constructor</b>
/// (<c>GovernorOptions.ToBudget()</c>), so a suite cannot re-cap mid-run. Every case therefore varies the
/// <b>seeded spend</b> against this fixed <see cref="DailyBudgetUsd"/>, never the config — exactly the constraint
/// gh#479 calls out. The value is deliberately small so a single scripted fire can exhaust it (see
/// <see cref="AdversarialLlmProvider.ReportsUsage"/>), letting the block / accrual boundary be reached with a
/// couple of triggers rather than thousands of realistic reviews.
/// </remarks>
public sealed class AiSpendTestPostgresFactory : NotificationHarnessPostgresFactory
{
    /// <summary>
    /// The one daily budget (USD) this factory declares. At the pinned triage rates ($1 / $5 per million input /
    /// output tokens, <c>LlmOptions</c>), a single fire reporting 1M input + 1M output tokens prices at $6.00 — over
    /// this cap — so a suite can drive the governor to Block by scripting usage, never by touching the config.
    /// </summary>
    public const decimal DailyBudgetUsd = 5.00m;

    /// <summary>The doubled model provider — feeds completions and token usage, never decisions.</summary>
    public AdversarialLlmProvider Llm { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureSuiteSettings(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The presence of a key is the reviewer switch (LlmOptions.IsConfigured); the provider behind it is doubled.
        builder.UseSetting("Llm:ApiKey", "test-key-not-a-secret");

        // The presence of a POSITIVE budget is the governor switch (GovernorOptions.IsActive). Invariant-formatted so
        // the decimal binds identically regardless of the CI agent's culture.
        builder.UseSetting(
            $"{GovernorOptions.SectionName}:{nameof(GovernorOptions.DailyBudgetUsd)}",
            DailyBudgetUsd.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    protected override void ConfigureSuiteServices(IServiceCollection services)
    {
        // The one seam doubled here: the outbound model provider. The reviewer, the geometry check, the debounce,
        // the DI-composed AiUsageLedger and the whole evaluation service remain production code.
        services.RemoveAll<ILlmProvider>();
        services.AddSingleton<ILlmProvider>(Llm);
    }
}
