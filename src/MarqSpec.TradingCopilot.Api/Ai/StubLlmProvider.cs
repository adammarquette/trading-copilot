using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The inc-3 seam default for <see cref="ILlmProvider"/> (R-4 / ADR-0008): a provider that does <b>no I/O</b> and
/// returns a canned <i>suppress</i> completion.
/// </summary>
/// <remarks>
/// It exists so the composition root always has an <see cref="ILlmProvider"/> to bind and the agent-review path is
/// exercisable end-to-end before the real client lands. Production <b>never fabricates trade geometry</b>: the stub
/// only ever suppresses, so a stub build cannot invent a suggestion. A2 swaps this for the Anthropic HTTP client
/// behind the same seam, with real <see cref="LlmUsage"/> and <see cref="LlmStopReason"/> filled in.
/// </remarks>
public sealed class StubLlmProvider : ILlmProvider
{
    // A minimal, schema-shaped body the reviewer maps to Suppress(NotWorthSurfacing) -- never a suggestion.
    private const string CannedSuppressJson = "{\"decision\":\"suppress\",\"reason\":\"stub\"}";

    /// <inheritdoc />
    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LlmCompletion(CannedSuppressJson, LlmStopReason.Completed, LlmUsage.None));
}
