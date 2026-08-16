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

        // The venue's routing flag is passed, not applied: the conventions decide whether it may be read (gh#780).
        // At a prop firm it is ignored entirely (R-14). At a brokerage it IS the answer, and it has to come from
        // the persisted column because two of the three write points here -- a stage override, a conventions
        // re-declaration -- have no live venue call to ask again.
        account.Mode = conventions.ModeFor(
            account.StageOverride ?? account.Stage, account.VenueReportsSimulated);
    }
}
