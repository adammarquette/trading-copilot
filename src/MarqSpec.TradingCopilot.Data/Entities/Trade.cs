using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A journaled trade — the round-trip outcome (data dictionary §7) — the <b>spine</b> only (gh#7): the trade's
/// terms, realized result, and mode.
/// </summary>
/// <remarks>
/// Deferred deliberately: R multiple, strategy linkage, per-fill composition, and annotations — they arrive
/// with the journal pipeline (R-8/R-9). <see cref="Mode"/> is a journal fact carried for query/report honesty
/// (practice results must never blend into live results); it is check-constrained non-<c>Undeclared</c> but not
/// trigger-guarded — a trade closes <i>after</i> placement, when the account's declaration may legitimately
/// have moved on, so the placement-time guard lives on <see cref="Order"/> and <see cref="Suggestion"/>.
/// </remarks>
public class Trade : IUserOwned
{
    /// <summary>The trade record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The account the trade was taken on.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The originating suggestion, when there was one. Survives suggestion deletion as null.</summary>
    public Guid? SuggestionId { get; set; }

    /// <summary>The venue contract traded (e.g. <c>CON.F.US.MES.U26</c>).</summary>
    public required string Instrument { get; set; }

    /// <summary>The entry side. <c>required</c>: the zero value is a real side, never a default.</summary>
    public required OrderSide Side { get; set; }

    /// <summary>The size in contracts. Positive — a DB check refuses the rest.</summary>
    public required int Size { get; set; }

    /// <summary>The average entry price.</summary>
    public required decimal EntryPrice { get; set; }

    /// <summary>The average exit price; null while the trade is open.</summary>
    public decimal? ExitPrice { get; set; }

    /// <summary>The realized P&amp;L, once closed.</summary>
    public decimal? RealizedPnL { get; set; }

    /// <summary>The mode the trade was taken under (R-14) — practice results never blend into live results.</summary>
    public required TradingMode Mode { get; set; }

    /// <summary>When the trade closed; null while open.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
}
