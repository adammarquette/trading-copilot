using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.ProjectX;

/// <summary>
/// ProjectX's setup-time declaration (ADR-0023, gh#64). The cases that matter are the ones that pin the naming trap:
/// the field an operator labels "Username" is the one stored under <c>ApiKey</c>, and the API key is the secret. If
/// that mapping ever silently flips, an operator enters an API key where a username belongs and authentication fails
/// as a bare "Unknown error" — exactly the trap this contract exists to make legible.
/// </summary>
public class ProjectXSetupContractTests
{
    private readonly ProjectXSetupContract _contract = new();

    [Fact]
    public void Id_ShouldBeProjectX()
    {
        _contract.Id.Should().Be(VenueId.Parse("projectx"));
    }

    [Fact]
    public void DisplayName_ShouldBeSet()
    {
        _contract.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Credentials_ShouldDeclareExactlyTwoFields()
    {
        _contract.Credentials.Fields.Should().HaveCount(2);
    }

    [Fact]
    public void Credentials_ShouldOrderUsernameBeforeApiKey()
    {
        // Login order: the username is entered first, the API key second — the order /api/Auth/loginKey expects.
        _contract.Credentials.Fields.Select(f => f.Key).Should().ContainInOrder("ApiKey", "ApiSecret");
    }

    [Fact]
    public void Credentials_ShouldLabelTheApiKeyFieldAsUsernameAndNotSecret()
    {
        // The trap: the config key is "ApiKey" but it carries the TopstepX *username* (sent as "username"), so it is
        // labelled "Username" and is NOT masked.
        VenueCredentialField field = _contract.Credentials.Fields.Single(f => f.Key == "ApiKey");

        field.Label.Should().Be("Username");
        field.Secret.Should().BeFalse();
        field.Required.Should().BeTrue();
    }

    [Fact]
    public void Credentials_ShouldLabelTheApiSecretFieldAsApiKeyAndSecret()
    {
        // The other half of the trap: the config key is "ApiSecret" but it carries the actual *API key* (sent as
        // "apikey"), so it is labelled "API key" and IS masked.
        VenueCredentialField field = _contract.Credentials.Fields.Single(f => f.Key == "ApiSecret");

        field.Label.Should().Be("API key");
        field.Secret.Should().BeTrue();
        field.Required.Should().BeTrue();
    }

    [Fact]
    public void Credentials_ShouldGiveEveryFieldHelpText()
    {
        // The label alone is terse; the help text is where the "sent as username/apikey" mapping is spelled out, so
        // an operator staring at the form never has to reach for the env comment.
        _contract.Credentials.Fields.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.HelpText));
    }
}
