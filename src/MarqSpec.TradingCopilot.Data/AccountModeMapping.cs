using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// The single application point for the persisted <see cref="Account.Mode"/> (R-14, gh#7):
/// <c>mode = conventions.ModeFor(StageOverride ?? Stage)</c>.
/// </summary>
/// <remarks>
/// Every write path that can move an account's mode — discovery, the stage override, a conventions
/// re-declaration — calls this rather than assigning <see cref="Account.Mode"/> directly, so the materialised
/// value the DB-level mode guard compares against can never drift from the declaration that defines it.
/// </remarks>
public static class AccountModeMapping
{
    /// <summary>Recomputes and stores the account's mode under the firm's current declarations.</summary>
    /// <param name="account">The account to recompute.</param>
    /// <param name="conventions">The owning firm's declared conventions.</param>
    public static void RecomputeMode(this Account account, FirmConventions conventions)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(conventions);
        account.Mode = conventions.ModeFor(account.StageOverride ?? account.Stage);
    }
}
