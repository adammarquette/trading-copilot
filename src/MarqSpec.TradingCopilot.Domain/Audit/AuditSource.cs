namespace MarqSpec.TradingCopilot.Domain.Audit;

/// <summary>
/// What tripped a safety action an <c>AuditRecord</c> records — the trigger source (engineering §9, gh#765).
/// </summary>
/// <remarks>
/// Carried by the kill-switch and auto-flatten records so an incident review can tell an operator engage from a
/// guardrail trip from the dead-man's switch, and a scheduled auto-flatten from either. <see cref="Unknown"/> is the
/// refusable zero (the fail-closed-zero convention, gh#60): a persisted source is never <see cref="Unknown"/>. The
/// column is <b>nullable</b> — the stop-plan actions (connection-loss, position-exit, order-cancel/-modify) concern
/// no external trigger and leave it null; only the safety-action rows carry a source.
/// </remarks>
public enum AuditSource
{
    /// <summary>Not a source — the refusable zero. Never persisted (null is used where no source applies).</summary>
    Unknown = 0,

    /// <summary>The operator, by an explicit request through the API — the only kill-switch trigger wired today.</summary>
    Operator = 1,

    /// <summary>A guardrail tripped it automatically (a risk/exposure breach). The seam is here; the trigger lands with it.</summary>
    Guardrail = 2,

    /// <summary>The dead-man's switch tripped it — the process could not vouch for its own liveness (R-13, ADR-0019).</summary>
    DeadMansSwitch = 3,

    /// <summary>The auto-flatten scheduler, firing a position's close at its deadline (R-13, ADR-0013).</summary>
    Scheduler = 4,
}
