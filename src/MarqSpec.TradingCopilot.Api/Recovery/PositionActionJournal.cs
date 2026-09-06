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
/// <c>ConditionalOrderRecord</c> whose status <i>is</i> the journal. Most of them add an <see cref="AuditRecord"/>
/// beside it; not all do — a cancel whose order had no staged stop plan to retire writes no audit row at all, and a
/// withdraw writes none ever, because in both cases the status row already carries the whole event. A per-position
/// exit or reduce writes <b>no such row</b>: it is a native venue close, not an order the
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
/// <para>
/// <b>The two boundaries are independent only because each store cleans up after itself.</b> <c>TimescaleEventLog</c>
/// and <c>AuditLog</c> are both scoped over the <b>same</b> <c>TradingCopilotDbContext</c>, and EF leaves a refused
/// insert tracked as <c>Added</c> — so a failed append would otherwise be re-attempted by the audit's
/// <c>SaveChanges</c>, be refused again, and take the audit row down with it, losing <i>both</i> records. Each store
/// therefore detaches its own refused entity before rethrowing (gh#1143). Two <c>try</c> blocks around one shared
/// change tracker are not two boundaries; this is what makes them two. <b>The guarantee is that each store cleans up
/// after itself, not that it is isolated from a third party</b>: a <c>SaveChanges</c> refused because of some
/// <i>other</i> entity the request already had pending is not rescued by detaching ours. Unreachable on these two
/// paths — the exit and reduce read accounts and connections and mutate nothing, so nothing else is ever pending at
/// the journal site — and stated rather than left for a future caller with pending writes to rediscover. It is held by
/// <c>PositionActionJournalFaultIsolationIntegrationTests</c> against real Postgres, because the in-memory provider
/// raises no constraint and a fake <c>IEventLog</c> never touches the shared context at all.
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
    public async Task RecordAsync(PositionActionEntry entry, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Two independent fault boundaries, not one: a single try around both would let a failing event append
        // silently cost the audit row as well, and the two stores fail for unrelated reasons.
        await AppendEventSafelyAsync(entry, occurredAt);
        await WriteAuditSafelyAsync(entry, occurredAt);
    }

    private async Task AppendEventSafelyAsync(PositionActionEntry entry, DateTimeOffset occurredAt)
    {
        try
        {
            string payload = JsonSerializer.Serialize(new
            {
                action = entry.Action.ToString(),
                account = entry.AccountId,
                venueAccount = entry.VenueAccountKey,
                instrument = entry.Instrument,
                contract = entry.Contract,
                requestedQuantity = entry.RequestedQuantity,
                netQuantityBefore = entry.NetQuantityBefore,
                netQuantityAfter = entry.NetQuantityAfter,
                outcome = entry.Outcome,
            });

            await _eventLog.AppendAsync(
                new EventDraft(EventTypeFor(entry.Action), EventSource, occurredAt, payload), CancellationToken.None);
        }
        catch (Exception error)
        {
            // Cancellation included, deliberately: see the seam's remarks. The close already happened.
            _logger.LogError(
                error,
                "Could not journal the operator {Action} of {Instrument} on account {Account} (outcome {Outcome}); "
                + "the action itself is unaffected (gh#1143).",
                entry.Action,
                entry.Instrument,
                entry.AccountId,
                entry.Outcome);
        }
    }

    private async Task WriteAuditSafelyAsync(PositionActionEntry entry, DateTimeOffset occurredAt)
    {
        try
        {
            string detail = Detail(entry);
            AuditRecord record = new()
            {
                Id = Guid.NewGuid(),
                UserId = entry.OwnerUserId,
                Action = AuditActionFor(entry.Action),

                // An account-level action on the position itself, resting on no single protective leg — the
                // auto-flatten's answer (gh#765). It changes no platform-held protection, so no synthetic_risk.
                Placement = AuditPlacement.None,
                SyntheticRisk = false,

                // Source stays null: CK_AuditRecords_Source_MatchesAction binds a non-null source to the kill /
                // flatten set (5-7) alone, and gh#909 set the precedent for a new action outside it. There is also
                // nothing to disambiguate — unlike a kill or a flatten, these two actions have exactly one possible
                // trigger, an authenticated operator request. Widening a safety CHECK to record a constant would be
                // the wrong trade on this path.
                Source = null,

                Before = entry.NetQuantityBefore?.ToString(CultureInfo.InvariantCulture),

                // The outcome, exactly as the operator was told it — the auto-flatten's `after: "Flat" / "Escalated"`
                // shape. The longest name in either set is 16 characters, well inside the column's 32.
                After = entry.Outcome,
                Detail = detail.Length > MaxDetail ? detail[..MaxDetail] : detail,
                RecordedAt = occurredAt,
            };

            await _auditLog.WriteAsync([record], CancellationToken.None);
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Could not audit the operator {Action} of {Instrument} on account {Account} (outcome {Outcome}); "
                + "the action itself is unaffected (gh#1143).",
                entry.Action,
                entry.Instrument,
                entry.AccountId,
                entry.Outcome);
        }
    }

    private static string EventTypeFor(PositionActionKind action) => action switch
    {
        PositionActionKind.Exit => ExitEventType,
        PositionActionKind.Reduce => ReduceEventType,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "There is no event type for this action."),
    };

    private static AuditAction AuditActionFor(PositionActionKind action) => action switch
    {
        PositionActionKind.Exit => AuditAction.PositionExitAttempted,
        PositionActionKind.Reduce => AuditAction.PositionReduceAttempted,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "There is no audit action for this one."),
    };

    private static string Detail(PositionActionEntry entry)
    {
        string before = entry.NetQuantityBefore?.ToString(CultureInfo.InvariantCulture) ?? "not read";
        string after = entry.NetQuantityAfter?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        string sized = entry.RequestedQuantity is int requested
            ? string.Create(CultureInfo.InvariantCulture, $" by {requested} contract(s)")
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Operator {entry.Action} of {entry.Instrument} on account {entry.VenueAccountKey}{sized} — outcome {entry.Outcome}; net quantity {before} → {after}.");
    }
}
