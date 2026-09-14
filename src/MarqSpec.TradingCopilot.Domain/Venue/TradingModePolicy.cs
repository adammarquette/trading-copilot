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

            // Undeclared is stricter than Live: Live is permitted in production, Undeclared nowhere. "We have
            // not established whether capital is at stake" is not a state to trade from, and production is
            // where guessing costs most. An unrecognized mode fails closed the same way.
            _ => false,
        };
    }

    /// <summary>
    /// Whether an account in <paramref name="mode"/> may set <b>Send as-is</b> as its default entry action (R-11,
    /// gh#218). Practice only — a live account never defaults to the one-action send, and an undeclared account
    /// (tradeable nowhere) cannot either. Deliberately <b>environment-independent</b>: even in production a live
    /// account is refused, because this guards the default <i>preference</i>, not the send path itself — which
    /// stays gated by R-5 / R-16 / R-12 on every account regardless of the default.
    /// </summary>
    /// <param name="mode">The account's declared trading mode.</param>
    /// <returns><see langword="true"/> only for <see cref="TradingMode.Practice"/>.</returns>
    public static bool SendAsIsDefaultAllowed(TradingMode mode) => mode == TradingMode.Practice;

    /// <summary>Throws unless <paramref name="mode"/> may be traded in <paramref name="environment"/>.</summary>
    /// <param name="mode">The account's trading mode.</param>
    /// <param name="environment">The environment the platform is running in.</param>
    /// <exception cref="TradingModeNotAllowedException">The combination is not permitted.</exception>
    public static void EnsureAllowed(TradingMode mode, DeploymentEnvironment environment)
    {
        if (!IsAllowed(mode, environment))
        {
            throw new TradingModeNotAllowedException(ReasonFor(mode, environment));
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
                $"Account '{account.Name}' ({account.Id}) may not be traded. "
                + ReasonFor(account.Mode, environment));
        }
    }

    /// <summary>
    /// Why a mode is refused. Undeclared and Live fail for different reasons and need different fixes, so one
    /// message for both would send the operator after the wrong thing — "practice accounts only outside
    /// production" reads as though production would have been fine.
    /// </summary>
    private static string ReasonFor(TradingMode mode, DeploymentEnvironment environment)
    {
        return mode == TradingMode.Live
            ? $"A {mode} account may not be traded from {environment} — practice accounts only outside "
                + "production (R-14)."
            : $"Its trading mode is {mode}: no firm convention says whether capital is at risk here, so it is "
                + $"refused in every environment including {environment}. Classify the account's stage before "
                + "trading it (R-14).";
    }
}
