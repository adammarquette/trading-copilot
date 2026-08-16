using MarqSpec.TradingCopilot.Api.MarketData;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// Startup validation of the key-level projection host's knobs (gh#597). A bad value must **fail fast on start**,
/// not at runtime: a non-positive <see cref="KeyLevelDetectorOptions.PollIntervalSeconds"/> throws from
/// <c>Task.Delay</c> inside the host's own retry path — and a faulting <c>BackgroundService</c> stops the whole host
/// process — while a non-positive <see cref="KeyLevelDetectorOptions.MaxLevelsPerKind"/> makes every
/// <c>KeyLevels.Detect</c> pass throw, silently swallowed per-series, so the host runs but writes no level forever.
/// </summary>
public class KeyLevelDetectorOptionsTests
{
    [Fact]
    public void Validate_ShouldAcceptTheShippedDefault() =>
        new KeyLevelDetectorOptions().Validate().Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldRejectANonPositivePollInterval_WhichWouldFaultTheHost(int seconds) =>
        new KeyLevelDetectorOptions { PollIntervalSeconds = seconds }.Validate().Should().BeFalse();

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_ShouldRejectANonPositiveCap_WhichWouldThrowEveryPassAndWriteNothing(int maxPerKind) =>
        new KeyLevelDetectorOptions { MaxLevelsPerKind = maxPerKind }.Validate().Should().BeFalse();

    [Fact]
    public void Validate_ShouldAcceptAWellFormedOverride() =>
        new KeyLevelDetectorOptions { PollIntervalSeconds = 120, MaxLevelsPerKind = 20 }.Validate().Should().BeTrue();
}
