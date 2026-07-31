using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Kill;

/// <summary>
/// Engages and disengages the kill switch (ADR-0007, R-11, gh#189). Engaging <b>disables outbound orders first</b>
/// (the runtime flag, then the durable row), then <b>cancels working orders</b>, then — per the requested mode —
/// either <b>flattens all open positions</b> (native-first) or <b>halts only</b>, leaving them on their native
/// safety stops. Disengaging re-enables outbound. Every transition is journaled (R-8).
/// </summary>
/// <remarks>
/// This runs on the authenticated operator's request, so it uses the request-scoped context (R-20 shows the
/// operator's own accounts and orders) and acts only over the accounts this process's one credential set can reach
/// (ADR-0015). Flattening reuses the venue's <see cref="IOrderExecutor.ClosePositionAsync"/> + the
/// <see cref="FlattenVerification"/> discipline — the same native-first close as auto-flatten, but with no deadline:
/// the kill closes everything now.
/// </remarks>
public sealed class KillSwitchService
{
    /// <summary>The <c>Source</c> stamped on every kill-switch event.</summary>
    public const string EventSource = "kill-switch";

    /// <summary>The kill switch was engaged — outbound disabled, working orders cancelled, positions flattened or halted.</summary>
    public const string EngagedEventType = "killswitch.engaged";

    /// <summary>The kill switch was disengaged — outbound orders re-enabled.</summary>
    public const string DisengagedEventType = "killswitch.disengaged";

    /// <summary>The kill switch could not confirm an account flat — positions remain open (gh#529).</summary>
    public const string EscalatedEventType = "killswitch.escalated";

    private const int MaxFlattenAttempts = 3;

    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IEventLog _eventLog;
    private readonly KillSwitch _killSwitch;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly ILogger<KillSwitchService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The request-scoped database (R-20 — the operator's own rows).</param>
    /// <param name="venueFactory">Builds a venue for a connection's firm conventions.</param>
    /// <param name="eventLog">The append-only journal every transition is recorded on (R-8).</param>
    /// <param name="killSwitch">The runtime kill-switch state this service flips.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="logger">The logger.</param>
    public KillSwitchService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IEventLog eventLog,
        KillSwitch killSwitch,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        ILogger<KillSwitchService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);

        _database = database;
        _venueFactory = venueFactory;
        _eventLog = eventLog;
        _killSwitch = killSwitch;
        _projectX = projectXOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Engages the kill switch: disables outbound orders, cancels working orders, and flattens-or-halts per
    /// <paramref name="mode"/>.
    /// </summary>
    /// <param name="mode">Flatten all open positions, or halt only (leave them on their native safety stops).</param>
    /// <param name="reason">The operator's reason, if given.</param>
    /// <param name="now">The instant of the action — the caller's clock read.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>What the engage did — how many orders were cancelled and positions flattened.</returns>
    public async Task<KillSwitchReport> EngageAsync(
        KillSwitchMode mode, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Block outbound FIRST -- flip the runtime flag and persist the lock -- so no new order can slip out while
        // we cancel and flatten. The durable row is what a restart rehydrates (the operator's lock persists).
        _killSwitch.Engage(mode, now, reason);
        await PersistAsync(engaged: true, mode, now, reason, cancellationToken);

        int cancelled = 0;
        int flattened = 0;
        List<string> failedAccounts = [];

        List<Account> accounts = await _database.Accounts.ToListAsync(cancellationToken);
        List<Guid> connectionIds = [.. accounts.Select(account => account.ConnectionId).Distinct()];
        Dictionary<Guid, Connection> connections = await _database.Connections
            .Where(connection => connectionIds.Contains(connection.Id))
            .ToDictionaryAsync(connection => connection.Id, cancellationToken);

        foreach (IGrouping<Guid, Account> byConnection in accounts.GroupBy(account => account.ConnectionId))
        {
            if (!connections.TryGetValue(byConnection.Key, out Connection? connection)
                || !string.Equals(connection.CredentialKey, _projectX.CredentialKey, StringComparison.Ordinal))
            {
                continue; // one credential set per process (ADR-0015): not ours to act on
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

                // PER-ACCOUNT FAULT ISOLATION (gh#529). ProjectXVenue.ClosePositionAsync THROWS on an ordinary
                // venue refusal -- a refusal is an exception here, not a return value -- and there is no exception
                // handler anywhere above this. Unisolated, one account's refusal unwound both loops: the remaining
                // contracts on it, every other account, and every other connection were never touched, the tracked
                // Cancelled statuses were discarded with the scope, and the engagement was never journalled. The
                // operator got a 500 naming no account, from the control they reach for when something has already
                // gone wrong.
                //
                // This is the invariant CancelWorkingOrdersAsync already states one call away: "a single cancel
                // failing must not abort the kill -- log it and press on with the rest."
                try
                {
                    cancelled += await CancelWorkingOrdersAsync(account, venueAccount.Id, venue, cancellationToken);

                    // Halt-only leaves open positions on their native safety stops (ADR-0007); only flatten-all closes them.
                    if (mode == KillSwitchMode.FlattenAll)
                    {
                        flattened += await FlattenAllAsync(venueAccount.Id, venue, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // a real shutdown still stops the host
                }
                catch (Exception error)
                {
                    // Named, counted and pressed past. The account is reported as failed rather than silently
                    // dropped: an operator who engaged the kill switch must be told which accounts it could not
                    // reach, because those are the ones they now have to handle by hand.
                    failedAccounts.Add(account.VenueAccountKey);
                    _logger.LogError(
                        error,
                        "Kill switch could not complete {Account} — continuing with the remaining accounts.",
                        account.VenueAccountKey);
                }
            }
        }

        // Both of these are now REACHABLE on the fault path (gh#529). They used to sit past an unguarded loop, so
        // a single venue refusal discarded the tracked Cancelled statuses and skipped the killswitch.engaged
        // append entirely -- ADR-0007 says every transition is journaled, and this one was not.
        await _database.SaveChangesAsync(cancellationToken);

        string failureNote = failedAccounts.Count == 0
            ? string.Empty
            : $" INCOMPLETE — could not reach: {string.Join(", ", failedAccounts)}.";

        _logger.LogWarning(
            "Kill switch ENGAGED ({Mode}): {Cancelled} working order(s) cancelled, {Flattened} position(s) flattened.{Failures}",
            mode, cancelled, flattened, failureNote);
        await JournalAsync(
            EngagedEventType,
            $"Kill switch engaged ({mode}): {cancelled} working order(s) cancelled, {flattened} position(s) flattened."
            + failureNote
            + (reason is null ? string.Empty : $" Reason: {reason}"),
            now, cancellationToken);

        return new KillSwitchReport(mode, cancelled, flattened);
    }

    /// <summary>Disengages the kill switch — outbound orders are allowed again.</summary>
    /// <param name="now">The instant of the action.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public async Task DisengageAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        _killSwitch.Disengage();
        await PersistAsync(engaged: false, KillSwitchMode.FlattenAll, now, reason: null, cancellationToken);

        _logger.LogWarning("Kill switch DISENGAGED — outbound orders are enabled again.");
        await JournalAsync(DisengagedEventType, "Kill switch disengaged — outbound orders re-enabled.", now, cancellationToken);
    }

    private async Task<int> CancelWorkingOrdersAsync(
        Account account, VenueAccountId venueAccount, ITradingVenue venue, CancellationToken cancellationToken)
    {
        // Working ORDER rows are resting entries (the protective stops are venue-managed brackets / StopPlans, never
        // Order rows), so cancelling them leaves a halted position's safety stop standing -- exactly halt-only's rule.
        List<Order> working = await _database.Orders
            .Where(order => order.AccountId == account.Id
                && order.Status == OrderStatus.Working
                && order.VenueOrderKey != null)
            .ToListAsync(cancellationToken);

        int cancelled = 0;
        foreach (Order order in working)
        {
            try
            {
                await venue.CancelOrderAsync(venueAccount, order.VenueOrderKey!, cancellationToken);
                order.Status = OrderStatus.Cancelled;
                cancelled++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // A single cancel failing must not abort the kill -- log it and press on with the rest.
                _logger.LogError(error, "Kill switch could not cancel working order {OrderId} ({Venue}).", order.Id, order.VenueOrderKey);
            }
        }

        return cancelled;
    }

    private async Task<int> FlattenAllAsync(VenueAccountId account, ITradingVenue venue, CancellationToken cancellationToken)
    {
        // Close every open position now -- no deadline, unlike auto-flatten -- reconciling against venue truth and
        // retrying to the cap. This only ever reduces exposure.
        IReadOnlyList<PositionSnapshot> outstanding =
            [.. (await venue.GetPositionsAsync(account, cancellationToken)).Where(position => !position.IsFlat)];
        if (outstanding.Count == 0)
        {
            return 0;
        }

        int attempts = 0;

        // Counted AS THEY CLOSE (gh#529). Both previous returns derived the count from `outstanding`, which is
        // rebound each attempt to only what is still open -- so the success path reported the LAST attempt's count
        // (three positions closed across two attempts reported 1), and the escalate path returned
        // `outstanding.Count(p => p.IsFlat)`, which is structurally ALWAYS 0 because `outstanding` is only ever
        // assigned from `.Where(p => !p.IsFlat)`. Only the single-attempt case was right -- the only case any test
        // covered.
        int closed = 0;

        while (true)
        {
            attempts++;

            List<PositionSnapshot> postClose = [];
            foreach (PositionSnapshot position in outstanding)
            {
                try
                {
                    postClose.Add(await venue.ClosePositionAsync(account, position.Contract, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    // A venue refusal is an EXCEPTION on this path, not a return value. One contract refusing must
                    // not abandon the others on this account: keep it outstanding so the retry loop takes another
                    // run at it, and let the escalate branch report it if it never closes.
                    _logger.LogError(
                        error, "Kill switch could not close {Account} {Contract} — will retry within this pass.",
                        account, position.Contract);
                    postClose.Add(position);
                }
            }

            closed += postClose.Count(position => position.IsFlat);

            FlattenVerdict verdict = FlattenVerification.Verify(postClose, attempts, MaxFlattenAttempts);
            if (verdict == FlattenVerdict.Flat)
            {
                return closed;
            }

            if (verdict == FlattenVerdict.Escalate)
            {
                int remaining = postClose.Count(position => !position.IsFlat);

                // JOURNALLED, not merely logged. Before this the only trace was a LogError, so the response was
                // 200 OK with FlattenedPositions: 0 -- byte-identical to halt-only and to "nothing was open". An
                // operator could not tell "the kill switch flattened nothing because there was nothing" from "the
                // venue refused and positions are still open".
                await JournalAsync(
                    EscalatedEventType,
                    $"Kill switch could not confirm {account.Key} flat after {attempts} attempt(s) — "
                    + $"{remaining} position(s) remain open and need manual intervention.",
                    DateTimeOffset.UtcNow,
                    cancellationToken);

                _logger.LogError(
                    "Kill switch could not confirm {Account} flat after {Attempts} attempt(s) — {Remaining} position(s) remain.",
                    account, attempts, remaining);
                return closed;
            }

            outstanding = [.. postClose.Where(position => !position.IsFlat)];
        }
    }

    private async Task PersistAsync(
        bool engaged, KillSwitchMode mode, DateTimeOffset now, string? reason, CancellationToken cancellationToken)
    {
        KillSwitchState? row = await _database.KillSwitchStates.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new KillSwitchState { Id = KillSwitchState.SingletonId };
            _database.KillSwitchStates.Add(row);
        }

        row.Engaged = engaged;
        row.Mode = mode;
        row.EngagedAt = engaged ? now : null;
        row.Reason = engaged ? reason : null;
        row.UpdatedAt = now;

        await _database.SaveChangesAsync(cancellationToken);
    }

    private Task JournalAsync(string type, string reason, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new { reason });
        return _eventLog.AppendAsync(new EventDraft(type, EventSource, occurredAt, payload), cancellationToken);
    }
}

/// <summary>What engaging the kill switch did (ADR-0007, gh#189).</summary>
/// <param name="Mode">The mode applied to open positions.</param>
/// <param name="CancelledOrders">How many working orders were cancelled.</param>
/// <param name="FlattenedPositions">How many open positions were closed (0 in halt-only mode).</param>
public sealed record KillSwitchReport(KillSwitchMode Mode, int CancelledOrders, int FlattenedPositions);
