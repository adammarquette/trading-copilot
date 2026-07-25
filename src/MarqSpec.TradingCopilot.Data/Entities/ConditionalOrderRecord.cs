using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A persisted <b>synthetic / local</b> conditional entry order (ADR-0007, gh#176): the "send when conditions
/// met" mode. The proposal is kept whole — as the <see cref="Order"/> row keeps it — so the firing watcher
/// re-builds and re-gates it at fire time (R-12); the trigger and cancel-if/expiry drive the deterministic
/// <see cref="ConditionalOrder"/> decisions.
/// </summary>
/// <remarks>
/// Operator-owned (R-20). <see cref="Mode"/> is the R-14 subject: check-constrained to reject
/// <see cref="TradingMode.Undeclared"/>. It is <b>not</b> shown at the broker while <see cref="Status"/> is
/// <see cref="ConditionalStatus.Pending"/>; <see cref="FiredOrderId"/> links the real <see cref="Order"/> once
/// the trigger fires.
/// </remarks>
public class ConditionalOrderRecord : IUserOwned
{
    /// <summary>The record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20).</summary>
    public Guid UserId { get; set; }

    /// <summary>The account the order will be sent to.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The originating suggestion, when the order came from one. Survives suggestion deletion as null.</summary>
    public Guid? SuggestionId { get; set; }

    /// <summary>The venue contract key the proposal resolved to (e.g. <c>CON.F.US.MES.U26</c>).</summary>
    public required string Instrument { get; set; }

    /// <summary>The venue-neutral symbol the proposal was sized for (e.g. <c>MES</c>) — re-resolved at fire time.</summary>
    public string? Symbol { get; set; }

    /// <summary>The order side. <c>required</c>: the zero value is a real side, never a default.</summary>
    public required OrderSide Side { get; set; }

    /// <summary>The size in contracts. Positive — a DB check refuses the rest.</summary>
    public required int Size { get; set; }

    /// <summary>The order type. <c>required</c>: the zero value is a real type, never a default.</summary>
    public required OrderType Type { get; set; }

    /// <summary>The intended entry price.</summary>
    public decimal EntryPrice { get; set; }

    /// <summary>The working (protective) stop — kept for every type (gh#134), as the order row does.</summary>
    public decimal WorkingStopPrice { get; set; }

    /// <summary>The safety-stop price — the deterministic worst case (R-5).</summary>
    public decimal SafetyStopPrice { get; set; }

    /// <summary>The reference price the fat-finger band measured from at creation.</summary>
    public decimal ReferencePrice { get; set; }

    /// <summary>The instrument's tick size at creation.</summary>
    public decimal TickSize { get; set; }

    /// <summary>The instrument's point value at creation.</summary>
    public decimal PointValue { get; set; }

    /// <summary>The optional take-profit price (gh#173), carried whole into the fired order.</summary>
    public decimal? TakeProfitPrice { get; set; }

    /// <summary>The price at which the order fires.</summary>
    public decimal TriggerPrice { get; set; }

    /// <summary>Which way price must cross <see cref="TriggerPrice"/> to fire. <c>required</c>; a DB check refuses unknown.</summary>
    public required ConditionalCrossDirection TriggerDirection { get; set; }

    /// <summary>
    /// The optional cancel band — the stale-side price past which the setup is abandoned. A DB check keeps it on
    /// the stale side of <see cref="TriggerPrice"/> (below for RisesTo, above for FallsTo); NULL means none.
    /// </summary>
    public decimal? CancelDriftPrice { get; set; }

    /// <summary>The optional validity deadline. Past it, a still-pending order cancels.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Where the order is in its lifecycle. <c>required</c>; a DB check refuses unknown.</summary>
    public required ConditionalStatus Status { get; set; }

    /// <summary>The mode the order was created under (R-14). <c>required</c>; a DB check refuses undeclared.</summary>
    public required TradingMode Mode { get; set; }

    /// <summary>The real order created when the trigger fired. Null while pending / cancelled / expired.</summary>
    public Guid? FiredOrderId { get; set; }

    /// <summary>When the conditional order was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
