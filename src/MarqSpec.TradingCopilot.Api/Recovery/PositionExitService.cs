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
    private readonly IPositionActionJournal _journal;
    private readonly ILogger<PositionExitService> _logger;

    /// <summary>Creates the exit service.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="venueFactory">Builds a venue for the connection's firm conventions.</param>
    /// <param name="projectXOptions">The credential key this process serves (ADR-0015).</param>
    /// <param name="journal">
    /// The durable record of what was asked and what happened (gh#1143). <b>Required, not optional</b>: an optional
    /// dependency defaults to a silent no-op the moment anything constructs this service without it.
    /// </param>
    /// <param name="logger">The logger.</param>
    public PositionExitService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IPositionActionJournal journal,
        ILogger<PositionExitService> logger)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _database = database;
        _venueFactory = venueFactory;
        _projectXOptions = projectXOptions;
        _journal = journal;
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
            // Not found / not owned (R-20), and deliberately NOT journaled: there is no owner to stamp a row with,
            // and writing one would be a side channel confirming the id exists.
            return null;
        }

        Attempt attempt = await AttemptAsync(account, instrument, cancellationToken);

        // TRANSMIT, THEN JOURNAL -- ADR-0007's accepted ordering for the send path (2026-08-02 update), applied
        // here rather than re-answered: the record carries the VERIFIED outcome, which does not exist until the
        // attempt resolves. The residual it leaves (a crash between the venue accepting and this write) is the same
        // window that update names, and closing it would need a durable pre-transmit intent -- a change to what this
        // path DOES, not to what it writes down. Recorded as an open item on the ADR instead.
        //
        // The journal cannot fail this: the seam is contracted never to throw, and it takes no cancellation token,
        // so a client that hung up mid-response cannot cost the record of a real order.
        await _journal.RecordSafelyAsync(
            new PositionActionEntry
            {
                Action = PositionActionKind.Exit,
                OwnerUserId = account.UserId,
                AccountId = account.Id,
                VenueAccountKey = account.VenueAccountKey,
                Instrument = instrument.ToString(),
                Contract = attempt.ContractKey,

                // A full close asks for flat, and reads no starting size: it does not need one, and adding a
                // pre-read would put another venue round trip -- and another failure mode -- in front of the one
                // action that reduces real exposure.
                RequestedQuantity = null,
                NetQuantityBefore = null,

                // The wire contract answers 0 on Unreachable (a deliberate divergence from the reduce, #1142), but
                // the journal is what an incident reads back: an exposure nobody could read is recorded as unknown,
                // never as a 0 that would manufacture a flat out of an outage (gh#929).
                NetQuantityAfter = attempt.Result.Outcome is PositionExitOutcome.Unreachable
                    ? null
                    : attempt.Result.NetQuantity,
                Outcome = attempt.Result.Outcome.ToString(),
            },
            DateTimeOffset.UtcNow,
            _logger);

        return attempt.Result;
    }

    /// <summary>The exit itself, plus the contract it resolved to when it got that far (gh#1143).</summary>
    /// <param name="Result">The outcome, exactly as it is returned to the operator.</param>
    /// <param name="ContractKey">The venue contract, or <see langword="null"/> when the attempt never resolved one.</param>
    private sealed record Attempt(PositionExitResult Result, string? ContractKey);

    /// <summary>
    /// The exit, unchanged by gh#1143 — every decision, guard and outcome is exactly what it was; the record simply
    /// carries the contract key out alongside the result.
    /// </summary>
    /// <param name="account">The caller's account row (already ownership-checked).</param>
    /// <param name="instrument">The instrument to flatten.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The outcome, read from venue truth after the attempt.</returns>
    private async Task<Attempt> AttemptAsync(
        Account account,
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        Connection? connection = await _database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == account.ConnectionId, cancellationToken);
        if (connection is null
            || !string.Equals(connection.CredentialKey, _projectXOptions.Value.CredentialKey, StringComparison.Ordinal))
        {
            // One credential set per process (ADR-0015). Closing on an account this process does not serve would
            // be acting through someone else's venue session.
            return new Attempt(new PositionExitResult(PositionExitOutcome.Unreachable, 0), null);
        }

        // Captured as the attempt walks forward, so a fault after the contract resolved still records WHICH
        // contract the operator was closing rather than losing it to the catch.
        string? contractKey = null;

        try
        {
            FirmConventions conventions = await _database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
            ITradingVenue venue = _venueFactory.Create(conventions);

            IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);
            VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
            if (venueAccount is null)
            {
                return new Attempt(new PositionExitResult(PositionExitOutcome.Unreachable, 0), null);
            }

            ResolvedContract resolved = await venue.ResolveContractAsync(instrument, cancellationToken);
            contractKey = resolved.Contract.Key;

            PositionSnapshot after = await venue.ClosePositionAsync(
                venueAccount.Id, resolved.Contract, cancellationToken);

            // Verified, not assumed: the outcome is what the venue reports AFTER the attempt.
            return new Attempt(
                after.IsFlat
                    ? new PositionExitResult(PositionExitOutcome.Flat, 0)
                    : new PositionExitResult(PositionExitOutcome.StillOpen, after.NetQuantity),
                contractKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER went away -- abort rather than manufacture a business outcome. Narrow on purpose: a venue
            // timeout also surfaces as an OperationCanceledException, but carrying HttpClient's own internal token,
            // so it falls through to the fault handler below and is reported (and journaled) as Unreachable.
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Operator exit of {Instrument} on account {Account} could not be completed — the position may still "
                + "be open (gh#656).",
                instrument,
                account.Id);
            return new Attempt(new PositionExitResult(PositionExitOutcome.Unreachable, 0), contractKey);
        }
    }
}
