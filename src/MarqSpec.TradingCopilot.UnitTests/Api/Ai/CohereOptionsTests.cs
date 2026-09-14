using MarqSpec.TradingCopilot.Api.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The Cohere cost + model config (gh#403, gh#975, ADR-0008): <see cref="CohereOptions"/> is the single
/// env-overridable source of truth for the embed and rerank model ids and their pinned rates. Rerank bills per
/// <b>search</b> (one call = one search unit), embed per token — two different rates, so the arithmetic of each is
/// pinned here. A wrong rate silently mis-estimates every ledger row, so these tests are the guard.
/// </summary>
public class CohereOptionsTests
{
    // --- Rerank pricing: per THOUSAND searches (gh#975) ---

    [Fact]
    public void EstimateRerankCost_ShouldPricePerThousandSearches()
    {
        CohereOptions options = new();

        // The pinned default is $2.00 / 1000 searches, so a thousand searches bill exactly $2.00 and one bills $0.002.
        options.EstimateRerankCost(1000).Should().Be(2.00m);
        options.EstimateRerankCost(1).Should().Be(0.002m);
    }

    [Fact]
    public void EstimateRerankCost_ShouldHonorAnOverriddenRate()
    {
        CohereOptions options = new() { UsdPerThousandSearches = 5.00m };

        options.EstimateRerankCost(1000).Should().Be(5.00m);
    }

    [Fact]
    public void EstimateRerankCost_ShouldBeZero_WhenThereAreNoSearches()
    {
        new CohereOptions().EstimateRerankCost(0).Should().Be(0m);
    }

    [Fact]
    public void EstimateRerankCost_ShouldFloorNegativeSearchesAtZero()
    {
        // A degraded call carries zero searches; a corrupt/negative count must never produce a NEGATIVE cost that
        // would net against real spend in a windowed sum.
        new CohereOptions().EstimateRerankCost(-5).Should().Be(0m);
    }

    [Fact]
    public void RerankModel_ShouldDefaultToTheCohereRerankModel()
    {
        new CohereOptions().RerankModel.Should().Be("rerank-english-v3.0");
    }

    // --- Embed pricing: per MILLION tokens (gh#403), pinned here alongside the rerank rate ---

    [Fact]
    public void EstimateCost_ShouldPricePerMillionTokens()
    {
        CohereOptions options = new();

        options.EstimateCost(1_000_000).Should().Be(0.10m, "the pinned embed rate is $0.10 / 1M input tokens");
    }

    [Fact]
    public void EstimateCost_ShouldBeZero_WhenThereAreNoTokens()
    {
        new CohereOptions().EstimateCost(0).Should().Be(0m);
    }
}
