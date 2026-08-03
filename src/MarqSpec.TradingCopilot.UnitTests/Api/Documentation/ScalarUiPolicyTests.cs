using MarqSpec.TradingCopilot.Api.Documentation;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Documentation;

/// <summary>
/// The one gate on the interactive Scalar UI (gh#604): it is served everywhere <b>except</b> production, so a
/// browsable "try it" console for the real order / kill-switch endpoints never ships in front of the live venue.
/// The OpenAPI JSON is ungated; only the interactive console is. Same fail-safe direction as R-14 — an ambiguous
/// host maps to Development (below), so it can only ever <i>enable</i> the UI off the practice-only ladder, never
/// expose it in production.
/// </summary>
public class ScalarUiPolicyTests
{
    [Theory]
    [InlineData(DeploymentEnvironment.Development)]
    [InlineData(DeploymentEnvironment.Staging)]
    public void IsEnabledFor_ShouldEnableTheUi_OutsideProduction(DeploymentEnvironment environment)
    {
        ScalarUiPolicy.IsEnabledFor(environment).Should().BeTrue(
            "the practice-only environments get the browsable console; only production is withheld");
    }

    [Fact]
    public void IsEnabledFor_ShouldDisableTheUi_InProduction()
    {
        // In production the console would put a one-click trigger for POST /accounts/{id}/orders and
        // POST /kill-switch in front of anyone who reaches the page, against the live real-money venue.
        ScalarUiPolicy.IsEnabledFor(DeploymentEnvironment.Production).Should().BeFalse();
    }
}
