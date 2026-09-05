using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the chat-turn-guard contention suite (gh#1118, of gh#1106 / R-6): the real API pipeline over a
/// throwaway Postgres container, with only the outbound model scripted. <c>IChatTurnGuard</c>, the
/// <c>/conversations/{id}/turns</c> endpoint, the R-20 query filter, and <c>IAccountEntryGuard</c> beside it are all
/// the shipped composition — the serialization under test is the real one, evaluated by a real Postgres advisory
/// lock, not an in-process double.
/// </summary>
/// <remarks>
/// <b>Exactly one seam is replaced</b> — the outbound <see cref="ILlmProvider"/>, by
/// <see cref="ScriptedChatLlmProvider"/> (the same double the chat tool-layer suite uses, gh#930), so a suite can
/// control how long a turn's model call takes and interleave a peer request from inside it
/// (<see cref="ScriptedChatLlmProvider.OnCall"/>) while <c>ChatTurnGuard</c>'s advisory lock is still held. The
/// venue stub inherited from <see cref="StubbedVenuePostgresFactory"/> is unused here — a chat turn never reaches
/// it — but this suite also drives <c>IAccountEntryGuard</c> directly (the lock-space-disjointness case), so
/// leaving the production venue composition intact is what keeps that guard resolvable exactly as it ships.
/// </remarks>
public sealed class ChatTurnGuardTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The scripted model — the one doubled seam.</summary>
    public ScriptedChatLlmProvider Llm { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // A configured deployment is the shape under test: with a key present the production registration would
        // bind the real Anthropic client, which is exactly the seam replaced below. Fabricated, never a secret.
        builder.UseSetting("Llm:ApiKey", "integration-test-key-not-a-secret");
    }

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        services.RemoveAll<ILlmProvider>();
        services.AddSingleton<ILlmProvider>(Llm);
    }
}
