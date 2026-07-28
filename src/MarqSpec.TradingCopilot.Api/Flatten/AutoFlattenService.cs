using System.Diagnostics;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
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

    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IEventLog _eventLog;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly FlattenOptions _options;
    private readonly INotificationChannel _notifications;
    private readonly IExecutionMetrics _metrics;
    private readonly ILogger<AutoFlattenService> _logger;

    /// <summary>Creates the service over the scoped database and event log.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="venueFactory">Builds a venue for a connection's firm conventions.</param>
    /// <param name="eventLog">The append-only journal every flatten action is recorded on (R-13).</param>
    /// <param name="projectXOptions">Carries the credential key this process serves (ADR-0015).</param>
    /// <param name="flattenOptions">The per-instrument schedule and attempt cap.</param>
    /// <param name="notifications">Reaches the operator away from the desk (gh#243, ADR-0019).</param>
    /// <param name="metrics">The execution SLIs (gh#232).</param>
    /// <param name="logger">The logger.</param>
    public AutoFlattenService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IEventLog eventLog,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<FlattenOptions> flattenOptions,
        INotificationChannel notifications,
        IExecutionMetrics metrics,
        ILogger<AutoFlattenService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);
        ArgumentNullException.ThrowIfNull(flattenOptions);

        _database = database;
        _venueFactory = venueFactory;
        _eventLog = eventLog;
        _projectX = projectXOptions.Value;
        _options = flattenOptions.Value;
        _notifications = notifications;
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
                    // The venue no longer reports this account -- a rediscovery concern, not the flatten's. Skip.
                    continue;
                }

                await FlattenAccountAsync(venueAccount.Id, venue, schedules, now, maxAttempts, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Flattens whatever is due on one account at <paramref name="now"/>: reads the venue's live positions, matches
    /// each to its instrument's schedule, and acts on the domain decision. The per-account safety core.
    /// </summary>
    /// <param name="account">The venue account to evaluate.</param>
    /// <param name="venue">The venue to read positions from and close through.</param>
    /// <param name="schedules">The per-instrument schedules in force.</param>
    /// <param name="now">The instant to evaluate against.</param>
    /// <param name="maxAttempts">How many close attempts before escalating.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many instruments were closed and verified flat.</returns>
    internal async Task<int> FlattenAccountAsync(
        VenueAccountId account,
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
                        bool flat = await CloseGroupAsync(account, venue, schedule, [.. group], maxAttempts, decision, now, cancellationToken);
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
                    await JournalAsync(MissedEventType, account, contract, decision.Reason, now, cancellationToken);
                    _metrics.RecordFlattenDeadline(FlattenTier.Primary, ExecutionMetrics.FlattenMissed);

                    // Also P1. Usually the same incident as the escalation above, so dedup collapses the two --
                    // but a process that only came up AFTER the deadline reaches this without ever escalating, and
                    // that operator still needs waking.
                    await NotifyAsync(
                        NotificationSeverity.Page,
                        $"Auto-flatten MISSED — {schedule.Instrument}",
                        decision.Reason,
                        IncidentKey(account, schedule.Instrument),
                        cancellationToken);
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

                    // The incident is over. Cancels any page still nagging from an earlier pass and re-arms the
                    // key, so a LATER failure today is reported as the new incident it is (gh#243).
                    await _notifications.ResolveAsync(
                        IncidentKey(account, schedule.Instrument), cancellationToken);
                    return true;

                case FlattenVerdict.Retry:
                    outstanding = [.. postClose.Where(position => !position.IsFlat)];
                    continue;

                case FlattenVerdict.Escalate:
                default:
                    _logger.LogError(
                        "Auto-flatten could not confirm {Account} {Instrument} flat after {Attempts} attempt(s).",
                        account, schedule.Instrument, attempts);
                    await JournalAsync(EscalatedEventType, account, group[0].Contract,
                        $"ESCALATE: {schedule.Instrument} still exposed after {attempts} close attempt(s) — {decision.Reason}",
                        now, cancellationToken);

                    // P1 — page now, not at the firing window (ADR-0019). `flatten.missed` lands 60 min past the
                    // deadline, AFTER a prop venue's own forced flatten would have closed the position, and a live
                    // brokerage has no backstop at all. This is the moment the operator can still act.
                    await NotifyAsync(
                        NotificationSeverity.Page,
                        $"Auto-flatten escalated — {schedule.Instrument}",
                        $"{schedule.Instrument} on {account.Key} is STILL EXPOSED after {attempts} close attempt(s). "
                        + "The position did not close at its deadline and needs manual intervention now.",
                        IncidentKey(account, schedule.Instrument),
                        cancellationToken);
                    return false;
            }
        }
    }

    /// <summary>
    /// Notifies the operator, absorbing any fault. Alerting is a <b>secondary</b> concern to closing a position:
    /// a channel that is down, slow, or misconfigured must never fail or abort a flatten — that would be the
    /// self-inflicted wound ADR-0019 rules out, where the thing meant to report trouble becomes the trouble.
    /// The adapters are already failure-tolerant; this is the belt to that braces, since a flatten is the one
    /// action the system takes without confirmation.
    /// </summary>
    private async Task NotifyAsync(
        NotificationSeverity severity,
        string title,
        string body,
        string incidentKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await _notifications.SendAsync(new Notification(severity, title, body, incidentKey), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a real shutdown still stops the host
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not notify the operator about {Incident}; the flatten is unaffected.", incidentKey);
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

    // The product root shared by every month of a contract (F.US.EP for ES). Single-venue today (ProjectX); when a
    // second venue lands this belongs behind the venue abstraction as a reverse contract -> instrument mapping.
    private static string ProductRoot(string contractKey) => ProjectXMapping.ToQuoteSymbol(contractKey);
}
