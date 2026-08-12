using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Audit;
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

    /// <summary>The audit <c>Detail</c> column's width (data dictionary §12); a longer summary is truncated to fit.</summary>
    private const int MaxDetail = 512;

    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IEventLog _eventLog;
    private readonly IAuditLog _auditLog;
    private readonly ICurrentUser _currentUser;
    private readonly KillSwitch _killSwitch;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly ILogger<KillSwitchService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The request-scoped database (R-20 — the operator's own rows).</param>
    /// <param name="venueFactory">Builds a venue for a connection's firm conventions.</param>
    /// <param name="eventLog">The append-only journal every transition is recorded on (R-8).</param>
    /// <param name="auditLog">The immutable audit trail (§9, gh#765) — every engage/disengage lands one row.</param>
    /// <param name="currentUser">The authenticated operator — the owner every audit row is stamped with (R-20).</param>
    /// <param name="killSwitch">The runtime kill-switch state this service flips.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="logger">The logger.</param>
    public KillSwitchService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IEventLog eventLog,
        IAuditLog auditLog,
        ICurrentUser currentUser,
        KillSwitch killSwitch,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        ILogger<KillSwitchService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);

        _database = database;
        _venueFactory = venueFactory;
        _eventLog = eventLog;
        _auditLog = auditLog;
        _currentUser = currentUser;
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
    /// <param name="source">What tripped the engage — the operator by default, the only trigger wired today; a
    /// guardrail or the dead-man's switch pass their own when those seams land (gh#765).</param>
    /// <returns>What the engage did — how many orders were cancelled and positions flattened.</returns>
    public async Task<KillSwitchReport> EngageAsync(
        KillSwitchMode mode,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        AuditSource source = AuditSource.Operator)
    {
        // The prior runtime state, captured before the flip, so the audit's Before reflects the real transition
        // (a re-engage records Engaged -> Engaged, not a fictional Disengaged).
        bool wasEngaged = _killSwitch.IsEngaged;

        // Block outbound FIRST -- flip the runtime flag and persist the lock -- so no new order can slip out while
        // we cancel and flatten. The durable row is what a restart rehydrates (the operator's lock persists).
        _killSwitch.Engage(mode, now, reason);
        await PersistAsync(engaged: true, mode, now, reason, cancellationToken);

        int cancelled = 0;
        int flattened = 0;
        int stillOpen = 0; // positions a FlattenAll could not confirm closed -- the escalation the audit must record
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
                        (int flat, int remaining) = await FlattenAllAsync(venueAccount.Id, venue, cancellationToken);
                        flattened += flat;
                        stillOpen += remaining;
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

        // A FlattenAll that could not confirm every position flat ESCALATED -- the worst kill outcome. It returns
        // normally (never throws), so without this it is invisible to failureNote and the audit row would read
        // byte-identical to "nothing was open" (gh#765 review). Record it so the trail is reconstructable alone.
        string escalationNote = stillOpen == 0
            ? string.Empty
            : $" ESCALATED — {stillOpen} position(s) still open, manual intervention needed.";
        string detail =
            $"Kill switch engaged ({mode}): {cancelled} working order(s) cancelled, {flattened} position(s) flattened."
            + failureNote
            + escalationNote
            + (reason is null ? string.Empty : $" Reason: {reason}");
        await JournalAsync(EngagedEventType, detail, now, cancellationToken);

        // The immutable §9 history row (gh#765), written beside the mutable KillSwitchState the read path uses so a
        // re-engage no longer overwrites and loses the prior transition. `After` marks an escalated kill distinctly so
        // an incident review can scan for it without parsing Detail.
        await WriteAuditAsync(
            AuditAction.KillSwitchEngaged, source,
            before: wasEngaged ? "Engaged" : "Disengaged",
            after: stillOpen == 0 ? $"Engaged ({mode})" : $"Engaged ({mode}, escalated)",
            detail, now, cancellationToken);

        return new KillSwitchReport(mode, cancelled, flattened);
    }

    /// <summary>Disengages the kill switch — outbound orders are allowed again.</summary>
    /// <param name="now">The instant of the action.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <param name="source">What tripped the disengage — the operator by default (gh#765).</param>
    public async Task DisengageAsync(
        DateTimeOffset now, CancellationToken cancellationToken, AuditSource source = AuditSource.Operator)
    {
        bool wasEngaged = _killSwitch.IsEngaged;

        _killSwitch.Disengage();
        await PersistAsync(engaged: false, KillSwitchMode.FlattenAll, now, reason: null, cancellationToken);

        _logger.LogWarning("Kill switch DISENGAGED — outbound orders are enabled again.");
        const string detail = "Kill switch disengaged — outbound orders re-enabled.";
        await JournalAsync(DisengagedEventType, detail, now, cancellationToken);
        await WriteAuditAsync(
            AuditAction.KillSwitchDisengaged, source,
            before: wasEngaged ? "Engaged" : "Disengaged", after: "Disengaged",
            detail, now, cancellationToken);
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

    /// <summary>Closes every open position on the account. Returns how many closed and how many remain open after
    /// escalation, so the engage audit can record a kill that could <b>not</b> confirm flat (gh#765).</summary>
    private async Task<(int Closed, int StillOpen)> FlattenAllAsync(
        VenueAccountId account, ITradingVenue venue, CancellationToken cancellationToken)
    {
        // Close every open position now -- no deadline, unlike auto-flatten -- reconciling against venue truth and
        // retrying to the cap. This only ever reduces exposure.
        IReadOnlyList<PositionSnapshot> outstanding =
            [.. (await venue.GetPositionsAsync(account, cancellationToken)).Where(position => !position.IsFlat)];
        if (outstanding.Count == 0)
        {
            return (0, 0);
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
                return (closed, 0);
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
                return (closed, remaining);
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

    /// <summary>
    /// Writes the immutable §9 audit row for a kill-switch transition (gh#765) — a <b>secondary</b> write behind the
    /// safety action: the engage/disengage already committed, so a failure here loses a history row but must never
    /// abort the kill (the <see cref="IAuditLog"/> contract). A kill/flatten rests on no single protective leg, so its
    /// <c>Placement</c> is <see cref="AuditPlacement.None"/> and it carries no synthetic-risk flag; the owner is the
    /// authenticated operator (R-20). <paramref name="detail"/> is bounded to the column width.
    /// </summary>
    private async Task WriteAuditAsync(
        AuditAction action,
        AuditSource source,
        string before,
        string after,
        string detail,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            AuditRecord record = new()
            {
                Id = Guid.NewGuid(),

                // Owner = the authenticated operator (R-20). The only caller today is the operator endpoint, so this
                // is always populated. A FUTURE background trigger (the guardrail / dead-man's-switch `source` values
                // this anticipates) has no ambient user, so it must supply the owner -- as the auto-flatten path does
                // from the account -- rather than rely on this: a Guid.Empty owner would hide the row under the R-20
                // read filter, losing a safety record precisely when an automatic kill fired (gh#765 review).
                UserId = _currentUser.UserId,
                Action = action,
                Placement = AuditPlacement.None,
                Source = source,
                SyntheticRisk = false,
                Before = before,
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
            _logger.LogError(
                error, "Could not write the kill-switch audit record ({Action}); the transition itself is unaffected.", action);
        }
    }
}

/// <summary>What engaging the kill switch did (ADR-0007, gh#189).</summary>
/// <param name="Mode">The mode applied to open positions.</param>
/// <param name="CancelledOrders">How many working orders were cancelled.</param>
/// <param name="FlattenedPositions">How many open positions were closed (0 in halt-only mode).</param>
public sealed record KillSwitchReport(KillSwitchMode Mode, int CancelledOrders, int FlattenedPositions);
