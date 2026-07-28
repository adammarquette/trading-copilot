using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
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
/// The redundant auto-flatten watchdog (gh#187, R-13, ADR-0013): the <b>independent second tier</b> above the
/// primary scheduler (gh#185). It exists so the flatten still fires when the primary tier is degraded — a hung or
/// crashed primary host, a per-pass give-up, a transient fault. It acts only on the primary's <i>failure</i> (a
/// position still open past its deadline plus a grace window), and then <b>persists</b> rather than giving up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately self-contained.</b> It does not call <see cref="AutoFlattenService"/> — its own trigger and
/// close logic, so a logic bug in the primary cannot disable it. It reuses only the proven, pure domain primitives
/// (<see cref="FlattenSchedule.Decide"/>, <see cref="FlattenVerification"/>, the venue's close). <b>Invariant:</b>
/// it enumerates the <i>same</i> account set as the primary (the ADR-0015 credential guard) — a divergence in which
/// accounts the two tiers act on would be a safety bug, so the enumeration below must stay in step with
/// <see cref="AutoFlattenService.RunPassAsync"/>.
/// </para>
/// <para>
/// It <b>respects a deliberately-disabled market</b> (R-13): a disabled market is the operator's warned, own-risk
/// choice to carry a position past close, so the watchdog leaves it alone — it backstops the primary's failures,
/// it does not override an explicit disable. A rejected close is <b>never a silent give-up</b>: it is journaled and
/// retried on the next pass. Past the firing window it escalates to a critical alarm rather than firing blind into
/// the settlement window. The still-open client-side fallback (a future PWA) and an opposing-market-order
/// alternative close are follow-ups, not this tier.
/// </para>
/// </remarks>
public sealed class AutoFlattenWatchdogService
{
    /// <summary>The <c>Source</c> stamped on every watchdog event — distinct from the primary's, so the watchdog's actions are queryable on their own.</summary>
    public const string EventSource = "auto-flatten-watchdog";

    /// <summary>The watchdog closed a position the primary had left open past its deadline — it caught a miss.</summary>
    public const string SavedEventType = "flatten.watchdog.saved";

    /// <summary>A watchdog close was rejected or left exposure — journaled, not given up on; the next pass retries.</summary>
    public const string RejectedEventType = "flatten.watchdog.rejected";

    /// <summary>Exposure remains past the firing window — the loudest alarm; firing blind now is worse than escalating.</summary>
    public const string CriticalEventType = "flatten.watchdog.critical";

    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IEventLog _eventLog;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly FlattenOptions _options;
    private readonly INotificationChannel _notifications;
    private readonly IExecutionMetrics _metrics;
    private readonly ILogger<AutoFlattenWatchdogService> _logger;

    /// <summary>Creates the service over the scoped database and event log.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="venueFactory">Builds a venue for a connection's firm conventions.</param>
    /// <param name="eventLog">The append-only journal every watchdog action is recorded on (R-13).</param>
    /// <param name="projectXOptions">Carries the credential key this process serves (ADR-0015).</param>
    /// <param name="flattenOptions">The per-instrument schedule and the watchdog grace window.</param>
    /// <param name="notifications">Reaches the operator away from the desk (gh#243, ADR-0019).</param>
    /// <param name="metrics">The execution SLIs (gh#232) — the watchdog tags its own tier.</param>
    /// <param name="logger">The logger.</param>
    public AutoFlattenWatchdogService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IEventLog eventLog,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<FlattenOptions> flattenOptions,
        INotificationChannel notifications,
        IExecutionMetrics metrics,
        ILogger<AutoFlattenWatchdogService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);
        ArgumentNullException.ThrowIfNull(flattenOptions);

        _notifications = notifications;
        _metrics = metrics;

        _database = database;
        _venueFactory = venueFactory;
        _eventLog = eventLog;
        _projectX = projectXOptions.Value;
        _options = flattenOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs one watchdog pass over every account this process serves, backstopping any flatten the primary was due
    /// to make and did not. Mirrors the primary's enumeration deliberately (same account set — see the class note).
    /// </summary>
    /// <param name="now">The instant to evaluate against — the host's clock read.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public async Task RunPassAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<FlattenSchedule> schedules = _options.ToSchedules();
        TimeSpan grace = _options.WatchdogGrace;

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

            // One credential set per process (ADR-0015): an account on another key is a sibling process's to serve.
            if (!string.Equals(connection.CredentialKey, _projectX.CredentialKey, StringComparison.Ordinal))
            {
                continue;
            }

            FirmConventions conventions = await _database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
            ITradingVenue venue = _venueFactory.Create(conventions);

            IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);

            foreach (Account account in byConnection)
            {
                VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
                if (venueAccount is null)
                {
                    continue;
                }

                await BackstopAccountAsync(venueAccount.Id, venue, schedules, now, grace, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Backstops one account at <paramref name="now"/>: for any position the primary should have closed by now
    /// (open past its deadline + <paramref name="grace"/>) on an <b>enabled</b> market, closes it and persists on
    /// failure; escalates critically past the firing window.
    /// </summary>
    /// <param name="account">The venue account to evaluate.</param>
    /// <param name="venue">The venue to read positions from and close through.</param>
    /// <param name="schedules">The per-instrument schedules in force.</param>
    /// <param name="now">The instant to evaluate against.</param>
    /// <param name="grace">How long past the deadline to wait before stepping in (defers to the primary).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many positions the watchdog closed and verified flat this pass.</returns>
    internal async Task<int> BackstopAccountAsync(
        VenueAccountId account,
        ITradingVenue venue,
        IReadOnlyList<FlattenSchedule> schedules,
        DateTimeOffset now,
        TimeSpan grace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(venue);

        IReadOnlyList<PositionSnapshot> positions = await venue.GetPositionsAsync(account, cancellationToken);
        List<PositionSnapshot> open = [.. positions.Where(position => !position.IsFlat)];
        if (open.Count == 0)
        {
            return 0;
        }

        Dictionary<string, FlattenSchedule> byRoot = new(StringComparer.Ordinal);
        foreach (FlattenSchedule schedule in schedules)
        {
            ResolvedContract resolved = await venue.ResolveContractAsync(schedule.Instrument, cancellationToken);
            byRoot[ProductRoot(resolved.Contract.Key)] = schedule;
        }

        int saved = 0;
        foreach (PositionSnapshot position in open)
        {
            // An unconfigured product has no deadline to backstop; the primary owns flagging that gap (gh#185), so
            // the watchdog stays quiet here rather than double-reporting it.
            if (!byRoot.TryGetValue(ProductRoot(position.Contract.Key), out FlattenSchedule? schedule))
            {
                continue;
            }

            FlattenDecision decision = FlattenSchedule.Decide(schedule, now, hasOpenPosition: true);

            // Disabled markets, warnings, not-yet-due, and the still-within-grace window all fall through untouched:
            // a disabled market is the operator's own-risk choice, and inside the grace window the primary owns it.
            bool pastGrace = decision.Action == FlattenAction.Flatten && -decision.TimeUntilDeadline >= grace;
            if (pastGrace)
            {
                if (await BackstopCloseAsync(account, venue, position, schedule, decision, now, cancellationToken))
                {
                    saved++;
                }
            }
            else if (decision.Action == FlattenAction.Missed)
            {
                _logger.LogCritical(
                    "Auto-flatten watchdog CRITICAL for {Account} {Contract}: {Reason}",
                    account, position.Contract, decision.Reason);
                await JournalAsync(CriticalEventType, account, position.Contract, decision.Reason, now, cancellationToken);
                _metrics.RecordFlattenDeadline(FlattenTier.Watchdog, ExecutionMetrics.FlattenMissed);

                // P1. Shares the primary's incident key on purpose: the operator should be woken ONCE for one
                // exposed position, not once per tier that noticed (gh#243, ADR-0019 §*The noise budget*).
                await NotifyAsync(
                    NotificationSeverity.Page,
                    $"Auto-flatten watchdog CRITICAL — {schedule.Instrument}",
                    $"{schedule.Instrument} on {account.Key} is still exposed past its firing window and BOTH tiers "
                    + "have failed to close it. Manual intervention is required. " + decision.Reason,
                    AutoFlattenService.IncidentKey(account, schedule.Instrument),
                    cancellationToken);
            }
        }

        return saved;
    }

    /// <summary>
    /// Closes one position the primary missed and reconciles against the venue. Returns whether it ended flat;
    /// a rejection or lingering exposure is journaled and left for the next pass — never a silent give-up.
    /// </summary>
    private async Task<bool> BackstopCloseAsync(
        VenueAccountId account,
        ITradingVenue venue,
        PositionSnapshot position,
        FlattenSchedule schedule,
        FlattenDecision decision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            // Close outright and reconcile against the venue's OWN post-close view (ADR-0013). This only ever
            // reduces exposure. One attempt per pass: the watchdog's persistence is the next pass, not a tight loop.
            PositionSnapshot after = await venue.ClosePositionAsync(account, position.Contract, cancellationToken);
            if (after.IsFlat)
            {
                _logger.LogWarning(
                    "Auto-flatten watchdog closed {Account} {Contract} — the primary tier had left it open past its deadline.",
                    account, position.Contract);
                _metrics.RecordFlattenDeadline(FlattenTier.Watchdog, ExecutionMetrics.FlattenExecuted);
                await JournalAsync(SavedEventType, account, position.Contract,
                        $"Watchdog backstop closed the position — the primary did not. {decision.Reason}", now, cancellationToken);

                // The position is flat, so any outstanding page is cancelled and the incident re-armed. But a tier
                // is broken: the primary should have done this. P2 — worth the operator's attention before the
                // next session, not worth waking them now (ADR-0019).
                await ResolveAsync(AutoFlattenService.IncidentKey(account, schedule.Instrument), cancellationToken);
                await NotifyAsync(
                    NotificationSeverity.Notify,
                    $"Watchdog saved a flatten — {schedule.Instrument}",
                    $"The redundant watchdog closed {schedule.Instrument} on {account.Key} because the primary "
                    + "scheduler did not. The position is flat; the primary tier needs investigating before the next session.",
                    $"watchdog-saved:{account.Key}:{schedule.Instrument}",
                    cancellationToken);
                return true;
            }

            // The close returned but the venue still shows exposure (partial fill / silent reject): persist.
            _metrics.RecordFlattenDeadline(FlattenTier.Watchdog, ExecutionMetrics.FlattenRejected);
            await JournalAsync(RejectedEventType, account, position.Contract,
                $"Watchdog close left {position.Contract.Key} still exposed — retrying next pass. {decision.Reason}",
                now, cancellationToken);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a clean stop -- let the host shut the pass down
        }
        catch (Exception error)
        {
            // A rejected / faulted close on one position must neither stall the pass nor be swallowed: log it,
            // journal it, and let the next pass try again (the watchdog persists).
            _logger.LogError(
                error, "Auto-flatten watchdog close was rejected for {Account} {Contract}; will retry next pass.",
                account, position.Contract);
            _metrics.RecordFlattenDeadline(FlattenTier.Watchdog, ExecutionMetrics.FlattenRejected);
            await JournalAsync(RejectedEventType, account, position.Contract,
                $"Watchdog close rejected ({error.Message}) — retrying next pass. {decision.Reason}", now, cancellationToken);
            return false;
        }
    }

    /// <summary>
    /// Notifies the operator, absorbing any fault. Alerting is <b>secondary</b> to closing a position: this is
    /// the last tier standing when the primary has already failed, so a notification channel that is down must
    /// never stop it doing its job (ADR-0019).
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
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not notify the operator about {Incident}; the watchdog is unaffected.", incidentKey);
        }
    }

    /// <summary>Closes an incident, absorbing any fault — same reasoning as <see cref="NotifyAsync"/>.</summary>
    private async Task ResolveAsync(string incidentKey, CancellationToken cancellationToken)
    {
        try
        {
            await _notifications.ResolveAsync(incidentKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not resolve incident {Incident}; the watchdog is unaffected.", incidentKey);
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

    // The product root shared by every month of a contract (F.US.EP for ES), single-venue today (ProjectX) — the
    // same rule the primary uses (gh#163); when a second venue lands it moves behind the venue abstraction.
    private static string ProductRoot(string contractKey) => ProjectXMapping.ToQuoteSymbol(contractKey);
}
