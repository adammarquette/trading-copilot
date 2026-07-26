using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The orphan guard (ADR-0007, ADR-0013, gh#209/gh#191): when the venue connection drops, a <b>hidden</b> working
/// stop can no longer be promoted, so it is marked <see cref="StopStaging.Orphaned"/> and the operator is warned;
/// on reconnect each orphaned stop is <b>re-validated against venue truth</b> before it re-arms. The native safety
/// stop remains the physical floor throughout — this concerns only the operator's <i>tighter</i> synthetic protection.
/// </summary>
/// <remarks>
/// Background plumbing with <b>no authenticated user</b>, so its queries <c>IgnoreQueryFilters</c> the R-20
/// default-deny filter — it acts for the deployment, over whoever owns the stops; ownership is preserved on write
/// (the same discipline as the stop-promotion watcher). Each transition is recorded as an immutable
/// <c>AuditRecord</c> carrying the <c>synthetic_risk</c> flag (gh#220), a <b>secondary</b> write that never fails
/// the safety action; the high-severity <b>log</b> remains the interim operator alert until the real-time channel
/// (Phase-4 SPA, gh#222) lands.
/// </remarks>
public sealed class OrphanGuardService
{
    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly IAuditLog _auditLog;
    private readonly IExecutionMetrics _metrics;
    private readonly ILogger<OrphanGuardService> _logger;

    /// <summary>Creates the guard over the scoped database.</summary>
    /// <param name="database">The database.</param>
    /// <param name="venueFactory">Builds a venue for a connection's firm conventions — the source of re-arm truth.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="auditLog">Records each synthetic-stop transition — a secondary, failure-tolerant write (gh#220).</param>
    /// <param name="metrics">The execution SLIs (gh#295) — live synthetic-risk exposure.</param>
    /// <param name="logger">The logger.</param>
    public OrphanGuardService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IAuditLog auditLog,
        IExecutionMetrics metrics,
        ILogger<OrphanGuardService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);

        _database = database;
        _venueFactory = venueFactory;
        _projectX = projectXOptions.Value;
        _auditLog = auditLog;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Orphans every hidden working stop on a venue-connection loss — they can no longer promote, so the
    /// operator's tighter protection is degraded to the native safety stop until reconnect.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many stops were orphaned.</returns>
    public async Task<int> OrphanAsync(CancellationToken cancellationToken)
    {
        List<StopPlanRecord> hidden = await _database.StopPlans
            .IgnoreQueryFilters()
            .Where(plan => plan.Staging == StopStaging.Hidden)
            .ToListAsync(cancellationToken);

        foreach (StopPlanRecord plan in hidden)
        {
            plan.Staging = StopStaging.Orphaned;
        }

        if (hidden.Count > 0)
        {
            await _database.SaveChangesAsync(cancellationToken);

            // An EMERGENCY, not a warning: a live position's tighter protection just degraded. High-severity so
            // it surfaces as the operator alert until the real-time channel (Phase-4 SPA, gh#222) lands. The
            // synthetic_risk marker is the flag the audit carries.
            _logger.LogError(
                "Venue connection lost — {Count} hidden stop(s) ORPHANED (synthetic_risk). The native safety "
                + "stop remains the floor; the tighter working stop re-arms on reconnect.",
                hidden.Count);

            // The audit is a SECONDARY write, after the safety action has already committed: each degradation is
            // recorded as an immutable synthetic_risk row (gh#220), but a failure here never undoes the orphaning.
            await AuditSafelyAsync(
                hidden.Select(plan => AuditFor(
                    plan, StopStaging.Hidden, StopStaging.Orphaned,
                    "Venue connection lost — hidden working stop orphaned; native safety stop remains the floor.")),
                cancellationToken);
        }

        await PublishOrphanExposureAsync(cancellationToken);
        return hidden.Count;
    }

    /// <summary>
    /// Publishes how many stops are orphaned RIGHT NOW (gh#295). Re-counted from the database rather than
    /// incremented, so the gauge cannot drift from reality and re-arming genuinely returns it to zero — a
    /// counter nudged by deltas would strand a stale non-zero reading after any missed transition.
    /// </summary>
    private async Task PublishOrphanExposureAsync(CancellationToken cancellationToken) =>
        _metrics.SetOrphanedStops(await _database.StopPlans
            .IgnoreQueryFilters()
            .CountAsync(plan => plan.Staging == StopStaging.Orphaned, cancellationToken));

    /// <summary>
    /// Re-arms every orphaned stop on reconnect — but only after <b>re-validating each against venue truth</b>
    /// (gh#191, ADR-0013). A plan whose position is still open re-arms to <see cref="StopStaging.Hidden"/>; one
    /// whose position closed (or reversed) during the outage is <see cref="StopStaging.Retired"/>, never re-armed;
    /// a partial close reconciles the protected quantity first; and a plan that <b>cannot be re-validated</b> — the
    /// venue is unreachable, or it belongs to another process's credential set — is left orphaned and retried on a
    /// later pass. Uncertainty resolves to the safe state: nothing re-arms on an unverified assumption (§9).
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many stops were re-armed, retired, and left orphaned (unverifiable).</returns>
    public async Task<RearmOutcome> RearmAsync(CancellationToken cancellationToken)
    {
        List<StopPlanRecord> orphaned = await _database.StopPlans
            .IgnoreQueryFilters()
            .Where(plan => plan.Staging == StopStaging.Orphaned)
            .ToListAsync(cancellationToken);
        if (orphaned.Count == 0)
        {
            return new RearmOutcome(0, 0, 0);
        }

        // The order carries the account, contract, side and size; the account carries the connection. Background
        // plumbing bypasses the R-20 filter deliberately (see the class note) — ownership is read, never re-assigned.
        List<Guid> orderIds = [.. orphaned.Select(plan => plan.OrderId).Distinct()];
        Dictionary<Guid, Order> orders = await _database.Orders
            .IgnoreQueryFilters()
            .Where(order => orderIds.Contains(order.Id))
            .ToDictionaryAsync(order => order.Id, cancellationToken);

        List<Guid> accountIds = [.. orders.Values.Select(order => order.AccountId).Distinct()];
        Dictionary<Guid, Account> accounts = await _database.Accounts
            .IgnoreQueryFilters()
            .Where(account => accountIds.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, cancellationToken);

        List<Guid> connectionIds = [.. accounts.Values.Select(account => account.ConnectionId).Distinct()];
        Dictionary<Guid, Connection> connections = await _database.Connections
            .IgnoreQueryFilters()
            .Where(connection => connectionIds.Contains(connection.Id))
            .ToDictionaryAsync(connection => connection.Id, cancellationToken);

        int rearmed = 0;
        int retired = 0;
        int stillOrphaned = 0;
        List<AuditRecord> auditRows = []; // one per stop that actually transitions; a stop left orphaned records nothing

        // Group by the account the order sits on, so the venue and its positions are read once per account.
        foreach (IGrouping<Guid?, StopPlanRecord> byAccount in orphaned.GroupBy(plan =>
            orders.TryGetValue(plan.OrderId, out Order? order) ? order.AccountId : (Guid?)null))
        {
            int count = byAccount.Count();

            // A plan whose order / account / connection is gone cannot be re-validated. Never re-arm on nothing;
            // leave it orphaned (a stop with no order should not exist — the FK cascades — but fail safe if it does).
            if (byAccount.Key is not { } accountId
                || !accounts.TryGetValue(accountId, out Account? account)
                || !connections.TryGetValue(account.ConnectionId, out Connection? connection))
            {
                stillOrphaned += count;
                continue;
            }

            // One credential set per process (ADR-0015): not ours to re-validate — a sibling process serves it.
            if (!string.Equals(connection.CredentialKey, _projectX.CredentialKey, StringComparison.Ordinal))
            {
                stillOrphaned += count;
                continue;
            }

            IReadOnlyList<PositionSnapshot> positions;
            try
            {
                FirmConventions conventions = await _database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
                ITradingVenue venue = _venueFactory.Create(conventions);
                IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);
                VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
                if (venueAccount is null)
                {
                    stillOrphaned += count; // the venue no longer reports this account — cannot re-validate, leave orphaned
                    continue;
                }

                positions = await venue.GetPositionsAsync(venueAccount.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // Venue unreachable: never re-arm on an unverified assumption. Leave orphaned; the next pass retries.
                _logger.LogWarning(
                    error, "Re-arm re-validation could not reach the venue for account {Account}; {Count} stop(s) stay orphaned.",
                    account.Id, count);
                stillOrphaned += count;
                continue;
            }

            foreach (StopPlanRecord plan in byAccount)
            {
                Order order = orders[plan.OrderId];
                PositionSnapshot? position = positions
                    .FirstOrDefault(candidate => string.Equals(candidate.Contract.Key, order.Instrument, StringComparison.Ordinal));

                // Still open only if the venue reports net exposure on the SAME side the order entered — a flat or
                // reversed contract means the protected position is gone.
                int expectedSign = order.Side == OrderSide.Buy ? 1 : -1;
                bool stillOpen = position is not null && position.NetQuantity != 0 && Math.Sign(position.NetQuantity) == expectedSign;

                if (!stillOpen)
                {
                    plan.Staging = StopStaging.Retired;
                    retired++;
                    auditRows.Add(AuditFor(
                        plan, StopStaging.Orphaned, StopStaging.Retired,
                        "Reconnect re-validation: protected position closed or reversed during the outage — stop retired, never re-armed."));
                    _logger.LogInformation(
                        "Re-arm: {Contract} position closed during the outage — stop plan {Plan} RETIRED, not re-armed (ADR-0013).",
                        order.Instrument, plan.Id);
                    continue;
                }

                int remaining = Math.Abs(position!.NetQuantity);
                if (remaining < order.Size)
                {
                    // Partially closed during the outage: protect what actually remains, not the original quantity.
                    _logger.LogInformation(
                        "Re-arm: {Contract} partially closed ({Old} -> {New} lots) — reconciling before re-arm.",
                        order.Instrument, order.Size, remaining);
                    order.Size = remaining;
                }

                plan.Staging = StopStaging.Hidden;
                rearmed++;
                auditRows.Add(AuditFor(
                    plan, StopStaging.Orphaned, StopStaging.Hidden,
                    "Reconnect re-validation: position verified still open — synthetic working stop re-armed."));
            }
        }

        if (rearmed + retired > 0)
        {
            await _database.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Venue connection restored — re-validated {Total} orphaned stop(s): {Rearmed} re-armed, {Retired} retired, "
            + "{Still} left orphaned (unverifiable, will retry).",
            orphaned.Count, rearmed, retired, stillOrphaned);

        // Secondary write, after the transitions have committed: record each re-arm / retirement (gh#220). A stop
        // left orphaned transitioned to nothing, so it contributes no row — the exposure window stays reconstructable.
        await AuditSafelyAsync(auditRows, cancellationToken);

        // Re-arm must refresh the gauge too, or a recovered system reads as permanently degraded (gh#295).
        await PublishOrphanExposureAsync(cancellationToken);
        return new RearmOutcome(rearmed, retired, stillOrphaned);
    }

    // Builds a synthetic-stop audit entry, owned by the affected stop's operator (R-20) — stamped explicitly
    // because this runs as background plumbing with no ambient user. A working stop is platform-held, so every
    // transition recorded here is the synthetic_risk case: the placement is Synthetic and the flag is set.
    private static AuditRecord AuditFor(StopPlanRecord plan, StopStaging before, StopStaging after, string detail) => new()
    {
        UserId = plan.UserId,
        Action = AuditAction.ConnectionLoss,
        Placement = AuditPlacement.Synthetic,
        SyntheticRisk = true,
        StopPlanId = plan.Id,
        Before = before.ToString(),
        After = after.ToString(),
        Detail = detail,
        RecordedAt = DateTimeOffset.UtcNow, // a record-keeping timestamp, not a decision input
    };

    // Writes the audit rows tolerating any failure: the safety action has already committed and its high-severity
    // log stands, so an audit-store failure is logged and swallowed — it must never fail the guard (gh#220). Only a
    // cooperative cancellation propagates.
    private async Task AuditSafelyAsync(IEnumerable<AuditRecord> records, CancellationToken cancellationToken)
    {
        List<AuditRecord> rows = [.. records];
        if (rows.Count == 0)
        {
            return;
        }

        try
        {
            await _auditLog.WriteAsync(rows, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error, "Audit write failed for {Count} synthetic_risk transition(s); the safety action still completed.",
                rows.Count);
        }
    }
}

/// <summary>The result of a re-arm pass (gh#191).</summary>
/// <param name="Rearmed">Orphaned stops whose position was verified still open and re-armed to hidden.</param>
/// <param name="Retired">Orphaned stops whose position closed during the outage — retired, never re-armed.</param>
/// <param name="StillOrphaned">Stops that could not be re-validated (venue unreachable / not this process's) — retried later.</param>
public sealed record RearmOutcome(int Rearmed, int Retired, int StillOrphaned);
