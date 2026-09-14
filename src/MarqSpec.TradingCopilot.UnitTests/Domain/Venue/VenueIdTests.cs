using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

public class VenueIdTests
{
    [Theory]
    [InlineData("projectx", "projectx")]
    [InlineData("ProjectX", "projectx")]
    [InlineData("  Tradovate  ", "tradovate")]
    public void Parse_ShouldNormalizeValue_WhenValueHasCaseOrWhitespace(string input, string expected)
    {
        VenueId id = VenueId.Parse(input);

        id.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_ShouldThrowFormatException_WhenValueIsBlank(string? input)
    {
        Action act = () => VenueId.Parse(input);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void TryParse_ShouldReturnFalseAndDefault_WhenValueIsBlank()
    {
        bool parsed = VenueId.TryParse("  ", out VenueId id);

        parsed.Should().BeFalse();
        id.Should().Be(default(VenueId));
    }

    [Fact]
    public void Equality_ShouldHold_WhenValuesMatchAfterNormalization()
    {
        VenueId.Parse("ProjectX").Should().Be(VenueId.Parse(" projectx "));
    }

    [Fact]
    public void ToString_ShouldReturnEmpty_WhenValueIsDefault()
    {
        default(VenueId).ToString().Should().BeEmpty();
    }
}
