using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the chat tool-layer suite (gh#930, verifying gh#925 / R-6 / ADR-0025): the real API pipeline over a
/// throwaway Postgres container, with the <b>model</b> scripted and everything else production.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is doubled, and what deliberately is not.</b> Exactly one seam is replaced — the outbound
/// <see cref="ILlmProvider"/>, by <see cref="ScriptedChatLlmProvider"/>. The tool registry, every
/// <c>IChatTool</c>, <c>ChatTurnService</c> and its round cap, the endpoint, the R-20 query filter, the AI-usage
/// ledger and the governor are all the shipped composition. <see cref="IChatRealtimeNotifier"/> is <i>wrapped</i>
/// rather than replaced (see <see cref="RecordingChatRealtimeNotifier"/>), so the production notifier and the real
/// hub stay on the path.
/// </para>
/// <para>
/// <b>The venue is here as a tripwire.</b> The inherited adversarial ProjectX venue stub is not used to drive
/// anything in this suite — a chat turn has no legitimate reason to touch it. It is present so that "no chat turn
/// ever reaches an order path" can be asserted against a recorder that <i>would</i> have counted the call
/// (<see cref="AdversarialTestProjectXVenueFactory.TotalPlacedOrderCount"/> and its siblings), rather than against a
/// name check.
/// </para>
/// <para>
/// <b>No spend budget is configured</b>, so the governor is inert and a scripted turn is never blocked short of the
/// model — the ledger cases here are about per-call rows, not about the cap (that is the gh#479 suite's subject).
/// </para>
/// </remarks>
public sealed class ChatToolLayerTestPostgresFactory : NotificationHarnessPostgresFactory
{
    /// <summary>The scripted model — the one doubled seam.</summary>
    public ScriptedChatLlmProvider Llm { get; } = new();

    /// <summary>What the production chat notifier was asked to push, per owner.</summary>
    public ChatRealtimeRecorder Realtime { get; } = new();

    /// <summary>The venue recorder a chat turn must never touch. Internal, as the stub itself is.</summary>
    internal AdversarialTestProjectXVenueFactory Venue => Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <inheritdoc />
    protected override void ConfigureSuiteSettings(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // A configured deployment is the shape under test: with a key present the production registration would bind
        // the real Anthropic client, which is exactly the seam replaced below. Fabricated, never a secret.
        builder.UseSetting("Llm:ApiKey", "integration-test-key-not-a-secret");
    }

    /// <inheritdoc />
    protected override void ConfigureSuiteServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Seam 1 — the outbound model. Everything the model's output then flows through stays production code.
        services.RemoveAll<ILlmProvider>();
        services.AddSingleton<ILlmProvider>(Llm);

        // Seam 2 — NOT a replacement. Production's notifier is resolved from its own descriptor and wrapped, so the
        // shipped per-owner routing still executes and only becomes observable. Reading the implementation type off
        // the descriptor (rather than naming it) keeps this working without reaching into the Api assembly's
        // internals — and fails loudly if production changes how the seam is registered, instead of silently
        // recording nothing.
        ServiceDescriptor existing = services.Single(descriptor => descriptor.ServiceType == typeof(IChatRealtimeNotifier));
        Type implementation = existing.ImplementationType ?? throw new InvalidOperationException(
            "gh#930: IChatRealtimeNotifier is no longer registered by implementation type, so the production notifier "
            + "can no longer be wrapped. Update this harness — do not swap the seam for a stub, which would delete the "
            + "shipped per-owner routing from the composition under test.");
        services.Remove(existing);
        services.AddSingleton<IChatRealtimeNotifier>(provider => new RecordingChatRealtimeNotifier(
            (IChatRealtimeNotifier)ActivatorUtilities.CreateInstance(provider, implementation), Realtime));
    }
}
