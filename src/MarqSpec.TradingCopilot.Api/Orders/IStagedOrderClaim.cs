using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Execution;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Orders;

/// <summary>
/// Decides, atomically, which of several concurrent takes owns a staged ticket (gh#530).
/// </summary>
/// <remarks>
/// <para>
/// The take path spans four venue round-trips between reading the row and writing the result — hundreds of
/// milliseconds, which a double-click or two ADR-0006 pop-out windows on one ticket will cross. Two requests each
/// read <c>Staged</c>, each passed the status check against their own change tracker, and <b>both transmitted</b>.
/// Then both wrote the same row, so one live venue order ended up recorded on no <c>Order</c> row: invisible to
/// cancel, to the kill-switch sweep and to the orphan guard, because every one of them resolves by row id.
/// </para>
/// <para>
/// <b>Why this is a seam rather than a line of code in the endpoint.</b> The decision has to be evaluated by the
/// database — two requests share no change tracker, so nothing in process can arbitrate. That means
/// <c>ExecuteUpdate</c> or raw SQL, and <b>neither is supported by the EF in-memory provider</b>: putting it
/// inline made the take endpoint unrunnable on the unit tier and took six existing take guards down with it. The
/// seam keeps those guards running against a fake, and puts the real compare-and-swap where it can actually be
/// proven — the container-backed Postgres tier.
/// </para>
/// </remarks>
public interface IStagedOrderClaim
{
    /// <summary>Claims a staged ticket for this request, moving it to <see cref="OrderStatus.Taking"/>.</summary>
    /// <param name="orderId">The staged order.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this caller won the claim. <see langword="false"/> means someone else holds it —
    /// and the caller must refuse <b>without touching the venue</b>, which is the entire point.
    /// </returns>
    Task<bool> TryClaimAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Hands the claim back, returning the ticket to <see cref="OrderStatus.Staged"/>.</summary>
    /// <remarks>
    /// Called on every path that does not reach the venue, so a refused take leaves the ticket exactly as takeable
    /// as it was. Conditioned on <see cref="OrderStatus.Taking"/>, so it can never drag a row that has since become
    /// <see cref="OrderStatus.Working"/> back into staging.
    /// </remarks>
    /// <param name="orderId">The order whose claim is being released.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <summary>The database-evaluated claim — a conditional UPDATE, which is the only thing two requests both see.</summary>
public sealed class StagedOrderClaim : IStagedOrderClaim
{
    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the claim over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    public StagedOrderClaim(TradingCopilotDbContext database) => _database = database;

    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(Guid orderId, CancellationToken cancellationToken) =>
        await _database.Orders
            .Where(candidate => candidate.Id == orderId && candidate.Status == OrderStatus.Staged)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Status, OrderStatus.Taking),
                cancellationToken) == 1;

    /// <inheritdoc />
    public Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken) =>
        _database.Orders
            .Where(candidate => candidate.Id == orderId && candidate.Status == OrderStatus.Taking)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Status, OrderStatus.Staged),
                cancellationToken);
}
