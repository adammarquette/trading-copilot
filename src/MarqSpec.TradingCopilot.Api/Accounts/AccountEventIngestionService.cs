using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
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
    private readonly ProjectXConnectionOptions _projectX;
    private readonly ILogger<AccountEventIngestionService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used only to discover the owning account (across owners).</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the writes.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="logger">The logger.</param>
    public AccountEventIngestionService(
        TradingCopilotDbContext discovery,
        DbContextOptions<TradingCopilotDbContext> options,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        ILogger<AccountEventIngestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);

        _discovery = discovery;
        _options = options;
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
        await database.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Order {Order} moved to {Status} from venue truth.", order.Id, status);
        return true;
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
