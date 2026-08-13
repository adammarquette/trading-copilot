using System.Globalization;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the R-4 suggestion-throttle suite (gh#745 ⇒ gh#551 / gh#588 / gh#587, R-4 / R-5, ADR-0008): a
/// deployment configured with <b>both</b> an LLM and the <b>throttle opted in</b>. <c>Llm:ApiKey</c> is set, so
/// <c>AiRegistration</c> binds the real <c>LlmTriggerReviewer</c>; <c>Suggestions:ThrottleEnabled</c> is set, so the
/// scan consults the pure <c>SuggestionThrottle</c> before waking the reviewer. Only the outbound
/// <see cref="ILlmProvider"/> is doubled, by <see cref="AdversarialLlmProvider"/> — the reviewer, the
/// <c>DailyRealizedReader</c>, the <c>SuggestionThrottle</c> policy, the scan and the whole notification chain stay
/// the shipped composition.
/// </summary>
/// <remarks>
/// <para>
/// <b>The throttle knobs are fixed by the factory; cases vary the seeded day-state.</b> <c>TriggerEvaluationService</c>
/// reads the threshold / cap / floor <b>once, in its constructor</b>, so a suite cannot re-tune them mid-run — exactly
/// as the AI-spend suite fixes its budget and varies the seeded spend. Every case therefore varies the seeded realized
/// P&amp;L and issued-suggestion count against these fixed knobs, never the config.
/// </para>
/// <para>
/// The knobs are chosen so a couple of seeded trades reach every regime boundary: with the Full/Throttled band at
/// <see cref="ThrottleThresholdFraction"/> = 0.5 of a small governor, a mid-day loss drops headroom into the Throttled
/// band and a loss that reaches the governor into Suppressed; the Full cap of <see cref="ThrottleFullWindowCap"/> = 3
/// is small enough that a handful of issued rows exhausts it, and the conviction floor of
/// <see cref="ThrottleConvictionFloor"/> = 70 sits above and below the two confidences the model double reports.
/// </para>
/// </remarks>
public sealed class SuggestionThrottleTestPostgresFactory : NotificationHarnessPostgresFactory
{
    /// <summary>The headroom fraction at/above which issuance is Full; below it, throttling begins (gh#588).</summary>
    public const decimal ThrottleThresholdFraction = 0.5m;

    /// <summary>The per-window issuance ceiling at Full — throttling only ever scales this down (gh#588).</summary>
    public const int ThrottleFullWindowCap = 3;

    /// <summary>The minimum model confidence a candidate needs while throttled — "higher-conviction only" (gh#588).</summary>
    public const int ThrottleConvictionFloor = 70;

    /// <summary>The doubled model provider — feeds completions and token usage, never decisions.</summary>
    public AdversarialLlmProvider Llm { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureSuiteSettings(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The presence of a key is the reviewer switch (LlmOptions.IsConfigured); the provider behind it is doubled.
        builder.UseSetting("Llm:ApiKey", "test-key-not-a-secret");

        // Opt the throttle in — it is inert by default (SuggestionOptions.ThrottleEnabled == false), which is a
        // byte-for-byte no-op the AgentReview suite already covers. The knobs are invariant-formatted so the decimal
        // binds identically regardless of the CI agent's culture.
        Setting(builder, nameof(SuggestionOptions.ThrottleEnabled), "true");
        Setting(builder, nameof(SuggestionOptions.ThrottleThresholdFraction), ThrottleThresholdFraction.ToString(CultureInfo.InvariantCulture));
        Setting(builder, nameof(SuggestionOptions.ThrottleFullWindowCap), ThrottleFullWindowCap.ToString(CultureInfo.InvariantCulture));
        Setting(builder, nameof(SuggestionOptions.ThrottleConvictionFloor), ThrottleConvictionFloor.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    protected override void ConfigureSuiteServices(IServiceCollection services)
    {
        // The one seam doubled here: the outbound model provider. The reviewer, the throttle policy, the realized-P&L
        // reader, the DI-composed evaluation service and the whole notification chain remain production code.
        services.RemoveAll<ILlmProvider>();
        services.AddSingleton<ILlmProvider>(Llm);
    }

    private static void Setting(IWebHostBuilder builder, string key, string value) =>
        builder.UseSetting($"{SuggestionOptions.SectionName}:{key}", value);
}
