using MarqSpec.TradingCopilot.Api.MarketData;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// Symbol normalisation for the ingestion host (gh#13): ingestion is opt-in, so the configured watchlist must
/// collapse to <b>empty</b> — "disabled" — when nothing real is configured, including the blank array element
/// compose produces for an unset variable.
/// </summary>
public class MarketDataIngestionHostTests
{
    [Fact]
    public void NormalizeSymbols_ShouldKeepRealSymbols_Trimmed()
    {
        string[] result = MarketDataIngestionHost.NormalizeSymbols(["MES", "  ES  "]);

        result.Should().Equal("MES", "ES");
    }

    public static TheoryData<string[]> NothingReal()
    {
        TheoryData<string[]> data = [];
        data.Add([]);
        data.Add([""]);          // the empty element compose yields for an unset Ingestion__Symbols__0
        data.Add(["   "]);
        data.Add(["", "  "]);
        return data;
    }

    [Theory]
    [MemberData(nameof(NothingReal))]
    public void NormalizeSymbols_ShouldBeEmpty_WhenNothingRealIsConfigured(string[] configured)
    {
        // Empty => the host disables ingestion. A subscription to a blank symbol would only fail to resolve.
        MarketDataIngestionHost.NormalizeSymbols(configured).Should().BeEmpty();
    }

    [Fact]
    public void NormalizeSymbols_ShouldDropBlanksAmongReals()
    {
        string[] result = MarketDataIngestionHost.NormalizeSymbols(["MES", "", "NQ"]);

        result.Should().Equal("MES", "NQ");
    }
}
