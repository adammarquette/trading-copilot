using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

public class VenueCredentialFieldTests
{
    [Fact]
    public void Create_ShouldHoldTheDeclaredValues_WhenGivenValidInput()
    {
        VenueCredentialField field = VenueCredentialField.Create(
            key: "ApiSecret", label: "API key", secret: true, required: true, helpText: "Sent as apikey.");

        field.Key.Should().Be("ApiSecret");
        field.Label.Should().Be("API key");
        field.Secret.Should().BeTrue();
        field.Required.Should().BeTrue();
        field.HelpText.Should().Be("Sent as apikey.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrow_WhenKeyIsBlank(string? key)
    {
        Action act = () => VenueCredentialField.Create(key!, label: "Label", secret: false, required: true);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldThrow_WhenLabelIsBlank(string? label)
    {
        // The label is the field an operator reads at the point of entry; a blank one would re-open the naming trap
        // this schema exists to close (ADR-0023), so it is refused rather than silently rendered empty.
        Action act = () => VenueCredentialField.Create(key: "ApiKey", label: label!, secret: false, required: true);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_ShouldTrimKeyAndLabel_WhenPadded()
    {
        VenueCredentialField field = VenueCredentialField.Create(
            key: "  ApiKey  ", label: "  Username  ", secret: false, required: true);

        field.Key.Should().Be("ApiKey");
        field.Label.Should().Be("Username");
    }

    [Fact]
    public void Create_ShouldDefaultHelpTextToNull_WhenOmitted()
    {
        VenueCredentialField field = VenueCredentialField.Create(
            key: "ApiKey", label: "Username", secret: false, required: true);

        field.HelpText.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldNormalizeBlankHelpTextToNull_WhenHelpTextIsWhitespace(string helpText)
    {
        VenueCredentialField field = VenueCredentialField.Create(
            key: "ApiKey", label: "Username", secret: false, required: true, helpText: helpText);

        field.HelpText.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldTrimHelpText_WhenPadded()
    {
        VenueCredentialField field = VenueCredentialField.Create(
            key: "ApiKey", label: "Username", secret: false, required: true, helpText: "  Your username.  ");

        field.HelpText.Should().Be("Your username.");
    }

    [Fact]
    public void Fields_ShouldBeValueEqual_WhenEveryValueMatches()
    {
        VenueCredentialField a = VenueCredentialField.Create("ApiKey", "Username", secret: false, required: true);
        VenueCredentialField b = VenueCredentialField.Create("ApiKey", "Username", secret: false, required: true);

        a.Should().Be(b);
    }

    [Fact]
    public void Fields_ShouldNotBeEqual_WhenSecretDiffers()
    {
        VenueCredentialField visible = VenueCredentialField.Create("X", "X", secret: false, required: true);
        VenueCredentialField masked = VenueCredentialField.Create("X", "X", secret: true, required: true);

        visible.Should().NotBe(masked);
    }
}
