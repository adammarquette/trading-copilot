using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>What an exit attempt actually achieved (gh#656).</summary>
public enum PositionExitOutcome
{
    /// <summary>Never a valid outcome — the fail-closed zero.</summary>
    Unknown = 0,

    /// <summary>The venue reports the position flat after the close.</summary>
    Flat = 1,

    /// <summary>The close was accepted but the venue still reports exposure — the operator is not done.</summary>
    StillOpen = 2,

    /// <summary>The venue could not be reached or does not serve this account. <b>Never read as closed.</b></summary>
    Unreachable = 3,
}

/// <summary>The result of an operator-initiated exit (gh#656).</summary>
/// <param name="Outcome">What was achieved, verified against venue truth rather than inferred from the call.</param>
/// <param name="NetQuantity">The signed exposure the venue reports <b>after</b> the attempt; 0 when flat.</param>
public sealed record PositionExitResult(PositionExitOutcome Outcome, int NetQuantity);

/// <summary>
/// The operator's per-position exit (gh#656, R-11) — the blotter's "exit this position" control.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reuses the shared close.</b> <see cref="IOrderExecutor.ClosePositionAsync"/> is the same native-first
/// close that auto-flatten (gh#185), the watchdog (gh#187) and the kill switch (gh#189) all drive. A second close
/// path would be a second thing to get wrong on the one action that reduces real exposure, and the two would
/// drift the first time either was fixed.
/// </para>
/// <para>
/// <b>It is a reducing action, so it is deliberately not gated on the kill switch.</b> Engaging the kill switch
/// stops <i>new</i> risk; closing what is already open must keep working, which is exactly the property the
/// safety strip states out loud (gh#657). Gating this would strand an operator with an open position and no
/// control over it.
/// </para>
/// <para>
/// <b>The close returning is not the position being gone.</b> The outcome is read from what the venue reports
/// <i>after</i> the attempt. Reporting success on an unverified close would tell the operator their exposure is
/// gone while it is still live — the worst answer this path can give — so a venue fault resolves to
/// <see cref="PositionExitOutcome.Unreachable"/>, never to closed.
/// </para>
/// </remarks>
public sealed class PositionExitService
{
    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IOptions<ProjectXConnectionOptions> _projectXOptions;
    private readonly ILogger<PositionExitService> _logger;

    /// <summary>Creates the exit service.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="venueFactory">Builds a venue for the connection's firm conventions.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="logger">The logger.</param>
    public PositionExitService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        ILogger<PositionExitService> logger)
    {
        _database = database;
        _venueFactory = venueFactory;
        _projectXOptions = projectXOptions;
        _logger = logger;
    }

    /// <summary>
    /// Closes one instrument's position on an account at market.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="instrument">The instrument to flatten.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The outcome, or <see langword="null"/> when the account is not found or not the caller's (R-20) — a 404,
    /// the same shape every account-scoped read uses, so this path leaks no existence.
    /// </returns>
    public async Task<PositionExitResult?> ExitAsync(
        Guid accountId,
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
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
            // One credential set per process (ADR-0015). Closing on an account this process does not serve would
            // be acting through someone else's venue session.
            return new PositionExitResult(PositionExitOutcome.Unreachable, 0);
        }

        try
        {
            FirmConventions conventions = await _database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
            ITradingVenue venue = _venueFactory.Create(conventions);

            IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);
            VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
            if (venueAccount is null)
            {
                return new PositionExitResult(PositionExitOutcome.Unreachable, 0);
            }

            ResolvedContract resolved = await venue.ResolveContractAsync(instrument, cancellationToken);
            PositionSnapshot after = await venue.ClosePositionAsync(
                venueAccount.Id, resolved.Contract, cancellationToken);

            // Verified, not assumed: the outcome is what the venue reports AFTER the attempt.
            return after.IsFlat
                ? new PositionExitResult(PositionExitOutcome.Flat, 0)
                : new PositionExitResult(PositionExitOutcome.StillOpen, after.NetQuantity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Operator exit of {Instrument} on account {Account} could not be completed — the position may still "
                + "be open (gh#656).",
                instrument,
                accountId);
            return new PositionExitResult(PositionExitOutcome.Unreachable, 0);
        }
    }
}
