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
        await Task.Yield();
        throw new NotImplementedException("gh#928: the verified sized partial close is not implemented yet.");
    }
}
