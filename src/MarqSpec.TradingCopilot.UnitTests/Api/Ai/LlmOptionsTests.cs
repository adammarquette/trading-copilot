using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The LLM cost + model config (gh#431, ADR-0008): <see cref="LlmOptions"/> is the single env-overridable source of
/// truth for the per-tier model id the provider bills and the per-tier input/output rates the ledger prices a call
/// at. A wrong rate here silently mis-estimates every usage row's cost, so the arithmetic is pinned by these tests.
/// </summary>
public class LlmOptionsTests
{
    [Fact]
    public void EstimateCost_ShouldPriceInputAndOutputSeparatelyPerTier()
    {
        LlmOptions options = new();

        // Per-million rates: Triage $1 in / $5 out, Deep $3 in / $15 out. One million of each token bills exactly the
        // sum of the two per-million rates (input * inRate + output * outRate) / 1_000_000.
        options.EstimateCost(LlmModelTier.Triage, 1_000_000, 1_000_000).Should().Be(6.00m);  // 1.00 + 5.00
        options.EstimateCost(LlmModelTier.Deep, 1_000_000, 1_000_000).Should().Be(18.00m);   // 3.00 + 15.00
    }

    [Fact]
    public void EstimateCost_ShouldBeZero_WhenThereAreNoTokens()
    {
        LlmOptions options = new();

        options.EstimateCost(LlmModelTier.Triage, 0, 0).Should().Be(0m);
    }

    [Fact]
    public void EstimateCost_ShouldFloorNegativeTokensAtZero()
    {
        // A Failed call carries zero tokens; a corrupt/negative count must never produce a NEGATIVE cost that would
        // net against real spend in the governor's windowed sum -- each side floors at zero before pricing.
        LlmOptions options = new();

        options.EstimateCost(LlmModelTier.Triage, -100, -100).Should().Be(0m);
        options.EstimateCost(LlmModelTier.Triage, -100, 1_000_000).Should().Be(5.00m); // input floored, output priced
    }

    [Theory]
    [InlineData(LlmModelTier.Triage, "claude-haiku-4-5")]
    [InlineData(LlmModelTier.Deep, "claude-sonnet-5")]
    public void ModelFor_ShouldReturnTheTierModel(LlmModelTier tier, string expected)
    {
        new LlmOptions().ModelFor(tier).Should().Be(expected);
    }
}
