using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// Mapping the host's environment name onto the R-14 <see cref="DeploymentEnvironment"/> at the composition
/// root (gh#9). The safety-relevant behaviour: anything unrecognised maps to <b>Development</b> — the
/// practice-only end of the ladder — so a typo in <c>ASPNETCORE_ENVIRONMENT</c> can never unlock live trading.
/// </summary>
public class DeploymentEnvironmentMappingTests
{
    [Theory]
    [InlineData("Development", DeploymentEnvironment.Development)]
    [InlineData("Staging", DeploymentEnvironment.Staging)]
    [InlineData("Production", DeploymentEnvironment.Production)]
    [InlineData("production", DeploymentEnvironment.Production)] // host names are case-insensitive
    public void From_ShouldMapTheKnownEnvironmentNames(string name, DeploymentEnvironment expected)
    {
        DeploymentEnvironmentMapping.From(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("QA")]
    [InlineData("Prod")] // close is not equal -- fail closed, never guess
    [InlineData("")]
    [InlineData("   ")]
    public void From_ShouldFailClosedToDevelopment_ForAnythingUnrecognised(string name)
    {
        // Development is the practice-only end: a misconfigured host can lose live access, never gain it.
        DeploymentEnvironmentMapping.From(name).Should().Be(DeploymentEnvironment.Development);
    }
}
