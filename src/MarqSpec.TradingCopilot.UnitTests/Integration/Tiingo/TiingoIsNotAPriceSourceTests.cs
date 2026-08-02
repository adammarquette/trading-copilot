using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Tiingo;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tiingo;

/// <summary>
/// Tiingo is wired for <b>news only</b> — price data is single-source (gh#496, of gh#411; engineering §"Data
/// sources").
/// </summary>
/// <remarks>
/// <para>
/// This is <b>asserted rather than merely omitted</b>, because an omission looks exactly like an oversight. Tiingo
/// ships as a complete client and a later contributor could reasonably "fix the gap" by adding a price adapter —
/// giving the deployment two disagreeing sources for the same prices, which is the duplication the single-source
/// decision exists to prevent. The guard holds <b>by construction</b>: it reads the shipped assembly, so it fails
/// the moment such an adapter is added, however it is written or registered.
/// </para>
/// <para>
/// It says nothing about news: Tiingo is deliberately one of <i>two</i> news sources, fanned in and deduped
/// (R-2).
/// </para>
/// </remarks>
public class TiingoIsNotAPriceSourceTests
{
    [Fact]
    public void TiingoIntegration_ShouldExposeNoPriceAdapter_BecausePriceDataIsSingleSource()
    {
        IEnumerable<Type> priceAdapters = typeof(TiingoNewsSource).Assembly
            .GetTypes()
            .Where(candidate => candidate is { IsAbstract: false, IsInterface: false })
            .Where(candidate =>
                typeof(IMarketDataSource).IsAssignableFrom(candidate)
                || typeof(IContextMarketDataSource).IsAssignableFrom(candidate));

        priceAdapters.Should().BeEmpty(
            "price data is single-source (Finnhub); Tiingo carries news only, and adding a price adapter here "
            + "would give the deployment two disagreeing sources for the same prices");
    }

    [Fact]
    public void TiingoNewsSource_ShouldStillDeclareNews_SoTheGuardAboveIsNotVacuous()
    {
        // Without this, the assembly-scan case would also pass if the Tiingo integration were emptied entirely.
        typeof(INewsSource).IsAssignableFrom(typeof(TiingoNewsSource)).Should().BeTrue();
    }
}
