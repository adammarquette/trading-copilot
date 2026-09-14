using MarqSpec.TradingCopilot.Api.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// Startup validation of the confluence-assembly knobs (gh#730, ADR-0026 §3). These feed the hosted trigger scan, so
/// a bad value must <b>fail fast on start</b>: a non-positive <see cref="ConfluenceOptions.KTicks"/> or
/// <see cref="ConfluenceOptions.FAtr"/> would mint a zero / negative proximity band that silently mis-measures every
/// level, and a non-positive ladder entry a nonsensical timeframe. An <b>empty</b> ladder is deliberately valid — the
/// inert, opt-out configuration that keeps every suggestion the degenerate N=1 set.
/// </summary>
public class ConfluenceOptionsTests
{
    [Fact]
    public void Validate_ShouldAcceptTheShippedDefault() =>
        new ConfluenceOptions().Validate().Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldRejectANonPositiveKTicks_WhichWouldMintADegenerateBand(int kTicks) =>
        new ConfluenceOptions { KTicks = kTicks }.Validate().Should().BeFalse();

    [Theory]
    [InlineData(0)]
    [InlineData(-0.25)]
    public void Validate_ShouldRejectANonPositiveFAtr_WhichWouldMintADegenerateBand(double fAtr) =>
        new ConfluenceOptions { FAtr = (decimal)fAtr }.Validate().Should().BeFalse();

    [Fact]
    public void Validate_ShouldRejectANonPositiveTimeframe_WhichIsNonsensical() =>
        new ConfluenceOptions { TimeframeMinutes = [5, 0, 60] }.Validate().Should().BeFalse();

    [Fact]
    public void Validate_ShouldAcceptAnEmptyLadder_TheInertOptOut() =>
        new ConfluenceOptions { TimeframeMinutes = [] }.Validate().Should().BeTrue("an empty ladder is valid and inert (N=1)");

    [Fact]
    public void Validate_ShouldAcceptAWellFormedOverride() =>
        new ConfluenceOptions { KTicks = 12, FAtr = 0.25m, TimeframeMinutes = [1, 5, 15] }.Validate().Should().BeTrue();
}
