using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>What a reduce attempt actually achieved, verified against venue truth rather than inferred (gh#928).</summary>
public enum PositionReduceOutcome
{
    /// <summary>Never a valid outcome — the fail-closed zero.</summary>
    Unknown = 0,

    /// <summary>
    /// The venue reports the position smaller by <b>exactly the requested amount</b>, same side — the reduction is
    /// verified against what was asked, not merely against direction.
    /// </summary>
    Reduced = 1,

    /// <summary>
    /// The venue did <b>not</b> report the exact requested reduction — <b>not done</b>. Covers the original size
    /// still standing, <b>less</b> off than asked (a partial execution), <b>more</b> off than asked (a protective
    /// stop or a concurrent exit fired, up to and including all the way to flat), and a <b>side flip</b> (a reversal
    /// is never a reduction, however small its magnitude). Read from venue truth after the attempt.
    /// </summary>
    NotReduced = 2,

    /// <summary>The venue could not be reached or does not serve this account. <b>Never read as reduced.</b></summary>
    Unreachable = 3,

    /// <summary>
    /// The requested size is not strictly less than what is open, so it is not a partial. A reduce must <b>leave
    /// part of the position on</b>: taking the whole thing off is a full close (which the exit path owns, together
    /// with its OCO-cancel), and taking more than is open is a sizing mistake. Both are refused here, before the
    /// venue is touched — a reduce must never silently become a flatten.
    /// </summary>
    ExceedsPosition = 4,
}

/// <summary>The result of an operator-initiated reduce (gh#928).</summary>
/// <param name="Outcome">What was achieved, verified against venue truth rather than inferred from the call.</param>
/// <param name="NetQuantity">
/// The signed exposure the venue reports <b>after</b> the attempt — or the current size, when the request was
/// refused before the venue was touched. <b><see langword="null"/> when the venue could not be reached</b>: an
/// exposure we could not read is unknown, and reporting it as <c>0</c> would fabricate a flat out of an outage,
/// which is the failure mode gh#929 exists to prevent. It is never <see langword="null"/> for any outcome the
/// venue actually answered.
/// </param>
public sealed record PositionReduceResult(PositionReduceOutcome Outcome, int? NetQuantity);

/// <summary>
/// The operator's per-position <b>reduce</b> (gh#928, R-11) — the blotter's "take part of this position off"
/// control, a sized partial close toward flat.
/// </summary>
/// <remarks>
/// <para>
/// <b>A reduce is a close, not a send.</b> It rides <see cref="IOrderExecutor.ReducePositionAsync"/> — the venue's
/// native sized partial close — and deliberately <b>not</b> the order-placement ladder. An opposing order would be
/// a send, gated by R-5 / R-16 / R-12, and a gate refusing a risk-<i>lowering</i> action would be the wrong answer.
/// As a reducing action it is, like the full exit (gh#656), <b>not</b> gated on the kill switch: engaging the kill
/// switch stops new risk; closing what is already open must keep working.
/// </para>
/// <para>
/// <b>Strictly partial, on purpose.</b> The requested size must be strictly less than what is open. Reducing by the
/// whole position is a full close — which belongs to the exit path, because a full close cancels the protective OCO
/// legs (gh#183, gh#656) and a sized partial close does not. Letting a reduce equal the position would route a
/// flatten through a path that leaves a dangling protective order, so it is refused
/// (<see cref="PositionReduceOutcome.ExceedsPosition"/>) before the venue is touched.
/// </para>
/// <para>
/// <b>Only a verified reduction is success.</b> The outcome is read from what the venue reports <i>after</i> the
/// attempt (ADR-0013): the position smaller by <b>exactly the requested amount</b>, same side, is
/// <see cref="PositionReduceOutcome.Reduced"/>; anything else — the original size still standing, <b>less</b> off
/// than asked (a partial execution), <b>more</b> off (a stop or a concurrent exit fired, up to flat), or a side
/// flip — is <see cref="PositionReduceOutcome.NotReduced"/>; any fault is
/// <see cref="PositionReduceOutcome.Unreachable"/>, never reduced. Reporting anything but the exact reduction as
/// done would leave the operator to notice from the net quantity that they got something other than what they
/// asked for.
/// </para>
/// <para>
/// <b>Known limitation — the protective bracket is left as it is, and the venue's behaviour is unverified.</b> The
/// copilot's attached safety bracket carries no size field (<c>{ticks, type}</c>, sized to the realized fill on
/// attach; gh#293), and this reduce does not resize it. Whether ProjectX auto-reduces a position-linked bracket on
/// a partial close <b>has not been observed</b> — gh#1012 scaffolded that verification but it has never been run —
/// so a resting stop may still cover the <i>original</i> quantity and, on trigger, overshoot the remainder into an
/// opposing position. Which of auto-resize / refuse-the-desyncing-reduce / warn-loudly answers that is a policy
/// gh#928 §⚠️ reserves for the operator, and none of them is implemented here. <b>This path is not to be trusted on
/// a funded account until that gate produces a finding.</b>
/// </para>
/// </remarks>
public sealed class PositionReduceService
{
    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IOptions<ProjectXConnectionOptions> _projectXOptions;
    private readonly ILogger<PositionReduceService> _logger;

    /// <summary>Creates the reduce service.</summary>
    /// <param name="database">The scoped database (R-20 applies).</param>
    /// <param name="venueFactory">Builds a venue for the connection's firm conventions.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="logger">The logger.</param>
    public PositionReduceService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        ILogger<PositionReduceService> logger)
    {
        _database = database;
        _venueFactory = venueFactory;
        _projectXOptions = projectXOptions;
        _logger = logger;
    }

    /// <summary>
    /// Reduces one instrument's position on an account by <paramref name="quantity"/> contracts, at market.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="instrument">The instrument to reduce.</param>
    /// <param name="quantity">The positive number of contracts to take off (validated at the endpoint too).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The outcome, or <see langword="null"/> when the account is not found or not the caller's (R-20) — a 404,
    /// the same shape every account-scoped read uses, so this path leaks no existence.
    /// </returns>
    public async Task<PositionReduceResult?> ReduceAsync(
        Guid accountId,
        InstrumentId instrument,
        int quantity,
        CancellationToken cancellationToken)
    {
        // The endpoint rejects a non-positive quantity as a 400; guard defensively so a mis-wired caller can never
        // reach the venue with a meaningless size.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        Account? account = await _database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return null; // not found / not owned (R-20)
        }

        Connection? connection = await _database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == account.ConnectionId, cancellationToken);
        if (connection is null
            || !string.Equals(connection.CredentialKey, _projectXOptions.Value.CredentialKey, StringComparison.Ordinal))
        {
            // One credential set per process (ADR-0015). Reducing on an account this process does not serve would
            // be acting through someone else's venue session.
            return new PositionReduceResult(PositionReduceOutcome.Unreachable, null);
        }

        try
        {
            FirmConventions conventions = await _database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
            ITradingVenue venue = _venueFactory.Create(conventions);

            IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);
            VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
            if (venueAccount is null)
            {
                return new PositionReduceResult(PositionReduceOutcome.Unreachable, null);
            }

            ResolvedContract resolved = await venue.ResolveContractAsync(instrument, cancellationToken);

            // BEFORE: venue truth for this contract. Load-bearing for BOTH the strict-partial guard and the
            // verified-reduction check -- without the starting size neither is possible. Deliberately no local
            // belief and no IsActive-style filter (gh#929): a filtered exposure read fabricates a flat.
            IReadOnlyList<PositionSnapshot> before = await venue.GetPositionsAsync(venueAccount.Id, cancellationToken);
            PositionSnapshot? position = before.FirstOrDefault(candidate => candidate.Contract == resolved.Contract);
            int beforeQuantity = position?.NetQuantity ?? 0;
            int beforeMagnitude = Math.Abs(beforeQuantity);

            // Strict partial: the request must LEAVE part of the position on. quantity >= what is open is either a
            // full close (which belongs to the exit path, with its OCO-cancel) or a sizing mistake -- refused here,
            // before the venue is touched, so a reduce can never silently become a flatten that strands a bracket.
            if (quantity >= beforeMagnitude)
            {
                return new PositionReduceResult(PositionReduceOutcome.ExceedsPosition, beforeQuantity);
            }

            PositionSnapshot after = await venue.ReducePositionAsync(
                venueAccount.Id, resolved.Contract, quantity, cancellationToken);

            // Verified against what was ASKED, not just against direction (gh#928 §⚠️2, settled as exact-delta).
            // Success is the venue reporting the position smaller by EXACTLY `quantity`, same side. Everything else
            // is "not done": a partial execution that closed LESS than asked (more exposure remains than the
            // operator targeted), a close that took off MORE (a protective stop or a concurrent exit fired), a side
            // flip, and no change at all. The operator asked for a specific reduction, so anything other than that
            // is a state they must SEE, never a green "reduced" whose net quantity they have to notice is wrong.
            // (The strict-partial guard above makes the target `beforeMagnitude - quantity` at least 1, so a
            // same-side match can never be flat: a close all the way to flat is an over-execution, reported
            // not-done -- which is also what surfaces the dangling-bracket-over-a-flat race gh#1012 describes.)
            bool reducedByExactlyRequested =
                Math.Sign(after.NetQuantity) == Math.Sign(beforeQuantity)
                && Math.Abs(after.NetQuantity) == beforeMagnitude - quantity;

            return reducedByExactlyRequested
                ? new PositionReduceResult(PositionReduceOutcome.Reduced, after.NetQuantity)
                : new PositionReduceResult(PositionReduceOutcome.NotReduced, after.NetQuantity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER went away -- abort, do not manufacture a business outcome. Narrow on purpose: a venue
            // timeout also surfaces as an OperationCanceledException, but carrying HttpClient's own internal token,
            // so it falls through to the fault handler below and reports Unreachable rather than escaping as an
            // aborted request.
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Operator reduce of {Instrument} on account {Account} could not be completed — the position may be "
                + "unchanged, or partly reduced (gh#928).",
                instrument,
                accountId);
            return new PositionReduceResult(PositionReduceOutcome.Unreachable, null);
        }
    }
}
