using System.Diagnostics;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// The auto-flatten scheduler's core (gh#185, R-13, ADR-0013): the <b>primary</b> trigger. Each pass reads the
/// venue's live positions for every account this process serves and, per instrument, applies the domain flatten
/// decision (gh#69) at its deadline — closing what is due, verifying against the venue, retrying, and journalling.
/// </summary>
/// <remarks>
/// <para>
/// This is the primary tier only. The <b>redundant / independent</b> trigger, the rejected-order behaviour, and a
/// local fallback path — the ADR-0013 guarantee that the flatten fires even when this tier is degraded — are a
/// separate slice (gh#187). Settlement-window reconciliation is gh#193. This tier fires <b>ahead of</b> any venue
/// forced-flatten and a live brokerage has none, so it cannot lean on the venue as a backstop.
/// </para>
/// <para>
/// It runs as background plumbing with <b>no authenticated user</b>, so its account reads deliberately
/// <c>IgnoreQueryFilters</c> the R-20 default-deny — the host acts for the deployment, over the accounts this
/// process's one credential set can reach (ADR-0015). Ownership is read, never re-assigned.
/// </para>
/// </remarks>
public sealed class AutoFlattenService
{
    /// <summary>The <c>Source</c> stamped on every flatten event.</summary>
    public const string EventSource = "auto-flatten";

    /// <summary>A position was closed at its deadline and verified flat.</summary>
    public const string ExecutedEventType = "flatten.executed";

    /// <summary>The deadline is near and a position is open — an escalating warning precedes the flatten (R-13).</summary>
    public const string WarningEventType = "flatten.warning";

    /// <summary>The deadline and its firing window passed with exposure still open — the degraded case (ADR-0013).</summary>
    public const string MissedEventType = "flatten.missed";

    /// <summary>The flatten fired but the venue still reports exposure after the last attempt — escalate loudly.</summary>
    public const string EscalatedEventType = "flatten.escalated";

    /// <summary>A market with a live position is disabled — kept visible, never a silent no-op (R-13).</summary>
    public const string DisabledEventType = "flatten.disabled";

    /// <summary>An open position in a product with no configured deadline — a gap that must not stay silent.</summary>
    public const string UnconfiguredEventType = "flatten.unconfigured";

    /// <summary>
    /// This process holds a row for an account its credential set serves, but the venue's live roster does not
    /// report it, so the pass could not evaluate its exposure. Recorded rather than skipped in silence (R-13,
    /// gh#527): a rediscovery concern, kept observable so it can be seen and followed up — never a quiet gap.
    /// </summary>
    public const string UnrosteredEventType = "flatten.unrostered";

    /// <summary>The audit <c>Detail</c> column's width (data dictionary §12); a longer summary is truncated to fit.</summary>
    private const int MaxDetail = 512;

    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IEventLog _eventLog;
    private readonly IAuditLog _auditLog;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly FlattenOptions _options;
    private readonly INotificationChannel _notifications;
    private readonly INotificationEnlister _enlister;
    private readonly IExecutionMetrics _metrics;
    private readonly ILogger<AutoFlattenService> _logger;

    /// <summary>Creates the service over the scoped database and event log.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="venueFactory">Builds a venue for a connection's firm conventions.</param>
    /// <param name="eventLog">The append-only journal every flatten action is recorded on (R-13).</param>
    /// <param name="auditLog">The immutable audit trail (§9, gh#765) — an executed or escalated flatten lands one row.</param>
    /// <param name="projectXOptions">Carries the credential key this process serves (ADR-0015).</param>
    /// <param name="flattenOptions">The per-instrument schedule and attempt cap.</param>
    /// <param name="notifications">Reaches the operator away from the desk (gh#243, ADR-0019).</param>
    /// <param name="enlister">Records a page inside this pass's own transaction (gh#455).</param>
    /// <param name="metrics">The execution SLIs (gh#232).</param>
    /// <param name="logger">The logger.</param>
    public AutoFlattenService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IEventLog eventLog,
        IAuditLog auditLog,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<FlattenOptions> flattenOptions,
        INotificationChannel notifications,
        INotificationEnlister enlister,
        IExecutionMetrics metrics,
        ILogger<AutoFlattenService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);
        ArgumentNullException.ThrowIfNull(flattenOptions);

        _database = database;
        _venueFactory = venueFactory;
        _eventLog = eventLog;
        _auditLog = auditLog;
        _projectX = projectXOptions.Value;
        _options = flattenOptions.Value;
        _notifications = notifications;
        _enlister = enlister;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// The incident a notification belongs to (gh#243): one account's exposure in one instrument. Scoped this way
    /// so ES failing never suppresses GC failing — they are different money — while the same ES failure repeating
    /// every 15 s stays one incident.
    /// </summary>
    internal static string IncidentKey(VenueAccountId account, InstrumentId instrument) =>
        $"flatten:{account.Key}:{instrument}";

    /// <summary>
    /// Runs one flatten pass over every account this process serves: for each, builds the venue, reads live
    /// positions, and flattens whatever is due at <paramref name="now"/>.
    /// </summary>
    /// <param name="now">The instant to evaluate the schedules against — the host's clock read.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public async Task RunPassAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<FlattenSchedule> schedules = _options.ToSchedules();
        int maxAttempts = _options.MaxFlattenAttempts;

        // No user context: the R-20 default-deny is bypassed deliberately (see the class note).
        List<Account> accounts = await _database.Accounts
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        if (accounts.Count == 0)
        {
            return;
        }

        List<Guid> connectionIds = [.. accounts.Select(account => account.ConnectionId).Distinct()];
        Dictionary<Guid, Connection> connections = await _database.Connections
            .IgnoreQueryFilters()
            .Where(connection => connectionIds.Contains(connection.Id))
            .ToDictionaryAsync(connection => connection.Id, cancellationToken);

        foreach (IGrouping<Guid, Account> byConnection in accounts.GroupBy(account => account.ConnectionId))
        {
            if (!connections.TryGetValue(byConnection.Key, out Connection? connection))
            {
                continue;
            }

            // One credential set per process (ADR-0015): an account on another key is not ours to act on -- a
            // sibling process serves it. Not an error; skip quietly.
            if (!string.Equals(connection.CredentialKey, _projectX.CredentialKey, StringComparison.Ordinal))
            {
                continue;
            }

            FirmConventions conventions = await _database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
            ITradingVenue venue = _venueFactory.Create(conventions);

            // Fresh venue truth, never the persisted snapshot (ADR-0013).
            IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);

            foreach (Account account in byConnection)
            {
                VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
                if (venueAccount is null)
                {
                    // We hold a row for this account but the venue's live roster does not report it, so we cannot
                    // read its exposure or act on it this pass. A rediscovery concern, not the flatten's -- but the
                    // safety net must never be quietly inert (R-13, gh#527), so record the skip rather than hiding
                    // it: the account is named on the journal, and metered so a persistent roster gap can alert.
                    await JournalUnrosteredAsync(account, now, cancellationToken);
                    _metrics.RecordFlattenDeadline(FlattenTier.Primary, ExecutionMetrics.FlattenUnrostered);
                    continue;
                }

                // The owner is threaded from the loaded account (gh#765 review) -- an exact match, not a second
                // under-scoped lookup by the non-unique VenueAccountKey.
                await FlattenAccountAsync(
                    venueAccount.Id, account.UserId, venue, schedules, now, maxAttempts, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Flattens whatever is due on one account at <paramref name="now"/>: reads the venue's live positions, matches
    /// each to its instrument's schedule, and acts on the domain decision. The per-account safety core.
    /// </summary>
    /// <param name="account">The venue account to evaluate.</param>
    /// <param name="ownerUserId">The account's owner — every audit row this flatten writes is stamped with it (R-20,
    /// gh#765); the host has no ambient user, so the caller supplies it from the loaded account.</param>
    /// <param name="venue">The venue to read positions from and close through.</param>
    /// <param name="schedules">The per-instrument schedules in force.</param>
    /// <param name="now">The instant to evaluate against.</param>
    /// <param name="maxAttempts">How many close attempts before escalating.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many instruments were closed and verified flat.</returns>
    internal async Task<int> FlattenAccountAsync(
        VenueAccountId account,
        Guid ownerUserId,
        ITradingVenue venue,
        IReadOnlyList<FlattenSchedule> schedules,
        DateTimeOffset now,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(venue);

        IReadOnlyList<PositionSnapshot> positions = await venue.GetPositionsAsync(account, cancellationToken);
        List<PositionSnapshot> open = [.. positions.Where(position => !position.IsFlat)];
        if (open.Count == 0)
        {
            // Nothing at risk -- but say so (gh#232). A deadline that passes quietly must still EMIT, or "the
            // flatten never fired" and "there was nothing to do" are the same silence, and the failure this
            // system exists to prevent looks exactly like an ordinary Tuesday. The series being present is the
            // health signal; its absence is what a dashboard alerts on.
            foreach (FlattenSchedule idle in schedules)
            {
                if (FlattenSchedule.Decide(idle, now, hasOpenPosition: false).TimeUntilDeadline <= TimeSpan.Zero)
                {
                    _metrics.RecordFlattenDeadline(FlattenTier.Primary, ExecutionMetrics.FlattenNothingToDo);
                }

                // gh#497: being flat IS the incident-over signal, WHOEVER produced it. Resolve fired only on the
                // flatten's own success -- and an escalation is by construction the path where the close did not
                // succeed, so the exposure ends by other hands (the operator, a prop firm's forced flatten) and
                // nothing re-armed the key. Every later escalation for that pair was then suppressed as a
                // duplicate, silently, for the life of the process.
                //
                // Nothing is open here, so every configured instrument is clear -- no root matching needed, and
                // no venue round trip added to the common flat pass.
                await ResolveAsync(IncidentKey(account, idle.Instrument), cancellationToken);
            }

            return 0;
        }

        // Match each open position to its schedule by PRODUCT ROOT, so any month of a configured product finds its
        // deadline (gh#163). The forward resolve pins each schedule's root once.
        Dictionary<string, FlattenSchedule> byRoot = new(StringComparer.Ordinal);
        foreach (FlattenSchedule schedule in schedules)
        {
            ResolvedContract resolved = await venue.ResolveContractAsync(schedule.Instrument, cancellationToken);
            byRoot[ProductRoot(resolved.Contract.Key)] = schedule;
        }

        // gh#497, the partial case: the account still holds something, so the "nothing open" branch above is never
        // reached -- but an instrument that IS clear still needs its key re-armed, or a later escalation on it is
        // suppressed as a duplicate. The root map is already built, so this costs no extra venue call.
        HashSet<string> exposedRoots =
            [.. open.Select(position => ProductRoot(position.Contract.Key))];
        foreach ((string root, FlattenSchedule configured) in byRoot)
        {
            if (!exposedRoots.Contains(root))
            {
                await ResolveAsync(IncidentKey(account, configured.Instrument), cancellationToken);
            }
        }

        int closed = 0;
        foreach (IGrouping<string, PositionSnapshot> group in open.GroupBy(position => ProductRoot(position.Contract.Key)))
        {
            VenueContractId contract = group.First().Contract;

            if (!byRoot.TryGetValue(group.Key, out FlattenSchedule? schedule))
            {
                // A live position in a product with no configured deadline: we cannot time its flatten, but it
                // must never be silent (R-13). Flag it; the full gap-handling is gh#187.
                _logger.LogError("Auto-flatten: open position in an unconfigured product {Root} on {Account}.", group.Key, account);
                await JournalAsync(UnconfiguredEventType, account, contract,
                    $"Open position in an unconfigured product ({group.Key}) — no auto-flatten deadline is set for it.",
                    now, cancellationToken);

                // Metered distinctly from `disabled` (gh#370): a market nobody configured and one deliberately
                // switched off are different operator errors, and only the first is a surprise. Folding them
                // together meant an unconfigured product with a live position could not be alerted on at all.
                _metrics.RecordFlattenDeadline(FlattenTier.Primary, ExecutionMetrics.FlattenUnconfigured);
                continue;
            }

            FlattenDecision decision = FlattenSchedule.Decide(schedule, now, hasOpenPosition: true);
            switch (decision.Action)
            {
                case FlattenAction.Flatten:
                    {
                        long startedTicks = Stopwatch.GetTimestamp();
                        bool flat = await CloseGroupAsync(
                            account, ownerUserId, venue, schedule, [.. group], maxAttempts, decision, now, cancellationToken);
                        if (flat)
                        {
                            closed++;
                            _metrics.RecordTimeToFlat(FlattenTier.Primary, Stopwatch.GetElapsedTime(startedTicks));
                        }

                        _metrics.RecordFlattenDeadline(
                            FlattenTier.Primary,
                            flat ? ExecutionMetrics.FlattenExecuted : ExecutionMetrics.FlattenEscalated);
                        break;
                    }

                case FlattenAction.Warn:
                    await JournalAsync(WarningEventType, account, contract, decision.Reason, now, cancellationToken);

                    // The pre-deadline warning PRD P1 asks for. Journalled since gh#12 but never metered, so the
                    // one signal that arrives BEFORE exposure becomes a problem could not raise an alert (gh#370).
                    _metrics.RecordFlattenDeadline(FlattenTier.Primary, ExecutionMetrics.FlattenWarning);
                    break;

                case FlattenAction.Missed:
                    _logger.LogError("Auto-flatten MISSED for {Account} {Instrument}: {Reason}", account, schedule.Instrument, decision.Reason);

                    // Also P1. Usually the same incident as the escalation above, so dedup collapses the two --
                    // but a process that only came up AFTER the deadline reaches this without ever escalating, and
                    // that operator still needs waking.
                    //
                    // Enlisted BEFORE the journal (gh#455): the row joins this pass's unit of work and the append's
                    // save commits both, so "MISSED is journalled and nobody was told" cannot happen.
                    await EnlistPageAsync(
                        NotificationSeverity.Page,
                        $"Auto-flatten MISSED — {schedule.Instrument}",
                        decision.Reason,
                        IncidentKey(account, schedule.Instrument),
                        cancellationToken);

                    await JournalAsync(MissedEventType, account, contract, decision.Reason, now, cancellationToken);

                    // The most incident-worthy auto-flatten outcome: the deadline passed with exposure still open and
                    // NO close fired (gh#765 review). It belongs in the immutable trail beside the executed/escalated
                    // rows, not only the event log + page. (Disabled / Unconfigured are operator-config states — the
                    // position may be deliberately live — and stay in the event log.)
                    await WriteFlattenAuditAsync(
                        ownerUserId, after: "Missed",
                        $"Auto-flatten MISSED: {schedule.Instrument} on {account.Key} — deadline passed with exposure "
                        + $"still open, no close fired. {decision.Reason}",
                        now, cancellationToken);
                    _metrics.RecordFlattenDeadline(FlattenTier.Primary, ExecutionMetrics.FlattenMissed);
                    break;

                case FlattenAction.Disabled:
                    await JournalAsync(DisabledEventType, account, contract, decision.Reason, now, cancellationToken);
                    _metrics.RecordFlattenDeadline(FlattenTier.Primary, ExecutionMetrics.FlattenDisabled);
                    break;

                case FlattenAction.None:
                default:
                    break;
            }
        }

        return closed;
    }

    /// <summary>
    /// Closes every contract in one instrument's group, verifying against the venue after each round and retrying
    /// to the attempt cap. Returns whether the instrument ended flat; escalates loudly if it did not.
    /// </summary>
    private async Task<bool> CloseGroupAsync(
        VenueAccountId account,
        Guid ownerUserId,
        ITradingVenue venue,
        FlattenSchedule schedule,
        IReadOnlyList<PositionSnapshot> group,
        int maxAttempts,
        FlattenDecision decision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PositionSnapshot> outstanding = group;
        int attempts = 0;

        while (true)
        {
            attempts++;

            List<PositionSnapshot> postClose = [];
            foreach (PositionSnapshot position in outstanding)
            {
                // Close outright and reconcile against the venue's OWN post-close view, never a local belief
                // (ADR-0013). This only ever reduces exposure — the one order action taken without confirmation.
                PositionSnapshot after = await venue.ClosePositionAsync(account, position.Contract, cancellationToken);
                postClose.Add(after);
            }

            FlattenVerdict verdict = FlattenVerification.Verify(postClose, attempts, maxAttempts);
            switch (verdict)
            {
                case FlattenVerdict.Flat:
                    await JournalAsync(ExecutedEventType, account, group[0].Contract,
                        $"{decision.Reason} Flat after {attempts} attempt(s).", now, cancellationToken);

                    // The immutable §9 history row (gh#765): the safety-critical close ran and confirmed flat. Beside
                    // the event-log append above, this is the durable "what closed, and did it work" an incident reads.
                    await WriteFlattenAuditAsync(
                        ownerUserId, after: "Flat",
                        $"Auto-flatten closed {schedule.Instrument} on {account.Key} — flat after {attempts} attempt(s).",
                        now, cancellationToken);

                    // The incident is over. Cancels any page still nagging from an earlier pass and re-arms the
                    // key, so a LATER failure today is reported as the new incident it is (gh#243).
                    //
                    // Fault-absorbed since gh#455: this was the one channel call on the flatten's path with no
                    // belt, so a throwing resolve aborted the pass -- on the SUCCESS path, after the position was
                    // already closed. The send beside it had been wrapped since gh#243; this had been missed.
                    await ResolveAsync(IncidentKey(account, schedule.Instrument), cancellationToken);
                    return true;

                case FlattenVerdict.Retry:
                    outstanding = [.. postClose.Where(position => !position.IsFlat)];
                    continue;

                case FlattenVerdict.Escalate:
                default:
                    _logger.LogError(
                        "Auto-flatten could not confirm {Account} {Instrument} flat after {Attempts} attempt(s).",
                        account, schedule.Instrument, attempts);

                    // P1 — page now, not at the firing window (ADR-0019). `flatten.missed` lands 60 min past the
                    // deadline, AFTER a prop venue's own forced flatten would have closed the position, and a live
                    // brokerage has no backstop at all. This is the moment the operator can still act.
                    //
                    // Enlisted BEFORE the journal (gh#455), which is the whole mechanism: the page becomes part of
                    // the same transaction the escalation is written in, so the two commit together or neither
                    // does. Sent afterwards, as it was, a crash in between left the escalation on record with the
                    // operator never told -- the one failure this alert exists to prevent.
                    await EnlistPageAsync(
                        NotificationSeverity.Page,
                        $"Auto-flatten escalated — {schedule.Instrument}",
                        $"{schedule.Instrument} on {account.Key} is STILL EXPOSED after {attempts} close attempt(s). "
                        + "The position did not close at its deadline and needs manual intervention now.",
                        IncidentKey(account, schedule.Instrument),
                        cancellationToken);

                    await JournalAsync(EscalatedEventType, account, group[0].Contract,
                        $"ESCALATE: {schedule.Instrument} still exposed after {attempts} close attempt(s) — {decision.Reason}",
                        now, cancellationToken);

                    // The immutable §9 record of the outcome that matters most: the close did NOT confirm flat and
                    // exposure remains (gh#765) — "auto-flatten ran, and here is what it could not close".
                    await WriteFlattenAuditAsync(
                        ownerUserId, after: "Escalated",
                        $"Auto-flatten ESCALATED: {schedule.Instrument} on {account.Key} still exposed after "
                        + $"{attempts} attempt(s) — manual intervention needed.",
                        now, cancellationToken);
                    return false;
            }
        }
    }

    /// <summary>
    /// Stages the operator's page <b>into this pass's own transaction</b> (gh#455), absorbing any fault. The row
    /// is committed by the journal append that follows, so the page and the state it reports become atomic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Call this immediately before the <see cref="JournalAsync"/> that records the same fact.</b> Enlisting
    /// stages without saving, so a call with no save behind it records nothing at all — no exception, no page.
    /// That is the cost of joining someone else's transaction (<c>INotificationEnlister</c>).
    /// </para>
    /// <para>
    /// Alerting is a <b>secondary</b> concern to closing a position: a channel that is down, slow, or
    /// misconfigured must never fail or abort a flatten — that would be the self-inflicted wound ADR-0019 rules
    /// out, where the thing meant to report trouble becomes the trouble. The adapters are already
    /// failure-tolerant; this is the belt to that braces, since a flatten is the one action the system takes
    /// without confirmation. It matters more now than it did behind a send: this runs <i>inside</i> the flatten's
    /// flow rather than after it.
    /// </para>
    /// </remarks>
    private async Task EnlistPageAsync(
        NotificationSeverity severity, string title, string body, string incidentKey, CancellationToken cancellationToken)
    {
        try
        {
            await _enlister.EnlistAsync(new Notification(severity, title, body, incidentKey), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a real shutdown still stops the host
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not record a page for {Incident}; the flatten is unaffected.", incidentKey);
        }
    }

    /// <summary>
    /// Closes an incident, absorbing any fault — same reasoning as <see cref="EnlistPageAsync"/>, and it needs the
    /// belt just as much: a resolve reaches the transport (a Pushover Emergency nags until cancelled), so unlike
    /// an enlist it really does touch a network.
    /// </summary>
    private async Task ResolveAsync(string incidentKey, CancellationToken cancellationToken)
    {
        try
        {
            await _notifications.ResolveAsync(incidentKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a real shutdown still stops the host
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not resolve {Incident}; the flatten is unaffected.", incidentKey);
        }
    }

    private Task JournalAsync(
        string type,
        VenueAccountId account,
        VenueContractId contract,
        string reason,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new
        {
            account = account.Key,
            contract = contract.Key,
            reason,
        });

        return _eventLog.AppendAsync(new EventDraft(type, EventSource, occurredAt, payload), cancellationToken);
    }

    /// <summary>
    /// Journals the R-13 fact that a held account was absent from the venue's live roster (gh#527), so the skip is
    /// observable rather than silent. There is no contract or venue account id to carry — the venue never named the
    /// account — so this records the key we hold and why it was not evaluated, not reusing <see cref="JournalAsync"/>.
    /// </summary>
    private Task JournalUnrosteredAsync(Account account, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new
        {
            account = account.VenueAccountKey,
            reason = "held account absent from the venue roster; not evaluated this pass",
        });

        return _eventLog.AppendAsync(new EventDraft(UnrosteredEventType, EventSource, occurredAt, payload), cancellationToken);
    }

    /// <summary>
    /// Writes the immutable §9 audit row for an auto-flatten outcome (gh#765) — a <b>secondary</b> write behind the
    /// close, so a failure loses a history row but must never fail or abort the flatten, the one action taken without
    /// confirmation (the <see cref="IAuditLog"/> contract). The owner is the account's (this host has no ambient
    /// user); an auto-flatten rests on no single protective leg, so its <c>Placement</c> is
    /// <see cref="AuditPlacement.None"/> and it carries no synthetic-risk flag. <paramref name="detail"/> is bounded
    /// to the column width.
    /// </summary>
    private async Task WriteFlattenAuditAsync(
        Guid ownerUserId, string after, string detail, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            AuditRecord record = new()
            {
                Id = Guid.NewGuid(),
                UserId = ownerUserId,
                Action = AuditAction.AutoFlatten,
                Placement = AuditPlacement.None,
                Source = AuditSource.Scheduler,
                SyntheticRisk = false,
                After = after,
                Detail = detail.Length > MaxDetail ? detail[..MaxDetail] : detail,
                RecordedAt = now,
            };
            await _auditLog.WriteAsync([record], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a real shutdown still stops the host
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not write the auto-flatten audit record; the flatten itself is unaffected.");
        }
    }

    // The product root shared by every month of a contract (F.US.EP for ES). Single-venue today (ProjectX); when a
    // second venue lands this belongs behind the venue abstraction as a reverse contract -> instrument mapping.
    private static string ProductRoot(string contractKey) => ProjectXMapping.ToQuoteSymbol(contractKey);
}
