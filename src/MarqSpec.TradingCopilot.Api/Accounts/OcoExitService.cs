using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Accounts;

/// <summary>
/// App-level OCO-cancel-on-exit (R-11, ADR-0007, gh#183): when a position goes <b>flat</b>, retire whatever
/// protection was standing for it. A dangling safety stop is not merely untidy — it is a <b>live resting order at
/// the exchange with no position behind it</b>, which on the next fill opens a position the operator never asked
/// for. The trigger is the account-event seam's <see cref="PositionEvent"/> (gh#219), so <b>every</b> exit route —
/// a manual flatten, the promoted actual stop firing, auto-flatten, the kill switch's flatten-all — reaches here
/// the same way, because each ends in a venue position update.
/// </summary>
/// <remarks>
/// <para>
/// The catastrophic failure mode is cancelling protection while the position is still open. It cannot happen here:
/// the pass acts <b>only</b> when the venue reports the contract flat (<c>NetQuantity == 0</c>); a partial fill
/// leaves the net non-zero and is a no-op, and a flat contract has, by definition, no open position to leave
/// unprotected (R-11 acceptance).
/// </para>
/// <para>
/// It retires the <b>synthetic</b> stop plans (a <see cref="StopStaging.Hidden"/> / <see cref="StopStaging.Native"/>
/// / <see cref="StopStaging.Orphaned"/> record → <see cref="StopStaging.Retired"/>, terminal) and cancels the
/// <b>native</b> legs resting at the venue — the safety-stop bracket, a promoted actual stop, a dangling
/// take-profit. Those legs are found by <see cref="OcoExitSelection.LegsToCancel"/>: a venue-resting order on the
/// flat contract that is <b>not</b> one of the operator's journaled entries. Idempotent by construction — a
/// replayed exit finds the plans already <see cref="StopStaging.Retired"/> and the legs already gone; a cancel the
/// venue rejects (already gone / already filled, e.g. the venue's own OCO won the race) is benign and never
/// retried. Owner-scoped throughout (R-20): the account is resolved to its owner, all reads/writes run in that
/// owner's context, and the legs are cancelled through that owner's own venue connection.
/// </para>
/// </remarks>
public sealed class OcoExitService
{
    private readonly TradingCopilotDbContext _discovery;
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IAuditLog _auditLog;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly ILogger<OcoExitService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used only to resolve the owning account (across owners).</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the work.</param>
    /// <param name="venueFactory">Builds the owner's venue from its connection's conventions.</param>
    /// <param name="auditLog">The immutable audit trail — each retired plan is recorded (gh#220), a secondary write.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="logger">The logger.</param>
    public OcoExitService(
        TradingCopilotDbContext discovery,
        DbContextOptions<TradingCopilotDbContext> options,
        IProjectXVenueFactory venueFactory,
        IAuditLog auditLog,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        ILogger<OcoExitService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);

        _discovery = discovery;
        _options = options;
        _venueFactory = venueFactory;
        _auditLog = auditLog;
        _projectX = projectXOptions.Value;
        _logger = logger;
    }

    /// <summary>Retires the protection left behind by a position that has just gone flat.</summary>
    /// <param name="exit">The position-update event — acted on only when it reports the contract flat.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A task that completes when the exit has been processed.</returns>
    public async Task ProcessExitAsync(PositionEvent exit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exit);

        // The one guard that makes the catastrophic case impossible: only a FLAT position retires protection. A
        // partial fill leaves the net non-zero -- the remaining position is still live and must stay protected.
        if (exit.NetQuantity != 0)
        {
            return;
        }

        ExitAccount? account = await ResolveAccountAsync(exit.Account, cancellationToken);
        if (account is null)
        {
            _logger.LogDebug("Position-exit event for unrecognized account {Account}; ignored.", exit.Account);
            return;
        }

        // Per-owner context: the Order / StopPlan reads and writes are R-20-scoped, so this exit can only ever
        // touch its own operator's protection.
        await using TradingCopilotDbContext database = new(_options, new OwnerUser(account.UserId));

        List<Order> orders = await database.Orders
            .Where(order => order.AccountId == account.AccountId && order.Instrument == exit.Contract.Key)
            .ToListAsync(cancellationToken);
        if (orders.Count == 0)
        {
            return; // nothing of ours journaled on this contract
        }

        // Re-check against LIVE venue truth before touching ANY protection (the stale-event re-open race): a flat
        // event can be processed after a NEW position has re-opened on the same contract -- a fast re-entry /
        // stop-and-reverse, or a replayed event after a reconnect (the event's flatness is emit-time, not
        // processing-time truth). Acting then would strip the new position's protection, the exact catastrophe.
        FirmConventions conventions = await database.ConventionsForConnectionAsync(account.ConnectionId, cancellationToken);
        ITradingVenue venue = _venueFactory.Create(conventions);
        if (!await ConfirmStillFlatAsync(venue, exit, cancellationToken))
        {
            return; // still holding a position, or could not confirm -- leave every leg standing
        }

        await RetireStopPlansAsync(database, orders, exit, cancellationToken);
        await CancelNativeLegsAsync(venue, orders, exit, cancellationToken);
    }

    private async Task<bool> ConfirmStillFlatAsync(
        ITradingVenue venue, PositionEvent exit, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<PositionSnapshot> positions = await venue.GetPositionsAsync(exit.Account, cancellationToken);
            PositionSnapshot? current = positions.FirstOrDefault(position => position.Contract == exit.Contract);
            if (current is { IsFlat: false })
            {
                // A position re-opened on the contract since the exit event -- its protection must stand.
                _logger.LogWarning(
                    "OCO-exit: {Contract} on account {Account} is no longer flat ({Net}) at processing time -- a "
                    + "position re-opened since the exit event; leaving its protection in place.",
                    exit.Contract, exit.Account, current.NetQuantity);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Cannot confirm flatness (venue unreachable / cannot read positions) -- do NOT retire or cancel on an
            // unconfirmed exit; leave protection standing and surface it loudly (synthetic_risk) to investigate.
            _logger.LogError(
                error,
                "OCO-exit: could not reach {Venue} to confirm {Contract} is flat (synthetic_risk); leaving all "
                + "protection in place. Investigate.",
                venue.Id, exit.Contract);
            return false;
        }
    }

    private async Task RetireStopPlansAsync(
        TradingCopilotDbContext database, List<Order> orders, PositionEvent exit, CancellationToken cancellationToken)
    {
        List<Guid> orderIds = [.. orders.Select(order => order.Id)];

        // The synthetic / working stop plans for the now-confirmed-flat position -- retire them terminally
        // (`Retired`) so the promotion watcher stops acting on them going forward. This retire is deliberately a
        // plain write with no concurrency token: a promotion racing it is fenced on the PROMOTION side (the gh#183
        // follow-up) -- StopPromotionService re-confirms the position is open before placing, and after placing it
        // re-reads this staging and, finding it no longer Hidden, cancels its own just-placed native stop instead of
        // clobbering this `Retired`. Keeping the retire token-free means it can never itself lose a race and skip
        // the CancelNativeLegs cleanup below.
        List<StopPlanRecord> plans = await database.StopPlans
            .Where(plan => orderIds.Contains(plan.OrderId)
                && (plan.Staging == StopStaging.Hidden
                    || plan.Staging == StopStaging.Native
                    || plan.Staging == StopStaging.Orphaned))
            .ToListAsync(cancellationToken);

        if (plans.Count == 0)
        {
            return;
        }

        List<AuditRecord> auditRows = [];
        foreach (StopPlanRecord plan in plans)
        {
            StopStaging before = plan.Staging;
            plan.Staging = StopStaging.Retired;
            auditRows.Add(AuditForExit(plan, before, exit));
        }

        await database.SaveChangesAsync(cancellationToken); // the retire -- the safety action, committed first
        _logger.LogInformation(
            "OCO-exit: {Count} stop plan(s) retired on {Contract} for account {Account} after it went flat.",
            plans.Count, exit.Contract, exit.Account);

        // Record the retirement to the immutable audit trail (gh#220) -- a SECONDARY write after the retire
        // committed, tolerating any failure so the audit can never be the reason the safety action did not complete.
        await AuditSafelyAsync(auditRows, cancellationToken);
    }

    // A retired-on-exit audit entry, owned by the affected stop's operator (R-20) -- stamped explicitly because
    // this runs as background plumbing with no ambient user. Not a synthetic_risk event: the position is flat, so
    // no live exposure was resting on platform-held protection when the plan was retired (unlike the orphan guard).
    private static AuditRecord AuditForExit(StopPlanRecord plan, StopStaging before, PositionEvent exit) => new()
    {
        UserId = plan.UserId,
        Action = AuditAction.PositionExit,
        Placement = before == StopStaging.Native ? AuditPlacement.Native : AuditPlacement.Synthetic,
        SyntheticRisk = false,
        StopPlanId = plan.Id,
        Before = before.ToString(),
        After = StopStaging.Retired.ToString(),
        Detail = $"Stop plan retired on position exit ({exit.Contract} flat).",
        RecordedAt = DateTimeOffset.UtcNow, // a record-keeping timestamp, not a decision input
    };

    private async Task AuditSafelyAsync(IReadOnlyCollection<AuditRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        try
        {
            await _auditLog.WriteAsync(records, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error, "OCO-exit: audit write failed for {Count} retirement(s); the retire still completed.",
                records.Count);
        }
    }

    private async Task CancelNativeLegsAsync(
        ITradingVenue venue,
        List<Order> orders,
        PositionEvent exit,
        CancellationToken cancellationToken)
    {
        // The operator's own journaled resting entries -- never cancelled. Every protective leg the venue spawned
        // (safety bracket, promoted stop, take-profit) is NOT a journaled order, so it is not in this set. A
        // partially-filled entry still has a live resting remainder at the venue, so it counts as an entry too --
        // the same "live at venue" states the fill consumer uses (working or partially filled).
        HashSet<string> journaledEntries =
        [
            .. orders
                .Where(order => order.Status is OrderStatus.Working or OrderStatus.PartiallyFilled
                    && order.VenueOrderKey is not null)
                .Select(order => order.VenueOrderKey!),
        ];

        IReadOnlyList<WorkingOrder> resting;
        try
        {
            resting = await venue.GetWorkingOrdersAsync(exit.Account, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Any failure to enumerate the venue's working orders -- the fail-closed NotSupported default, or a
            // transient fault (timeout / 5xx) -- means a native leg cannot be reached. The plans are retired above,
            // so surface it just as loudly (synthetic_risk) rather than silently leave a possibly-dangling leg.
            _logger.LogError(
                error,
                "OCO-exit: could not list {Venue}'s working orders; native protective legs on {Contract} were not "
                + "auto-cancelled (synthetic_risk). Cancel them manually.",
                venue.Id, exit.Contract);
            return;
        }

        IReadOnlyList<WorkingOrder> legs = OcoExitSelection.LegsToCancel(resting, exit.Contract, journaledEntries);

        int cancelled = 0;
        foreach (WorkingOrder leg in legs)
        {
            try
            {
                await venue.CancelOrderAsync(exit.Account, leg.VenueOrderKey, cancellationToken);
                cancelled++;
                _logger.LogInformation(
                    "OCO-exit: cancelled dangling protective leg {Leg} on {Contract} for account {Account}.",
                    leg.VenueOrderKey, exit.Contract, exit.Account);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // Benign and common: the leg was already gone (filled, or cancelled by the venue's own OCO). One
                // attempt per leg per event -- never retried, so no storm. Logged so a genuine failure is visible.
                _logger.LogWarning(
                    error, "OCO-exit: could not cancel leg {Leg} on {Contract} (likely already gone); continuing.",
                    leg.VenueOrderKey, exit.Contract);
            }
        }

        if (cancelled > 0)
        {
            _logger.LogInformation(
                "OCO-exit: {Count} dangling protective leg(s) cancelled on {Contract} for account {Account}.",
                cancelled, exit.Contract, exit.Account);
        }
    }

    private async Task<ExitAccount?> ResolveAccountAsync(VenueAccountId account, CancellationToken cancellationToken)
    {
        var match = await _discovery.Accounts
            .IgnoreQueryFilters()
            .Join(
                _discovery.Connections.IgnoreQueryFilters(),
                owned => owned.ConnectionId,
                connection => connection.Id,
                (owned, connection) => new { Account = owned, connection.CredentialKey })
            .Where(pair => pair.Account.VenueAccountKey == account.Key && pair.CredentialKey == _projectX.CredentialKey)
            .Select(pair => new { pair.Account.Id, pair.Account.UserId, pair.Account.ConnectionId })
            .FirstOrDefaultAsync(cancellationToken);

        return match is null ? null : new ExitAccount(match.UserId, match.Id, match.ConnectionId);
    }

    /// <summary>The owner, our account id, and its connection — everything the exit needs, all R-20 identity.</summary>
    private sealed record ExitAccount(Guid UserId, Guid AccountId, Guid ConnectionId);

    /// <summary>The owning operator, so the per-owner context is R-20-scoped and never touches another's rows.</summary>
    private sealed record OwnerUser(Guid UserId) : ICurrentUser;
}
