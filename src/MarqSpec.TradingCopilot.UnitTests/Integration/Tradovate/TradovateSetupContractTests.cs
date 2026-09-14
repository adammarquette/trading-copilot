using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Tradovate;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tradovate;

/// <summary>
/// Tradovate's setup-time declaration (ADR-0023, gh#64 / gh#41) — the seven-field credential shape ADR-0023 named as
/// the case the fixed two-field ProjectX form could not serve. The cases that matter pin that shape: seven fields in
/// login order, only Username and Password required, only Password and the OAuth Secret masked, and every field
/// carrying the help text an operator reads at the point of entry.
/// </summary>
public class TradovateSetupContractTests
{
    private readonly TradovateSetupContract _contract = new();

    [Fact]
    public void Id_ShouldBeTradovate()
    {
        _contract.Id.Should().Be(VenueId.Parse("tradovate"));
    }

    [Fact]
    public void DisplayName_ShouldBeSet()
    {
        _contract.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Credentials_ShouldDeclareTheSevenTradovateFields_InLoginOrder()
    {
        // The 7-field case ADR-0023 named — the exact shape the 2-field ProjectX form could not render.
        _contract.Credentials.Fields.Should().HaveCount(7);
        _contract.Credentials.Fields.Select(field => field.Key)
            .Should().ContainInOrder("Username", "Password", "AppId", "AppVersion", "DeviceId", "Cid", "Secret");
    }

    [Fact]
    public void Credentials_ShouldRequireOnlyUsernameAndPassword()
    {
        // The client validates username + password; the application / OAuth fields are optional (needed for API-key access).
        _contract.Credentials.Fields.Where(field => field.Required).Select(field => field.Key)
            .Should().BeEquivalentTo(new[] { "Username", "Password" });
    }

    [Fact]
    public void Credentials_ShouldMaskOnlyThePasswordAndTheOAuthSecret()
    {
        // Only the password and the OAuth client secret are secrets; the identifiers, versions, and client id are not.
        _contract.Credentials.Fields.Where(field => field.Secret).Select(field => field.Key)
            .Should().BeEquivalentTo(new[] { "Password", "Secret" });
    }

    [Fact]
    public void Credentials_ShouldGiveEveryFieldHelpText()
    {
        // The label alone is terse; the help text is where each field's meaning is spelled out at the point of entry.
        _contract.Credentials.Fields.Should().OnlyContain(field => !string.IsNullOrWhiteSpace(field.HelpText));
    }
}
