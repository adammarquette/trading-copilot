using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// Reconciles an account's positions against <b>venue truth</b> across the settlement boundary (R-13, R-19,
/// ADR-0013, gh#193): positions come from the venue, never local state (there is no local position store, which
/// is why venue-as-truth is the tractable design). The result is tagged with its
/// <see cref="PositionMarkBasis"/>, so a settlement re-mark is never read as live movement and an unreachable
/// venue is a <b>declared-unknown</b> state rather than a stale live-looking number.
/// </summary>
/// <remarks>
/// Request-scoped (the operator reconciling their own account), so the R-20 filter applies — an account not owned
/// by the caller is simply not found. Firing across a reconnect is a later increment (gh#221 owns restart
/// rehydration); this is the reconcile the caller invokes. The account-level basis flags the settlement window if
/// <b>any</b> configured instrument is in it; per-position precision is a noted refinement.
/// </remarks>
public sealed class PositionReconciliationService
{
    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IOptions<ProjectXConnectionOptions> _projectXOptions;
    private readonly IOptions<FlattenOptions> _flattenOptions;
    private readonly ILogger<PositionReconciliationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The scoped database (R-20 applies).</param>
    /// <param name="venueFactory">Builds the venue for the account's connection.</param>
    /// <param name="projectXOptions">The process's credential-key configuration (ADR-0015 guard).</param>
    /// <param name="flattenOptions">The per-instrument session schedules the settlement window derives from.</param>
    /// <param name="logger">The logger.</param>
    public PositionReconciliationService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<FlattenOptions> flattenOptions,
        ILogger<PositionReconciliationService> logger)
    {
        _database = database;
        _venueFactory = venueFactory;
        _projectXOptions = projectXOptions;
        _flattenOptions = flattenOptions;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles the account's positions from venue truth at <paramref name="now"/>.
    /// </summary>
    /// <param name="accountId">The account to reconcile (R-20-scoped to the caller).</param>
    /// <param name="now">The current instant — the settlement window is judged against it in market time.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The reconciliation, or <see langword="null"/> if the account is not found / not owned by the caller (the
    /// endpoint maps that to 404). A venue that cannot be reached yields a <see cref="PositionMarkBasis.Unknown"/>
    /// reconciliation with no positions — declared-unknown, never a stale live-looking view.
    /// </returns>
    public async Task<PositionReconciliation?> ReconcileAsync(
        Guid accountId,
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
            // No connection, or one this process does not hold credentials for (ADR-0015): truth is unobtainable
            // -> declared-unknown, not a stale local view.
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

            IReadOnlyList<PositionSnapshot> positions = await venue.GetPositionsAsync(venueAccount.Id, cancellationToken);
            PositionMarkBasis basis = BasisNow(now, venueReachable: true);
            return new PositionReconciliation(basis, positions);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Venue unreachable across the reconcile: fail-safe to declared-unknown rather than surface stale state.
            _logger.LogWarning(error, "Position reconcile for account {AccountId} could not reach venue truth.", accountId);
            return Unknown(now);
        }
    }

    private PositionReconciliation Unknown(DateTimeOffset now) =>
        new(BasisNow(now, venueReachable: false), []);

    /// <summary>
    /// The account-level mark basis: the most-notable across configured session schedules — the settlement window
    /// wins where any instrument is in it, so a carried position is never read as live movement (R-13).
    /// </summary>
    private PositionMarkBasis BasisNow(DateTimeOffset now, bool venueReachable)
    {
        return _flattenOptions.Value.ToSchedules()
            .Select(schedule => MarketSession.MarkBasisAt(schedule, now, venueReachable))
            .DefaultIfEmpty(venueReachable ? PositionMarkBasis.Live : PositionMarkBasis.Unknown)
            .Max();
    }
}

/// <summary>
/// An account's positions from venue truth, and what their mark rests on (gh#193). When
/// <see cref="Basis"/> is <see cref="PositionMarkBasis.Unknown"/>, <see cref="Positions"/> is empty — the state
/// is declared unknown, not shown as a stale live view.
/// </summary>
/// <param name="Basis">Whether the mark is live, a settlement re-mark, or unknown.</param>
/// <param name="Positions">The venue-reported positions (empty when the basis is unknown).</param>
public sealed record PositionReconciliation(PositionMarkBasis Basis, IReadOnlyList<PositionSnapshot> Positions);
