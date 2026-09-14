using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One firing of a <see cref="TriggerRecord"/> (gh#385, ADR-0008) — an append-only journal row written each time a
/// mechanical trigger's crossing edge fires an alert. The audit trail of what fired, when, and against what.
/// </summary>
/// <remarks>
/// Operator-owned (R-20). Append-only: a firing is never updated once written. <see cref="DedupKey"/> is the
/// incident key the notification channel deduplicates on (<c>trigger:{TriggerId}:{ArmCycle}</c>), carried here so
/// the journal and the alert agree on the incident identity.
/// </remarks>
public class TriggerFiringRecord : IUserOwned
{
    /// <summary>The record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20).</summary>
    public Guid UserId { get; set; }

    /// <summary>The trigger that fired — a soft reference (the journal outlives the trigger it records).</summary>
    public Guid TriggerId { get; set; }

    /// <summary>When the crossing edge fired.</summary>
    public DateTimeOffset FiredAt { get; set; }

    /// <summary>The indicator value observed at the firing.</summary>
    public decimal ObservedValue { get; set; }

    /// <summary>The threshold in force at the firing (copied, so the journal is self-describing if the trigger later changes).</summary>
    public decimal Threshold { get; set; }

    /// <summary>The comparison in force at the firing.</summary>
    public IndicatorComparison Comparison { get; set; }

    /// <summary>The incident dedup key the alert was sent under (<c>trigger:{TriggerId}:{ArmCycle}</c>).</summary>
    public required string DedupKey { get; set; }
}
