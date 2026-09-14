using MarqSpec.TradingCopilot.Api.MarketData;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// Startup validation and the shipped default of the news poller's knobs (gh#1123, R-2). A bad value must
/// <b>fail fast on start</b>, not silently no-op: a non-positive <see cref="NewsIngestionOptions.LookbackMinutes"/>
/// puts <c>since</c> at or after "now", which is the exact starvation shape gh#1123 found in the shipped default
/// itself — every article dropped, every pass reporting success while storing nothing.
/// </summary>
public class NewsIngestionOptionsTests
{
    [Fact]
    public void Validate_ShouldAcceptTheShippedDefault() =>
        new NewsIngestionOptions().Validate().Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldRejectANonPositiveLookback_WhichWouldFilterEveryArticle(int minutes) =>
        new NewsIngestionOptions { LookbackMinutes = minutes }.Validate().Should().BeFalse();

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_ShouldRejectANonPositivePollInterval_WhichWouldFaultTheHost(int seconds) =>
        new NewsIngestionOptions { PollIntervalSeconds = seconds }.Validate().Should().BeFalse();

    [Fact]
    public void Validate_ShouldAcceptAWellFormedOverride() =>
        new NewsIngestionOptions { LookbackMinutes = 120, PollIntervalSeconds = 120 }.Validate().Should().BeTrue();

    [Fact]
    public void LookbackMinutes_DefaultShouldCoverADayOfMeasuredProviderLatency()
    {
        // gh#1123: measured live, Finnhub's free-tier feed publishes stories already 90-200+ minutes old, and a
        // day's typical volume (13-37 articles) needs roughly 24 hours of window to appear inside it at all.
        new NewsIngestionOptions().LookbackMinutes.Should().BeGreaterThanOrEqualTo(
            1440, "the shipped default must actually admit real Finnhub free-tier news, not just the poll interval's worth of it");
    }
}
