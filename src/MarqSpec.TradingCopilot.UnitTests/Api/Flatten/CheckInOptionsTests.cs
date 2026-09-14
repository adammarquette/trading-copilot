using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Domain;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Flatten;

/// <summary>
/// Binding the dead-man's switch's configuration (gh#244, ADR-0019) — the ping URLs and the cadences.
/// </summary>
/// <remarks>
/// Unlike the flatten schedule, this has <b>no safe built-in default</b>: a fabricated URL would report flat to
/// nowhere while looking configured. So absence disables the switch loudly rather than guessing, and a malformed
/// URL fails at startup rather than at the deadline.
/// </remarks>
public class CheckInOptionsTests
{
    [Fact]
    public void IsConfigured_ShouldBeFalse_WhenNothingIsSet()
    {
        // A fresh deployment has no monitor account. The app must run -- loudly unmonitored, not crashed.
        new CheckInOptions().IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_ShouldBeTrue_WhenOnlyTheHeartbeatIsSet()
    {
        new CheckInOptions { HeartbeatUrl = "https://hc.example/ping/alive" }.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void UrlFor_ShouldResolveTheInstrument_IgnoringSymbolCase()
    {
        CheckInOptions options = new()
        {
            Instruments = [new CheckInUrlOption { Symbol = "es", Url = "https://hc.example/ping/es" }],
        };

        options.UrlFor(InstrumentId.Parse("ES")).Should().Be(new Uri("https://hc.example/ping/es"));
    }

    [Fact]
    public void UrlFor_ShouldReturnNull_WhenTheInstrumentHasNoCheck()
    {
        // Not an error: the operator may only monitor the markets they actually trade.
        new CheckInOptions().UrlFor(InstrumentId.Parse("ES")).Should().BeNull();
    }

    [Fact]
    public void UrlFor_ShouldThrow_WhenTheConfiguredUrlIsMalformed()
    {
        // Fail at startup, not at 14:35 -- the same "never coerce on a safety path" stance as the deadline parser.
        CheckInOptions options = new()
        {
            Instruments = [new CheckInUrlOption { Symbol = "ES", Url = "not-a-url" }],
        };

        Action act = () => options.UrlFor(InstrumentId.Parse("ES"));

        act.Should().Throw<FormatException>().WithMessage("*ES*");
    }

    [Fact]
    public void HeartbeatUri_ShouldThrow_WhenTheConfiguredUrlIsMalformed()
    {
        CheckInOptions options = new() { HeartbeatUrl = "://nonsense" };

        Action act = () => _ = options.HeartbeatUri;

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Intervals_ShouldDefaultToAMinute_WhenNotConfigured()
    {
        CheckInOptions options = new();

        options.HeartbeatInterval.Should().Be(TimeSpan.FromMinutes(1));
        options.PollInterval.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Intervals_ShouldHonourConfiguration_WhenSet()
    {
        CheckInOptions options = new() { HeartbeatIntervalSeconds = 30, PollIntervalSeconds = 20 };

        options.HeartbeatInterval.Should().Be(TimeSpan.FromSeconds(30));
        options.PollInterval.Should().Be(TimeSpan.FromSeconds(20));
    }
}
