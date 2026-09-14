using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Ai;

/// <summary>
/// The no-op ledger (gh#431): the deliberately-ledgerless path for tests and any context with no persistence. It
/// records nothing and never throws — the inert counterpart the composition root binds where there is no store.
/// </summary>
public class NullAiUsageLedgerTests
{
    [Fact]
    public async Task RecordAsync_ShouldDoNothing_AndNotThrow()
    {
        AiUsageEntry entry = new(
            Guid.NewGuid(),
            new AiCallCost(
                AiUsageFeature.Triage, "claude-haiku-4-5", LlmModelTier.Triage, AiUsageOutcome.Succeeded,
                42, 9, 0.0006m, TimeSpan.FromMilliseconds(1234)),
            "trace-abc",
            DateTimeOffset.UnixEpoch);

        Func<Task> act = () => NullAiUsageLedger.Instance.RecordAsync(entry, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
