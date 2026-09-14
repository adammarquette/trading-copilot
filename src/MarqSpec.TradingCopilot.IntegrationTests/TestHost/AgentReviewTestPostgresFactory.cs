using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the agent-review route suite (gh#429 ⇒ gh#402), configured as a deployment that <b>has an LLM</b>:
/// <c>Llm:ApiKey</c> is set, so <c>AiRegistration</c> binds the real <c>LlmTriggerReviewer</c> and its fail-closed
/// parsing is under test rather than stubbed. Only the outbound <see cref="ILlmProvider"/> is doubled, by
/// <see cref="AdversarialLlmProvider"/>.
/// </summary>
/// <remarks>
/// Notifications come from <see cref="NotificationHarnessPostgresFactory"/> (gh#442): the whole production chain —
/// outbox → queue → dedup → the real Pushover adapter — stays intact, and only the wire beneath it is recorded.
/// This factory previously rebuilt that chain itself, which silently deleted the outbox from the composition under
/// test; the shared base exists so that no harness can do it again.
/// </remarks>
public sealed class AgentReviewTestPostgresFactory : NotificationHarnessPostgresFactory
{
    /// <summary>The doubled model provider — feeds completions, never decisions.</summary>
    public AdversarialLlmProvider Llm { get; } = new();

    protected override void ConfigureSuiteSettings(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The presence of a key is THE reviewer switch (LlmOptions.IsConfigured). Not a real secret, and never
        // used — the provider behind it is doubled.
        builder.UseSetting("Llm:ApiKey", "test-key-not-a-secret");
    }

    protected override void ConfigureSuiteServices(IServiceCollection services)
    {
        // The one seam doubled here: the outbound model provider. The reviewer, the geometry check, the debounce
        // and the whole evaluation service remain production code — as does the notification chain.
        services.RemoveAll<ILlmProvider>();
        services.AddSingleton<ILlmProvider>(Llm);
    }
}

/// <summary>
/// The same host configured as the <b>default deployment</b>: <c>Llm:ApiKey</c> is <b>absent</b>, so
/// <c>AiRegistration</c> binds <c>NullTriggerReviewer</c> (gh#429). The provider is still doubled — not to feed it,
/// but to prove it is <b>never called</b> when no reviewer is configured.
/// </summary>
public sealed class NoReviewerTestPostgresFactory : NotificationHarnessPostgresFactory
{
    /// <summary>The doubled provider — expected to receive nothing at all on this host.</summary>
    public AdversarialLlmProvider Llm { get; } = new();

    protected override void ConfigureSuiteServices(IServiceCollection services)
    {
        services.RemoveAll<ILlmProvider>();
        services.AddSingleton<ILlmProvider>(Llm);
    }
}
