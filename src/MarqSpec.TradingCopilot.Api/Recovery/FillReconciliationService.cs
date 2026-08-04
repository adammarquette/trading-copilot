using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// Asks venue truth whether an order stamped with a given <c>customTag</c> ever <b>filled</b> (gh#631) — the
/// fill-level sibling of <see cref="WorkingOrderReconciliationService"/> and <see cref="PositionReconciliationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// It closes the one gap the other two cannot: a fire or take that <b>placed, filled and round-tripped</b> leaves no
/// resting order (a fill is not a working order) and no position (its bracket closed it), so through those two reads
/// alone it is indistinguishable from an attempt that never reached the market at all. Only fill history separates
/// them, and that difference decides whether releasing a stranded row re-arms something that already executed.
/// </para>
/// <para>
/// <b>Read-only, and a veto rather than an authorisation.</b> Nothing here writes or executes. Callers may use a
/// reported fill to <i>stop</i> a release they would otherwise perform; they must not use any other answer to
/// <i>perform</i> one they otherwise would not. The reasoning lives on <see cref="TaggedFillEvidence"/>.
/// </para>
/// <para>
/// <b>A failed read is <see cref="TaggedFillStatus.Unavailable"/>, never <see cref="TaggedFillStatus.NoFillFound"/>.</b>
/// That is the same discipline as the sibling services' declared-unknown basis: on this question "we could not ask"
/// and "nothing is there" are opposite answers, and the cost of confusing them is a second live order.
/// </para>
/// <para>
/// Request-scoped, so the R-20 filter applies — an account not owned by the caller is simply not found.
/// </para>
/// </remarks>
public sealed class FillReconciliationService
{
    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IOptions<ProjectXConnectionOptions> _projectXOptions;
    private readonly ILogger<FillReconciliationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The request-scoped context (carries the R-20 filter).</param>
    /// <param name="venueFactory">Builds the venue client for a connection's conventions.</param>
    /// <param name="projectXOptions">This process's ProjectX credential key (ADR-0015).</param>
    /// <param name="logger">Logs a read that could not reach venue truth.</param>
    public FillReconciliationService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        ILogger<FillReconciliationService> logger)
    {
        _database = database;
        _venueFactory = venueFactory;
        _projectXOptions = projectXOptions;
        _logger = logger;
    }

    /// <summary>
    /// Asks whether an order tagged <paramref name="customTag"/> on <paramref name="accountId"/> ever filled.
    /// </summary>
    /// <param name="accountId">The account the order was placed on.</param>
    /// <param name="customTag">The tag stamped at transmit time — the stranded row's own id.</param>
    /// <param name="since">How far back to search; the caller supplies the row's transmit instant.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The venue's answer, or <see langword="null"/> when the account is not found or not owned by the caller (R-20).
    /// </returns>
    public async Task<TaggedFillEvidence?> FindFillAsync(
        Guid accountId,
        string customTag,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customTag);

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
            // No connection, or one this process does not hold credentials for (ADR-0015): we cannot ask, so we do
            // not know -- and not-knowing is never "did not fill".
            return TaggedFillEvidence.Unavailable(customTag);
        }

        try
        {
            FirmConventions conventions = await _database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
            ITradingVenue venue = _venueFactory.Create(conventions);

            IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);
            VenueAccount? venueAccount = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
            if (venueAccount is null)
            {
                return TaggedFillEvidence.Unavailable(customTag); // the venue no longer reports the account
            }

            TaggedFillEvidence evidence = await venue.FindFilledOrderByTagAsync(
                venueAccount.Id, customTag, since, cancellationToken);

            // An adapter that answers with an unset status has not actually answered. Coerce to Unsupported rather
            // than Unavailable: an unset enum is OUR bug, not a venue condition, and the caller treats Unavailable
            // as a reason to refuse and strand the row. Failing our own bug closed would strand every reconcile on
            // that venue; treating it as "this venue did not answer" leaves the caller with exactly the behaviour it
            // had before this read existed, which is the guarantee this whole path is built on.
            return evidence.Status == TaggedFillStatus.Unknown
                ? TaggedFillEvidence.Unsupported(customTag)
                : evidence;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(
                error,
                "Fill-history read for account {AccountId} under tag {CustomTag} could not reach venue truth; the "
                + "answer is unavailable, which is NOT 'did not fill'.",
                accountId,
                customTag);
            return TaggedFillEvidence.Unavailable(customTag);
        }
    }
}
