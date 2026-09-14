using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// Reads an account's <b>resting orders — including the attached protective bracket and its size</b> — from venue
/// truth (R-1, R-13, R-17, ADR-0013, gh#381). The resting-orders sibling of
/// <see cref="PositionReconciliationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The app had a venue-truth read of <i>positions</i> and none of <i>resting orders</i>, so nothing could show
/// whether protection was actually standing at the exchange. The gh#269 / gh#293 pre-live bracket gates had to
/// reach <i>around</i> the app to the ProjectX gateway to witness the protective leg — that bypass is the gap this
/// closes.
/// </para>
/// <para>
/// <b>Read-only.</b> Nothing here is execution-shaped: no writes, no cancels, no gate involvement. It reports what
/// the venue says is resting, and that is all.
/// </para>
/// <para>
/// <b>An unreachable venue is declared-unknown, never an empty book.</b> That distinction is the whole value of
/// the read: on a question about whether protection is standing, "we could not ask" and "nothing is there" are
/// opposite answers, and returning an empty list for the first would be the more dangerous of the two.
/// </para>
/// <para>
/// Request-scoped, so the R-20 filter applies — an account not owned by the caller is simply not found.
/// </para>
/// </remarks>
public sealed class WorkingOrderReconciliationService
{
    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IOptions<ProjectXConnectionOptions> _projectXOptions;
    private readonly IOptions<FlattenOptions> _flattenOptions;
    private readonly ILogger<WorkingOrderReconciliationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The scoped database (R-20 applies).</param>
    /// <param name="venueFactory">Builds the venue for the account's connection.</param>
    /// <param name="projectXOptions">The process's credential-key configuration (ADR-0015 guard).</param>
    /// <param name="flattenOptions">The per-instrument session schedules the basis derives from.</param>
    /// <param name="logger">The logger.</param>
    public WorkingOrderReconciliationService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<FlattenOptions> flattenOptions,
        ILogger<WorkingOrderReconciliationService> logger)
    {
        _database = database;
        _venueFactory = venueFactory;
        _projectXOptions = projectXOptions;
        _flattenOptions = flattenOptions;
        _logger = logger;
    }

    /// <summary>Reads the account's resting orders from venue truth at <paramref name="now"/>.</summary>
    /// <param name="accountId">The account to read (R-20-scoped to the caller).</param>
    /// <param name="now">The current instant — the session basis is judged against it in market time.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The read, or <see langword="null"/> if the account is not found / not owned by the caller (the endpoint
    /// maps that to 404). A venue that cannot be reached yields <see cref="PositionMarkBasis.Unknown"/> with no
    /// orders — declared-unknown, never an empty book.
    /// </returns>
    public Task<WorkingOrderReconciliation?> ReconcileAsync(
        Guid accountId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => ReconcileAsync(accountId, instrument: null, now, cancellationToken);

    /// <summary>
    /// Reads the account's resting orders from venue truth, scoped to a single <paramref name="instrument"/> when one
    /// is given (gh#772, for the chart's per-instrument execution overlay gh#727).
    /// </summary>
    /// <param name="accountId">The account to read (R-20-scoped to the caller).</param>
    /// <param name="instrument">
    /// When set, only orders resting on <i>that</i> instrument's venue contract are returned — the venue resolves the
    /// symbol to its contract, the same resolution the order path uses. <see langword="null"/> returns every contract,
    /// unchanged. A well-formed symbol that resolves to <b>no</b> contract is a client mistake, so it raises
    /// <see cref="InstrumentNotResolvableException"/> (the endpoint maps it to a 400) — never an empty "nothing resting
    /// on this instrument", and never <see cref="PositionMarkBasis.Unknown"/>, which stays reserved for an unreachable
    /// venue.
    /// </param>
    /// <param name="now">The current instant — the session basis is judged against it in market time.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The read, or <see langword="null"/> if the account is not found / not owned by the caller (the endpoint maps
    /// that to 404). A venue that cannot be reached yields <see cref="PositionMarkBasis.Unknown"/> with no orders.
    /// </returns>
    public async Task<WorkingOrderReconciliation?> ReconcileAsync(
        Guid accountId,
        InstrumentId? instrument,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Account? account = await _database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return null; // not found / not owned (R-20) -> 404
        }

        Connection? connection = await _database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == account.ConnectionId, cancellationToken);
        if (connection is null
            || !string.Equals(connection.CredentialKey, _projectXOptions.Value.CredentialKey, StringComparison.Ordinal))
        {
            // No connection, or one this process does not hold credentials for (ADR-0015): truth is unobtainable,
            // and the venue is not even asked.
            return Unknown(now);
        }

        try
        {
            FirmConventions conventions = await _database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
            ITradingVenue venue = _venueFactory.Create(conventions);

            IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);
            VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
            if (venueAccount is null)
            {
                return Unknown(now); // the venue no longer reports the account -> truth unobtainable
            }

            IReadOnlyList<WorkingOrder> orders = await venue.GetWorkingOrdersAsync(venueAccount.Id, cancellationToken);
            if (instrument is { } wanted)
            {
                // The read reached the venue (roster + book above), so a resolve failure here is the venue saying "no
                // such contract" -- a CLIENT mistake (a typo'd / delisted symbol), which surfaces as a 400. It must not
                // become an empty "nothing resting on this instrument", and must not become Unknown, which is reserved
                // (ADR-0013) for "the venue could not be reached". A transport fault mid-resolve is NOT the venue's
                // not-found type, so it falls through to the broad catch below and correctly stays Unknown.
                ResolvedContract resolved;
                try
                {
                    resolved = await venue.ResolveContractAsync(wanted, cancellationToken);
                }
                catch (ProjectXVenueException notFound)
                {
                    throw new InstrumentNotResolvableException(wanted, notFound);
                }

                orders = [.. orders.Where(order => order.Contract == resolved.Contract)];
            }

            return new WorkingOrderReconciliation(BasisNow(now, venueReachable: true), orders);
        }
        catch (Exception error) when (error is not OperationCanceledException and not InstrumentNotResolvableException)
        {
            // Includes NotSupportedException from the R-17 fail-closed default: a venue that cannot list working
            // orders tells us nothing about protection, which is unknown -- not "none resting".
            _logger.LogWarning(
                error, "Resting-order read for account {AccountId} could not reach venue truth.", accountId);
            return Unknown(now);
        }
    }

    private WorkingOrderReconciliation Unknown(DateTimeOffset now) =>
        new(BasisNow(now, venueReachable: false), []);

    /// <summary>
    /// The account-level basis, shared with the positions read so a caller has <b>one</b> vocabulary for how far
    /// to trust a venue-truth payload.
    /// </summary>
    /// <remarks>
    /// For resting orders the basis qualifies <i>the read</i>, not a price mark: <c>Settlement</c> means the view
    /// was taken inside the maintenance window, when the venue's own book may be mid-transition — not that these
    /// orders are re-marked, which would be meaningless for an order.
    /// </remarks>
    private PositionMarkBasis BasisNow(DateTimeOffset now, bool venueReachable)
    {
        return _flattenOptions.Value.ToSchedules()
            .Select(schedule => MarketSession.MarkBasisAt(schedule, now, venueReachable))
            .DefaultIfEmpty(venueReachable ? PositionMarkBasis.Live : PositionMarkBasis.Unknown)
            .Max();
    }
}

/// <summary>
/// An account's resting orders from venue truth, and how far the read can be trusted (gh#381). When
/// <see cref="Basis"/> is <see cref="PositionMarkBasis.Unknown"/>, <see cref="Orders"/> is empty — the state is
/// declared unknown rather than presented as an empty book.
/// </summary>
/// <param name="Basis">Whether the read is live, taken inside the settlement window, or unknown.</param>
/// <param name="Orders">The venue-reported resting orders (empty when the basis is unknown).</param>
public sealed record WorkingOrderReconciliation(PositionMarkBasis Basis, IReadOnlyList<WorkingOrder> Orders);
