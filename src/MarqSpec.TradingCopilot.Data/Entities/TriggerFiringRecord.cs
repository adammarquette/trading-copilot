using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One firing of a <see cref="Trigger"/> (gh#385) — the durable record that a mechanical alert was raised. The
/// operator's journal of what the deterministic scan caught.
/// </summary>
/// <remarks>
/// <b>Append-only</b> (the <c>AuditRecord</c> pattern): written once, never updated or deleted. <see cref="TriggerId"/>
/// is a <b>soft</b> reference (no FK) so a firing survives the trigger it records being edited or deleted, and the
/// row <b>snapshots the condition</b> (instrument, indicator, threshold, and the value that fired it) so it stays
/// interpretable on its own. <b>Operator-owned (R-20):</b> stamped with the trigger's owner — a background scan runs
/// with no ambient user, so the writer copies <see cref="Trigger.UserId"/> onto the record.
/// </remarks>
public class TriggerFiringRecord : IUserOwned
{
    /// <summary>The firing's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20), copied from the trigger that fired.</summary>
    public Guid UserId { get; set; }

    /// <summary>The trigger that fired. A soft reference (no FK) — the firing outlives the trigger.</summary>
    public Guid TriggerId { get; set; }

    /// <summary>The firing cycle this record belongs to — the incident, together with <see cref="TriggerId"/>.</summary>
    public required int ArmCycle { get; set; }

    /// <summary>The instrument, snapshotted so the record is self-describing.</summary>
    public required string Instrument { get; set; }

    /// <summary>The indicator name, snapshotted.</summary>
    public required string Indicator { get; set; }

    /// <summary>The indicator period, snapshotted.</summary>
    public required int Period { get; set; }

    /// <summary>The resolution in minutes, snapshotted.</summary>
    public required int ResolutionMinutes { get; set; }

    /// <summary>The comparison, snapshotted.</summary>
    public required ThresholdComparison Comparison { get; set; }

    /// <summary>The threshold, snapshotted.</summary>
    public required decimal Threshold { get; set; }

    /// <summary>The indicator value that crossed the threshold and fired the trigger.</summary>
    public required decimal Value { get; set; }

    /// <summary>When the firing was recorded (UTC).</summary>
    public required DateTimeOffset FiredAt { get; set; }
}
