using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A trading account as discovered through a <see cref="Connection"/> — the persisted counterpart of the
/// venue-neutral <c>VenueAccount</c> (data dictionary "Account", discovery shape).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the <b>discovery</b> slice only: identity, name, stage, tradability, balance. The risk-rule
/// fields (drawdown floor, daily-loss limit, trailing mode) land with the risk persistence, and the persisted
/// <c>mode</c> column lands with the execution entities and their R-14 check constraint
/// (<c>Order.mode == Account.mode</c>) — until then mode is <i>computed</i> from <see cref="Stage"/> plus the
/// firm's conventions, never stored, so it cannot go stale when a declaration changes (gh#60).
/// </para>
/// <para>
/// <see cref="Stage"/> is persisted (not just resolved on the fly) because it is where the future per-account
/// operator override lands: the conservative name-resolver writes its answer here, and the operator corrects it.
/// </para>
/// </remarks>
public class Account : IUserOwned
{
    /// <summary>The account record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The connection (firm login) this account was discovered through.</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>
    /// The venue's own account handle (e.g. <c>9001</c>) — the key half of the domain's venue-qualified
    /// <c>VenueAccountId</c>; the venue half comes from the connection's <see cref="Connection.Platform"/>.
    /// </summary>
    public required string VenueAccountKey { get; set; }

    /// <summary>The venue's account name (e.g. <c>PRAC-50K-1234</c>).</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Where the account sits in the firm's programme — resolved conservatively from the name at discovery
    /// (<c>Unknown</c> when not confidently recognised), and the landing spot for a future operator override.
    /// </summary>
    public AccountStage Stage { get; set; }

    /// <summary>Whether the venue permits trading this account.</summary>
    public bool CanTrade { get; set; }

    /// <summary>Whether the operator has left the account visible.</summary>
    public bool IsVisible { get; set; }

    /// <summary>The account balance as last reported by the venue.</summary>
    public decimal Balance { get; set; }
}
