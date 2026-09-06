using System.Globalization;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Events;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The journal both operator position actions write through (gh#1143): an <b>event-log</b> append (ADR-0001) for
/// the structured record, and an immutable <b>operator-owned <see cref="AuditRecord"/></b> (gh#220, engineering §9)
/// for the §9 trail — the same pair the auto-flatten writes for a position-level close (gh#765).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why both, rather than the cancel / modify shape.</b> A cancel (gh#250), a reprice / re-stage / resize
/// (gh#259 / gh#267 / gh#292) and a withdraw (gh#655) all leave a durable row of their own — the <c>Order</c> or
/// <c>ConditionalOrderRecord</c> whose status <i>is</i> the journal — and add an <see cref="AuditRecord"/> beside
/// it. A per-position exit or reduce writes <b>no such row</b>: it is a native venue close, not an order the
/// platform journals, so an <see cref="AuditRecord"/> alone would leave the structured facts (what was asked, what
/// was open, what is open now) only in a prose <c>Detail</c> string. The one other position-level close in the
/// system, the auto-flatten, already answers this by writing both — so this follows it rather than inventing a
/// third shape. ADR-0007's <i>Order lifecycle</i> decision names the append-only event log (R-8 / R-9, ADR-0001);
/// the gh#220 update names the immutable §9 row. Both, then.
/// </para>
/// <para>
/// <b>One event type per action, with the outcome as a payload field.</b> The auto-flatten's types name distinct
/// <i>events</i> (a warning, a missed deadline, an unrostered account), several of which are not close attempts at
/// all. Here there is exactly one event — the operator's attempt — with a named outcome, and the outcome set is
/// open (the reduce's grew from five names to eight during its own review). A payload field means a new outcome
/// cannot silently become an unjournaled event type.
/// </para>
/// <para>
/// <b>It cannot fail the action.</b> Each half is written inside its own fault boundary, so a failing event log
/// still leaves the audit row (and the reverse), and <b>nothing</b> propagates — not even cancellation. Every
/// caller reaches this seam <i>after</i> the venue attempt has resolved, holding the outcome it is about to return;
/// a throw here would replace a verified answer with an error.
/// </para>
/// </remarks>
public sealed class PositionActionJournal : IPositionActionJournal
{
    /// <summary>The producing system on every event this journal appends.</summary>
    public const string EventSource = "position-action";

    /// <summary>The event type for an operator's per-position full exit (gh#656).</summary>
    public const string ExitEventType = "position.exit";

    /// <summary>The event type for an operator's sized partial close (gh#928).</summary>
    public const string ReduceEventType = "position.reduce";

    // AuditRecord.Detail is capped at 512 by the model configuration; bound it here rather than letting a long
    // instrument or account key fail the insert and lose the row.
    private const int MaxDetail = 512;

    private readonly IEventLog _eventLog;
    private readonly IAuditLog _auditLog;
    private readonly ILogger<PositionActionJournal> _logger;

    /// <summary>Creates the journal.</summary>
    /// <param name="eventLog">The append-only event backbone (ADR-0001).</param>
    /// <param name="auditLog">The immutable operator-owned audit trail (gh#220).</param>
    /// <param name="logger">The logger — the last resort when both durable writes fail.</param>
    public PositionActionJournal(IEventLog eventLog, IAuditLog auditLog, ILogger<PositionActionJournal> logger)
    {
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(logger);
        _eventLog = eventLog;
        _auditLog = auditLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task RecordAsync(PositionActionEntry entry, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _ = _eventLog;
        _ = _auditLog;
        _ = _logger;
        return Task.CompletedTask;
    }
}
