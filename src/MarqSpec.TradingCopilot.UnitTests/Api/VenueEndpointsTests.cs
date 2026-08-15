using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using Microsoft.AspNetCore.Http;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// The <c>GET /venues/setup</c> handler (ADR-0023, gh#64): it serves each registered adapter's onboarding
/// self-description — identity + the labelled credential schema — so onboarding renders from the adapter, not a
/// hardcoded form. The behaviours that matter: it maps every registered contract, preserves field order, surfaces
/// ProjectX's true labels (the naming trap), and carries **schema only** — never a credential value (ADR-0015).
/// </summary>
public class VenueEndpointsTests
{
    private static List<VenueSetupResponse> Invoke(params IVenueSetupContract[] contracts)
    {
        IResult result = VenueEndpoints.GetVenueSetup(contracts);
        return (List<VenueSetupResponse>)((IValueHttpResult)result).Value!;
    }

    [Fact]
    public void GetVenueSetup_ShouldReturnOneEntryPerRegisteredContract()
    {
        Invoke(new ProjectXSetupContract()).Should().ContainSingle(r => r.VenueId == "projectx");
    }

    [Fact]
    public void GetVenueSetup_ShouldBeEmpty_WhenNoContractsAreRegistered()
    {
        Invoke().Should().BeEmpty();
    }

    [Fact]
    public void GetVenueSetup_ShouldReturn200()
    {
        IResult result = VenueEndpoints.GetVenueSetup([new ProjectXSetupContract()]);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public void GetVenueSetup_ShouldPreserveTheCredentialFieldOrder()
    {
        VenueSetupResponse projectx = Invoke(new ProjectXSetupContract()).Single();

        projectx.Credentials.Select(f => f.Key).Should().ContainInOrder("ApiKey", "ApiSecret");
    }

    [Fact]
    public void GetVenueSetup_ShouldSurfaceTheProjectXNamingLabels()
    {
        // The whole point of exposing the schema: the operator reads the true label, not the config-key folklore.
        IReadOnlyList<VenueCredentialFieldResponse> fields = Invoke(new ProjectXSetupContract()).Single().Credentials;

        VenueCredentialFieldResponse username = fields.Single(f => f.Key == "ApiKey");
        username.Label.Should().Be("Username");
        username.Secret.Should().BeFalse();

        VenueCredentialFieldResponse apiKey = fields.Single(f => f.Key == "ApiSecret");
        apiKey.Label.Should().Be("API key");
        apiKey.Secret.Should().BeTrue();
    }

    [Fact]
    public void GetVenueSetup_ShouldCarrySchemaOnly_NeverACredentialValue()
    {
        // Pins the response shape: key / label / secret / required / help — the schema, with no field that could
        // hold a value. If someone adds a value-bearing property, this fails (ADR-0015: no secret to the browser).
        IEnumerable<string> properties = typeof(VenueCredentialFieldResponse).GetProperties().Select(p => p.Name);

        properties.Should().BeEquivalentTo(["Key", "Label", "Secret", "Required", "HelpText"]);
    }
}
