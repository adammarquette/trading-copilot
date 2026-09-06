using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// <see cref="TriggerAuthoring"/> (gh#1135 of gh#1059, R-7) — the <b>one</b> set of refusals over a trigger's
/// condition half, lifted out of <c>TriggerEndpoints.CreateTriggerAsync</c> so the chat <c>edit_rulebook</c> tool
/// makes exactly the same ones rather than a second copy that drifts.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests exist because the extraction is otherwise unpinned.</b> Every pre-existing
/// <c>TriggerEndpointsTests</c> case asserts the <i>status</i> of a refusal (400) and never its <i>text</i>, so a
/// lift-and-shift that quietly reworded a message would keep the whole endpoint suite green while changing what the
/// API's 400 body says. So each refusal string is pinned here <b>verbatim as the endpoint emitted it before the
/// extraction</b>: that is what makes "this refactor changed no endpoint behaviour" a checked claim rather than an
/// assertion in a PR body.
/// </para>
/// <para>
/// Note the shape deliberately kept: <b>one refusal per check</b>, not a whole-request validator. Each caller keeps
/// its own evaluation <i>order</i> — the create endpoint still refuses a bad route before a bad period, and the chat
/// tool refuses in the order its own schema reads — so lifting these out moved no decision between callers.
/// </para>
/// </remarks>
public class TriggerAuthoringTests
{
    // The refusal strings the endpoint emitted before the extraction. Written out here rather than referenced from
    // production, because a test that asks the code under test what to expect cannot fail on the code's defect.
    private const string SymbolRefusal = "A trigger needs a non-blank instrument symbol.";
    private const string ComparisonRefusal = "The comparison must be Below or Above.";
    private const string PeriodRefusal = "The period must be positive.";
    private const string ResolutionRefusal = "The resolution must be a positive number of minutes.";
    private const string HysteresisRefusal = "The hysteresis band must be positive when set — null means none.";

    [Theory]
    [InlineData("ES")]
    [InlineData("mnq")]
    public void RefuseSymbol_ShouldAccept_WhenTheSymbolParses(string symbol)
    {
        TriggerAuthoring.RefuseSymbol(symbol, out InstrumentId instrument).Should().BeNull();
        instrument.Symbol.Should().NotBeEmpty("an accepted symbol yields the parsed instrument the caller stores");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RefuseSymbol_ShouldRefuseVerbatim_WhenTheSymbolIsBlank(string? symbol) =>
        TriggerAuthoring.RefuseSymbol(symbol, out _).Should().Be(
            SymbolRefusal, "the API's 400 body must read exactly as it did before the extraction");

    [Theory]
    [InlineData(IndicatorComparison.Below)]
    [InlineData(IndicatorComparison.Above)]
    public void RefuseComparison_ShouldAccept_WhenTheComparisonIsBuildable(IndicatorComparison comparison) =>
        TriggerAuthoring.RefuseComparison(comparison).Should().BeNull();

    [Fact]
    public void RefuseComparison_ShouldRefuseVerbatim_WhenTheComparisonIsTheFailClosedZero() =>
        TriggerAuthoring.RefuseComparison(IndicatorComparison.Unknown).Should().Be(ComparisonRefusal);

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public void RefusePeriod_ShouldAccept_WhenThePeriodIsPositive(int period) =>
        TriggerAuthoring.RefusePeriod(period).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RefusePeriod_ShouldRefuseVerbatim_WhenThePeriodIsNotPositive(int period) =>
        TriggerAuthoring.RefusePeriod(period).Should().Be(PeriodRefusal);

    [Fact]
    public void RefuseResolution_ShouldAccept_WhenTheResolutionIsPositive() =>
        TriggerAuthoring.RefuseResolution(5).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-15)]
    public void RefuseResolution_ShouldRefuseVerbatim_WhenTheResolutionIsNotPositive(int resolution) =>
        TriggerAuthoring.RefuseResolution(resolution).Should().Be(ResolutionRefusal);

    [Theory]
    [InlineData("rsi", RsiIndicator.IndicatorName)]
    [InlineData("RSI", RsiIndicator.IndicatorName)]
    [InlineData("Atr", AtrIndicator.IndicatorName)]
    public void RefuseIndicator_ShouldCanonicalise_WhenTheNameIsKnown(string given, string canonical)
    {
        TriggerAuthoring.RefuseIndicator(given, out string resolved).Should().BeNull();
        resolved.Should().Be(
            canonical, "the STORED name must be exactly the IIndicatorSource read identity, whatever the casing in");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("macd")]
    public void RefuseIndicator_ShouldRefuseAndName_TheSupportedSet_WhenTheNameIsUnknown(string? given)
    {
        string? refusal = TriggerAuthoring.RefuseIndicator(given, out string resolved);

        refusal.Should().Be(
            $"Unknown indicator — supported names are {AtrIndicator.IndicatorName}, {RsiIndicator.IndicatorName}.",
            "the endpoint's 400 named the supported set, and a refusal the caller cannot act on is a dead end");
        resolved.Should().BeEmpty("a refused indicator resolves to nothing the caller could accidentally store");
    }

    [Fact]
    public void KnownIndicators_ShouldBeTheR22Set_SoACallerCanListThem() =>
        TriggerAuthoring.KnownIndicators.Should().BeEquivalentTo(
            [AtrIndicator.IndicatorName, RsiIndicator.IndicatorName],
            "a tool schema and an error message both list them, and a second hand-kept copy would drift");

    [Theory]
    [InlineData(null)]
    [InlineData(0.5)]
    public void RefuseHysteresis_ShouldAccept_WhenTheBandIsAbsentOrPositive(double? band) =>
        TriggerAuthoring.RefuseHysteresis(band is null ? null : (decimal)band.Value).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RefuseHysteresis_ShouldRefuseVerbatim_WhenTheBandIsNotPositive(double band) =>
        TriggerAuthoring.RefuseHysteresis((decimal)band).Should().Be(HysteresisRefusal);
}
