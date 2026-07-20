namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// Enforces R-14: <b>practice accounts only outside production</b>. Development and staging connect to practice
/// accounts — the real execution path, no real money — and a live real-money account is reachable from production
/// alone. This is enforcement below the model: a gate in code, never a prompt instruction or a config convention.
/// </summary>
public static class TradingModePolicy
{
    /// <summary>Indicates whether <paramref name="mode"/> may be traded in <paramref name="environment"/>.</summary>
    /// <param name="mode">The account's trading mode.</param>
    /// <param name="environment">The environment the platform is running in.</param>
    /// <returns><see langword="true"/> if the combination is permitted.</returns>
    public static bool IsAllowed(TradingMode mode, DeploymentEnvironment environment)
    {
        return mode switch
        {
            TradingMode.Practice => true,
            TradingMode.Live => environment == DeploymentEnvironment.Production,

            // An unrecognized mode fails closed -- a new mode must opt in here deliberately.
            _ => false,
        };
    }

    /// <summary>Throws unless <paramref name="mode"/> may be traded in <paramref name="environment"/>.</summary>
    /// <param name="mode">The account's trading mode.</param>
    /// <param name="environment">The environment the platform is running in.</param>
    /// <exception cref="TradingModeNotAllowedException">The combination is not permitted.</exception>
    public static void EnsureAllowed(TradingMode mode, DeploymentEnvironment environment)
    {
        if (!IsAllowed(mode, environment))
        {
            throw new TradingModeNotAllowedException(
                $"A {mode} account may not be traded from {environment} — practice accounts only outside production (R-14).");
        }
    }

    /// <summary>Throws unless <paramref name="account"/> may be traded in <paramref name="environment"/>.</summary>
    /// <param name="account">The account being selected or traded.</param>
    /// <param name="environment">The environment the platform is running in.</param>
    /// <exception cref="TradingModeNotAllowedException">The account's mode is not permitted here.</exception>
    public static void EnsureAllowed(VenueAccount account, DeploymentEnvironment environment)
    {
        if (!IsAllowed(account.Mode, environment))
        {
            throw new TradingModeNotAllowedException(
                $"Account '{account.Name}' ({account.Id}) is {account.Mode} and may not be traded from " +
                $"{environment} — practice accounts only outside production (R-14).");
        }
    }
}
