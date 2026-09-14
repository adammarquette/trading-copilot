using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Api.Documentation;

/// <summary>
/// Decides whether the interactive Scalar reference UI is served in a given environment (gh#604). The OpenAPI
/// JSON document is served <b>everywhere</b>; only the browsable "try it" console is gated, because in production
/// it would put a one-click trigger for real, execution-shaped endpoints (<c>POST /accounts/{id}/orders</c>,
/// <c>POST /kill-switch</c>) against the live venue in front of anyone who reaches the page. Endpoints already
/// require a JWT, so this is defence in depth, not the only control — but a live-money console is not something
/// to ship by default.
/// </summary>
public static class ScalarUiPolicy
{
    /// <summary>
    /// The interactive UI is enabled in every environment <b>except</b> <see cref="DeploymentEnvironment.Production"/>.
    /// Because the host maps any unrecognised environment name to <see cref="DeploymentEnvironment.Development"/>
    /// (never Production, per <c>DeploymentEnvironmentMapping</c>), an ambiguous or misconfigured host errs toward
    /// enabling the UI on the practice-only end of the ladder — it can never accidentally expose the console in
    /// production, which is the direction R-14 fails.
    /// </summary>
    /// <param name="environment">The resolved R-14 deployment environment.</param>
    /// <returns><see langword="true"/> to serve the interactive UI; otherwise <see langword="false"/>.</returns>
    public static bool IsEnabledFor(DeploymentEnvironment environment) =>
        environment != DeploymentEnvironment.Production;
}
