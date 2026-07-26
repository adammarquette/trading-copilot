using MarqSpec.TradingCopilot.Data.Tenancy;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A firm login — <b>one per firm × platform</b> (ADR-0016): Apex-on-Tradovate and Apex-on-Rithmic are two
/// connections; several firms sharing one platform are several connections on it. Exposes many
/// <see cref="Account"/>s once discovered.
/// </summary>
/// <remarks>
/// Carries <b>no secret</b>. <see cref="CredentialKey"/> names the environment entry the actual credentials live
/// under (.env locally, Railway variables in the cloud) — the repo's settled secrets-in-env pattern. Encrypted
/// in-database credential storage (the ADR-0016 UI-entry model) is a later increment with its own key-management
/// design; the exact env naming convention is settled by the venue-wiring increment that consumes it (gh#76).
/// </remarks>
public class Connection : IUserOwned
{
    /// <summary>The connection's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The firm this login belongs to.</summary>
    public Guid FirmId { get; set; }

    /// <summary>
    /// The platform this login is for — a venue key the adapter registry resolves (e.g. <c>projectx</c>,
    /// <c>tradovate</c>), matching the domain <c>VenueId</c>.
    /// </summary>
    public required string Platform { get; set; }

    /// <summary>
    /// The <b>non-secret</b> name of the environment entry holding this connection's credentials. Never the
    /// credentials themselves.
    /// </summary>
    public required string CredentialKey { get; set; }

    /// <summary>
    /// Whether this login is active. Deactivation (gh#210) is a <b>soft delete</b>: the row and its accounts stay,
    /// because the journal — orders, trades, gate decisions — references those accounts and is the audit trail.
    /// A deactivated connection is no longer a usable path to the venue.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>The accounts this login exposes, as discovered from the venue.</summary>
    public ICollection<Account> Accounts { get; set; } = [];
}
