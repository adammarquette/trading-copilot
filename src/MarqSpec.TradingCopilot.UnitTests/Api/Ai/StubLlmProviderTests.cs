using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The inc-3 <see cref="ILlmProvider"/> stub (gh#402, ADR-0008): no I/O, and only ever a canned <i>suppress</i>. The
/// property that matters is that a stub build cannot fabricate a suggestion — production never invents trade geometry.
/// </summary>
public class StubLlmProviderTests
{
    [Fact]
    public async Task CompleteAsync_ShouldReturnACleanCompletionWithNoUsage_ThatCannotBecomeASuggestion()
    {
        StubLlmProvider provider = new();
        LlmRequest request = new(
            LlmModelTier.Triage,
            "system",
            [new LlmMessage(LlmRole.User, "hi")],
            LlmResponseFormat.Text);

        LlmCompletion completion = await provider.CompleteAsync(request, CancellationToken.None);

        completion.StopReason.Should().Be(LlmStopReason.Completed);
        completion.Usage.Should().Be(LlmUsage.None);
        completion.Text.Should().Contain("suppress"); // canned decline -- never proposes geometry
    }

    [Fact]
    public async Task StreamAsync_ShouldEmitTheCannedTextAsOneDelta_AndReturnTheSameCompletion()
    {
        StubLlmProvider provider = new();
        LlmRequest request = new(
            LlmModelTier.Triage,
            "system",
            [new LlmMessage(LlmRole.User, "hi")],
            LlmResponseFormat.Text);

        List<string> deltas = [];
        LlmCompletion completion = await provider.StreamAsync(
            request, (delta, _) => { deltas.Add(delta); return Task.CompletedTask; }, CancellationToken.None);

        // Streaming shape parity with CompleteAsync: same completion, and it streamed exactly what it returned.
        completion.StopReason.Should().Be(LlmStopReason.Completed);
        completion.Usage.Should().Be(LlmUsage.None);
        deltas.Should().ContainSingle().Which.Should().Be(completion.Text);
        completion.Text.Should().Contain("suppress");
    }
}
