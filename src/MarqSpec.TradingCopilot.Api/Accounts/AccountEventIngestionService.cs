using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Realtime;
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
/// The account-event consumer's core (R-17, R-11, gh#219): it turns the venue's realtime truth into journal state
/// — <b>a fill event writes a <see cref="Fill"/> row</b> (the entity's first producer) and advances its order to
/// <see cref="OrderStatus.PartiallyFilled"/> / <see cref="OrderStatus.Filled"/>; <b>an order-state event</b> moves
/// a working order to <see cref="OrderStatus.Rejected"/> / <see cref="OrderStatus.Cancelled"/>. Before this, an
/// order went <c>Staged → Working</c> and stopped, blind to what the venue did next.
/// </summary>
/// <remarks>
/// <para>
/// Background plumbing with no request user: it <b>discovers</b> the owning account across owners with
/// <c>IgnoreQueryFilters</c> (the stop-promotion / conditional-firing pattern), confirms the account is one this
/// process's credential set may act on (ADR-0015), then does the work in a DbContext <b>scoped to that owner</b> —
/// so the <see cref="Fill"/> and <see cref="Order"/> writes carry the owner and an event for another operator's
/// account never crosses the R-20 boundary.
/// </para>
/// <para>
/// Idempotency is <b>by construction</b>: a fill is keyed on the venue's own trade handle, and the
/// <c>{ OrderId, VenueFillKey }</c> unique index rejects a replay — the consumer catches that and treats it as
/// already-recorded, never an inspecting <c>SELECT</c>-then-branch. Order status is recomputed from the persisted
/// fill total, so a replayed fill leaves the status correct. An event for an unknown / foreign order is logged and
/// ignored — a stray payload never takes the consumer down.
/// </para>
/// </remarks>
public sealed class AccountEventIngestionService
{
    private readonly TradingCopilotDbContext _discovery;
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly IAuditLog _auditLog;
    private readonly IAccountRealtimeNotifier _notifier;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly ILogger<AccountEventIngestionService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used only to discover the owning account (across owners).</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the writes.</param>
    /// <param name="auditLog">The immutable audit trail — a retired stop plan is recorded (gh#220), a secondary write.</param>
    /// <param name="notifier">Read-side realtime push to the owning operator (gh#683) — best-effort, after the commit.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="logger">The logger.</param>
    public AccountEventIngestionService(
        TradingCopilotDbContext discovery,
        DbContextOptions<TradingCopilotDbContext> options,
        IAuditLog auditLog,
        IAccountRealtimeNotifier notifier,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        ILogger<AccountEventIngestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);

        _discovery = discovery;
        _options = options;
        _auditLog = auditLog;
        _notifier = notifier;
        _projectX = projectXOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// The accounts under this process's one credential set (ADR-0015), for the stream to subscribe. Discovered
    /// across owners — background plumbing bypasses the R-20 filter — but only their venue keys leave here.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The venue-qualified accounts to subscribe.</returns>
    public async Task<IReadOnlyList<VenueAccountId>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        List<string> keys = await _discovery.Accounts
            .IgnoreQueryFilters()
            .Join(
                _discovery.Connections.IgnoreQueryFilters(),
                account => account.ConnectionId,
                connection => connection.Id,
                (account, connection) => new { account.VenueAccountKey, connection.CredentialKey })
            .Where(pair => pair.CredentialKey == _projectX.CredentialKey)
            .Select(pair => pair.VenueAccountKey)
            .Distinct()
            .ToListAsync(cancellationToken);

        return [.. keys.Select(key => VenueAccountId.Create(VenueId.Parse("projectx"), key))];
    }

    /// <summary>Applies one account event to the journal. Unknown / foreign accounts and orders are ignored.</summary>
    /// <param name="accountEvent">The neutral account event.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> when the event changed persisted state.</returns>
    public async Task<bool> ProcessAsync(AccountEvent accountEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountEvent);

        AccountOwner? owner = await ResolveOwnerAsync(accountEvent.Account, cancellationToken);
        if (owner is null)
        {
            // Not one of this process's accounts (foreign or unknown) -- never cross the R-20 boundary. Ignored.
            _logger.LogDebug("Account event for unrecognized account {Account}; ignored.", accountEvent.Account);
            return false;
        }

        // Per-owner context: the Fill / Order writes are R-20-scoped, so they carry the owner and see only its rows.
        await using TradingCopilotDbContext database = new(_options, new OwnerUser(owner.UserId));

        return accountEvent switch
        {
            FillEvent fill => await ProcessFillAsync(database, owner, fill, cancellationToken),
            OrderStateEvent state => await ProcessOrderStateAsync(database, owner, state, cancellationToken),

            // Position truth for reconciliation comes from the venue query path (gh#193); carried, not persisted.
            PositionEvent => false,
            _ => false,
        };
    }

    private async Task<bool> ProcessFillAsync(
        TradingCopilotDbContext database, AccountOwner owner, FillEvent fill, CancellationToken cancellationToken)
    {
        if (fill.Voided)
        {
            // A busted trade is not an execution -- do not record it or count it toward the order's filled size.
            _logger.LogInformation("Voided fill {Fill} on account {Account}; skipped.", fill.VenueFillKey, fill.Account);
            return false;
        }

        Order? order = await FindOrderAsync(database, owner.AccountId, fill.VenueOrderKey, cancellationToken);
        if (order is null)
        {
            _logger.LogWarning(
                "Fill {Fill} for unknown order {Order} on account {Account}; ignored.",
                fill.VenueFillKey, fill.VenueOrderKey, fill.Account);
            return false;
        }

        // The order's filled total BEFORE this fill; adding this fill's size decides the new status. Set the status
        // in the SAME unit of work as the insert so the two commit ATOMICALLY -- one SaveChanges is one transaction
        // (relational and in-memory alike). A partial commit -- the fill saved but the status not -- would strand a
        // filled order at Working, and the replay the unique index dedupes would then never re-advance it.
        int priorFilled = await database.Fills
            .Where(existing => existing.OrderId == order.Id)
            .SumAsync(existing => existing.Size, cancellationToken);

        database.Fills.Add(new Fill
        {
            Id = Guid.NewGuid(),
            UserId = owner.UserId,
            OrderId = order.Id,
            VenueFillKey = fill.VenueFillKey,
            Price = fill.ExecutionPrice.Value,
            Size = fill.Quantity,
            Fees = fill.Fees,
            ExecutedAt = fill.At,
        });

        // Only a live order advances on fills -- never resurrect a terminal one.
        OrderStatus statusBefore = order.Status;
        if (order.Status is OrderStatus.Working or OrderStatus.PartiallyFilled)
        {
            order.Status = priorFilled + fill.Quantity >= order.Size ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken); // the fill and the status advance, atomically
        }
        catch (DbUpdateException)
        {
            // The { OrderId, VenueFillKey } unique index rejected a replay -- idempotent by construction, not by an
            // inspecting branch. The fill insert and the status advance roll back together, so the order keeps the
            // status its first, committed fill already set; a replayed fill never strands it at Working.
            _logger.LogInformation("Fill {Fill} already recorded for order {Order}; idempotent skip.", fill.VenueFillKey, order.Id);
            return false;
        }

        // Read-side pushes to the owning operator (gh#683) -- AFTER the commit, so a push failure can never affect
        // the journal; NotifySafelyAsync swallows all but cancellation. The fill carries the execution; if it also
        // advanced the order's status, emit that too, so realtimeOrderState is the COMPLETE status stream --
        // fill-driven (PartiallyFilled / Filled) and terminal (Cancelled / Rejected) alike (ADR-0021).
        await NotifySafelyAsync(
            token => _notifier.FillRecordedAsync(
                owner.UserId,
                new RealtimeFill(fill.VenueOrderKey, fill.VenueFillKey, fill.ExecutionPrice.Value, fill.Quantity, fill.At),
                token),
            cancellationToken);

        if (order.Status != statusBefore)
        {
            await NotifySafelyAsync(
                token => _notifier.OrderStateChangedAsync(
                    owner.UserId, new RealtimeOrderState(fill.VenueOrderKey, order.Status.ToString(), fill.At), token),
                cancellationToken);
        }

        return true;
    }

    private async Task<bool> ProcessOrderStateAsync(
        TradingCopilotDbContext database, AccountOwner owner, OrderStateEvent state, CancellationToken cancellationToken)
    {
        Order? order = await FindOrderAsync(database, owner.AccountId, state.VenueOrderKey, cancellationToken);
        if (order is null)
        {
            _logger.LogWarning(
                "Order-state event for unknown order {Order} on account {Account}; ignored.",
                state.VenueOrderKey, state.Account);
            return false;
        }

        // Only a live (working) order transitions from the state stream. Filled / partially-filled are driven by
        // fill events (the authoritative record of executed size), so this handles the terminal NON-fill states.
        if (order.Status is not (OrderStatus.Working or OrderStatus.PartiallyFilled))
        {
            return false;
        }

        bool wasWorking = order.Status == OrderStatus.Working;

        OrderStatus? next = state.State switch
        {
            // A rejection of a still-working order is a reject; of a partially-filled one it is a cancel of the
            // remainder (the fills already stand as Fill rows). Either way the order never stays Working (R-11).
            VenueOrderState.Rejected => order.Status == OrderStatus.Working ? OrderStatus.Rejected : OrderStatus.Cancelled,
            VenueOrderState.Cancelled or VenueOrderState.Expired => OrderStatus.Cancelled,
            _ => null,
        };

        if (next is not { } status || order.Status == status)
        {
            return false;
        }

        order.Status = status;

        // A working entry that NEVER filled (wasWorking) leaves no position, so its stop plan protects nothing --
        // retire it here too, else the promotion watcher could promote a native stop for a cancelled / rejected
        // entry (the gh#183 Finding-4 hazard, reachable via this venue-truth reconcile path -- which the operator
        // cancel deliberately delegates to, gh#250). A PARTIALLY-filled order KEEPS its plan: the filled contracts
        // are a live position the plan still protects, retired later when THAT position closes (OCO-exit, gh#183).
        AuditRecord? planAudit = wasWorking
            ? await RetireStopPlanForTerminalOrderAsync(database, order, cancellationToken)
            : null;

        await database.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Order {Order} moved to {Status} from venue truth.", order.Id, status);
        await AuditSafelyAsync(planAudit, cancellationToken);
        await NotifySafelyAsync(
            token => _notifier.OrderStateChangedAsync(
                owner.UserId, new RealtimeOrderState(state.VenueOrderKey, status.ToString(), state.At), token),
            cancellationToken);
        return true;
    }

    private static async Task<AuditRecord?> RetireStopPlanForTerminalOrderAsync(
        TradingCopilotDbContext database, Order order, CancellationToken cancellationToken)
    {
        StopPlanRecord? plan = await database.StopPlans
            .FirstOrDefaultAsync(candidate => candidate.OrderId == order.Id
                && (candidate.Staging == StopStaging.Hidden
                    || candidate.Staging == StopStaging.Native
                    || candidate.Staging == StopStaging.Orphaned), cancellationToken);
        if (plan is null)
        {
            return null;
        }

        StopStaging before = plan.Staging;
        plan.Staging = StopStaging.Retired;
        return new AuditRecord
        {
            UserId = order.UserId,
            Action = AuditAction.OrderCancelled,
            Placement = before == StopStaging.Native ? AuditPlacement.Native : AuditPlacement.Synthetic,
            SyntheticRisk = false, // the entry never filled -- no live position rested on the protection
            StopPlanId = plan.Id,
            Before = before.ToString(),
            After = StopStaging.Retired.ToString(),
            Detail = $"Stop plan retired: order {order.VenueOrderKey} left the book ({order.Status}) without a position.",
            RecordedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task AuditSafelyAsync(AuditRecord? record, CancellationToken cancellationToken)
    {
        if (record is null)
        {
            return;
        }

        try
        {
            await _auditLog.WriteAsync([record], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Audit write failed for a retired stop plan on a terminal order; the retire still completed.");
        }
    }

    private async Task NotifySafelyAsync(Func<CancellationToken, Task> push, CancellationToken cancellationToken)
    {
        try
        {
            await push(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // The journal write already committed; a realtime push is best-effort presentation and must never take
            // the ingestion consumer down or roll anything back (gh#683).
            _logger.LogWarning(error, "Realtime account push failed after a committed journal write; ignored.");
        }
    }

    private static Task<Order?> FindOrderAsync(
        TradingCopilotDbContext database, Guid accountId, string venueOrderKey, CancellationToken cancellationToken) =>
        database.Orders.FirstOrDefaultAsync(
            order => order.AccountId == accountId && order.VenueOrderKey == venueOrderKey, cancellationToken);

    private async Task<AccountOwner?> ResolveOwnerAsync(VenueAccountId account, CancellationToken cancellationToken)
    {
        var match = await _discovery.Accounts
            .IgnoreQueryFilters()
            .Join(
                _discovery.Connections.IgnoreQueryFilters(),
                owned => owned.ConnectionId,
                connection => connection.Id,
                (owned, connection) => new { Account = owned, connection.CredentialKey })
            .Where(pair => pair.Account.VenueAccountKey == account.Key && pair.CredentialKey == _projectX.CredentialKey)
            .Select(pair => new { pair.Account.Id, pair.Account.UserId })
            .FirstOrDefaultAsync(cancellationToken);

        return match is null ? null : new AccountOwner(match.UserId, match.Id);
    }

    /// <summary>The owning operator and our account id for a venue account — the R-20 identity every write carries.</summary>
    private sealed record AccountOwner(Guid UserId, Guid AccountId);

    /// <summary>The owning operator, so the per-owner context is R-20-scoped and every journaled row keeps its owner.</summary>
    private sealed record OwnerUser(Guid UserId) : ICurrentUser;
}
