using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One gate decision, persisted (data dictionary §5, R-5/R-16, gh#10/gh#11): every evaluated send attempt
/// leaves exactly one row — allowed, resized, or blocked — carrying the size and the layer that bound it, so
/// the operator is never told "no" without a durable why (engineering §9: guardrail decisions are auditable).
/// </summary>
/// <remarks>
/// Pre-gate refusals (mode, account state, mismatch, unsupported type) persist nothing here — the order was
/// never sized, so no decision exists; the refusal reason returns to the caller instead. Blocked decisions
/// carry no <see cref="OrderId"/> (nothing was transmitted); placed ones link the journaled order — the audit
/// pair.
/// </remarks>
public class GateDecisionRecord : IUserOwned
{
    /// <summary>The decision record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The account the proposal was sized against.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The journaled order this decision authorized, when one was transmitted. Null for blocks.</summary>
    public Guid? OrderId { get; set; }

    /// <summary>The gate's verdict. <c>required</c>: the zero value is a real verdict, never a default.</summary>
    public required GateOutcome Outcome { get; set; }

    /// <summary>The contracts the gate authorized. Zero means no trade.</summary>
    public int ApprovedQuantity { get; set; }

    /// <summary>The layer that bound the decision, when one did.</summary>
    public RiskLayer? BindingLayer { get; set; }

    /// <summary>The gate's human-readable explanation — always populated (R-5).</summary>
    public required string Reason { get; set; }

    /// <summary>When the gate decided.</summary>
    public required DateTimeOffset DecidedAt { get; set; }
}
