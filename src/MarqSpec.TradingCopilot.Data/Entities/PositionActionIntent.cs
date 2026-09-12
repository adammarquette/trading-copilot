using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Recovery;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A durable pre-transmit intent for an operator per-position exit or reduce (gh#1161, data dictionary §4).
/// Written in its own committed unit <b>before</b> the venue is touched, so a caller abort or a lock-cleanup
/// fault after transmit still leaves the requested quantity on a row the reconcile sweep (gh#722) can surface.
/// </summary>
/// <remarks>
/// Operator-owned (R-20). <see cref="Status"/> is <see cref="PositionActionIntentStatus.Open"/> until the #1160
/// journal stamps the verified outcome and this row is resolved. An Open row that survives a restart is an
/// impossible combination (<see cref="DecisionInconsistencyKind.PositionActionMidIntent"/>).
/// </remarks>
public class PositionActionIntent : IUserOwned
{
    /// <summary>The intent's unique id — the key the #1160 journal row references.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20).</summary>
    public Guid UserId { get; set; }

    /// <summary>The platform account the close was requested against.</summary>
    public Guid AccountId { get; set; }

    /// <summary>
    /// Which close was asked — matches <c>PositionActionKind</c> (<c>Exit = 1</c>, <c>Reduce = 2</c>). The
    /// fail-closed zero is refused by a DB check.
    /// </summary>
    public int Action { get; set; }

    /// <summary>The venue's own key for that account.</summary>
    public required string VenueAccountKey { get; set; }

    /// <summary>The instrument the operator named (e.g. <c>MES</c>).</summary>
    public required string Instrument { get; set; }

    /// <summary>The venue contract, when the attempt had resolved one before committing.</summary>
    public string? Contract { get; set; }

    /// <summary>
    /// How many contracts the operator asked to take off, for a reduce. Null for an exit, which asks for flat.
    /// </summary>
    public int? RequestedQuantity { get; set; }

    /// <summary>Open until the attempt writes its outcome; the fail-closed zero is refused.</summary>
    public required PositionActionIntentStatus Status { get; set; }

    /// <summary>The verified outcome name, set only once <see cref="Status"/> is Resolved.</summary>
    public string? Outcome { get; set; }

    /// <summary>When this intent was committed — before transmit.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the outcome was stamped; null while Open.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}
