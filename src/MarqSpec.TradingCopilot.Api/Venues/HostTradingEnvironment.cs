using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Api.Venues;

/// <summary>
/// The R-14 <see cref="DeploymentEnvironment"/> this host runs in, wrapped for DI so endpoint parameters bind
/// from <b>services only</b> — a bare enum parameter would be a simple type minimal APIs happily bind from the
/// query string, and "which environment am I in" must never be caller-suppliable.
/// </summary>
/// <param name="Value">The environment.</param>
public sealed record HostTradingEnvironment(DeploymentEnvironment Value);

/// <summary>
/// Maps the host's environment name (<c>ASPNETCORE_ENVIRONMENT</c>) onto the R-14
/// <see cref="DeploymentEnvironment"/> at the composition root (gh#9).
/// </summary>
public static class DeploymentEnvironmentMapping
{
    /// <summary>
    /// Maps an environment name. Unrecognised names — typos included — map to
    /// <see cref="DeploymentEnvironment.Development"/>, the practice-only end of the ladder: a misconfigured
    /// host can <b>lose</b> live access, never gain it.
    /// </summary>
    /// <param name="environmentName">The host environment name.</param>
    /// <returns>The mapped environment.</returns>
    public static DeploymentEnvironment From(string environmentName)
    {
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
        {
            return DeploymentEnvironment.Production;
        }

        if (string.Equals(environmentName, "Staging", StringComparison.OrdinalIgnoreCase))
        {
            return DeploymentEnvironment.Staging;
        }

        return DeploymentEnvironment.Development;
    }
}
