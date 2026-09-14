using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A journaled order (data dictionary §4) — the <b>spine</b> only (gh#7): identity, the order's terms, its
/// status, and its mode.
/// </summary>
/// <remarks>
/// <para>
/// Deferred deliberately: placement (native / synthetic), send mode (now / on-trigger), bracket / OCO linkage,
/// stop plans, and conditional orders — they arrive with the execution engine (R-11, ADR-0007).
/// </para>
/// <para>
/// <see cref="Mode"/> is the R-14 persistence guard's subject: a repository-level check at write is backed by a
/// DB <b>constraint trigger</b> (<c>Order.mode</c> must equal the account's persisted mode — a cross-table rule
/// a plain single-row CHECK cannot express) plus a check constraint refusing
/// <see cref="TradingMode.Undeclared"/> outright, so a direct write or a future service bug still cannot
/// journal a live order against a practice account (gh#7, carried from #78).
/// </para>
/// </remarks>
public class Order : IUserOwned
{
    /// <summary>The order record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The account the order belongs to — the parent whose mode the R-14 guard compares against.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The originating suggestion, when the order came from one. Survives suggestion deletion as null.</summary>
    public Guid? SuggestionId { get; set; }

    /// <summary>The venue contract the order is on (e.g. <c>CON.F.US.MES.U26</c>).</summary>
    public required string Instrument { get; set; }

    /// <summary>The order side. <c>required</c>: the zero value is a real side, never a default.</summary>
    public required OrderSide Side { get; set; }

    /// <summary>The size in contracts. Positive — a DB check refuses the rest.</summary>
    public required int Size { get; set; }

    /// <summary>The order type. <c>required</c>: the zero value is a real type, never a default.</summary>
    public required OrderType Type { get; set; }

    /// <summary>
    /// The venue-neutral symbol the proposal was sized for (e.g. <c>MES</c>) — the R-12 rebuild re-resolves
    /// the contract from it; <see cref="Instrument"/> keeps the resolved contract key.
    /// </summary>
    public string? Symbol { get; set; }

    /// <summary>The proposal's intended entry price — kept whole so a staged ticket can re-validate (R-12).</summary>
    public decimal EntryPrice { get; set; }

    /// <summary>
    /// The proposal's <b>working</b> (protective) stop — persisted for <i>every</i> order type, distinct from
    /// <see cref="StopPrice"/> (the venue trigger, which only Stop/StopLimit orders carry). Kept whole so a
    /// staged Market/Limit ticket re-validates against the stop the operator armed, not the safety stop
    /// (gh#134). For a Stop order it equals <see cref="StopPrice"/>.
    /// </summary>
    public decimal WorkingStopPrice { get; set; }

    /// <summary>The proposal's safety-stop price — the deterministic worst case (R-5).</summary>
    public decimal SafetyStopPrice { get; set; }

    /// <summary>The reference price the fat-finger band measured from at the last evaluation.</summary>
    public decimal ReferencePrice { get; set; }

    /// <summary>The instrument's tick size at evaluation. Moves to the Instrument entity when it lands.</summary>
    public decimal TickSize { get; set; }

    /// <summary>The instrument's point value at evaluation. Moves to the Instrument entity when it lands.</summary>
    public decimal PointValue { get; set; }

    /// <summary>The limit price, for types that carry one.</summary>
    public decimal? LimitPrice { get; set; }

    /// <summary>The stop price, for types that carry one.</summary>
    public decimal? StopPrice { get; set; }

    /// <summary>
    /// The optional take-profit price (ADR-0007, gh#173) — kept whole so a staged ticket's target survives the
    /// <b>arm → take</b> re-build (R-12). A side-dependent DB check enforces it sits on the winning side of
    /// <see cref="EntryPrice"/> (above for a long, below for a short); <see langword="null"/> is the two-leg
    /// bracket (protective stop only).
    /// </summary>
    public decimal? TakeProfitPrice { get; set; }

    /// <summary>The lifecycle status. <see cref="OrderStatus.Unknown"/> is refused by a DB check.</summary>
    public required OrderStatus Status { get; set; }

    /// <summary>
    /// The mode the order was placed under (R-14) — a fact of placement time. Guarded at write by the
    /// repository and the DB constraint trigger; <see cref="TradingMode.Undeclared"/> is refused outright.
    /// </summary>
    public required TradingMode Mode { get; set; }

    /// <summary>
    /// How the order entered the journal (R-11, ADR-0007, gh#181) — manual ticket, armed take, modified take,
    /// send-as-is, or a fired conditional. Every new order records a real method; a DB check refuses
    /// <see cref="OrderEntryMethod.Unknown"/>. Nullable only for rows journaled before this field existed.
    /// </summary>
    public OrderEntryMethod? EntryMethod { get; set; }

    /// <summary>The venue's own order handle, once placed natively. Null for synthetic / unplaced orders.</summary>
    public string? VenueOrderKey { get; set; }

    /// <summary>When the order was placed.</summary>
    public required DateTimeOffset PlacedAt { get; set; }
}
