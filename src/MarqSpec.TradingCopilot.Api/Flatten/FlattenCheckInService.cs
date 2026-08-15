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

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// The dead-man's switch's daily check-in (gh#244, R-13, ADR-0019): decides, per instrument, whether this process
/// may tell an <b>external</b> monitor that the market is flat — and stays silent when it may not, so the monitor
/// pages.
/// </summary>
/// <remarks>
/// <para>
/// It reads venue positions <b>itself</b> rather than inheriting the flatten's view. That duplication is
/// deliberate, and the same reasoning the redundant watchdog (gh#187) is built on: this is the signal that vouches
/// for flatness to the outside world, so it forms its own opinion. A shared read would inherit any mistake in the
/// tier it is meant to be independent of.
/// </para>
/// <para>
/// The cost is contained by <see cref="FlattenCheckIn.IsDue"/>: outside the window between an instrument's
/// deadline and its check-in, this does no venue work at all.
/// </para>
/// <para>
/// Runs as background plumbing with <b>no authenticated user</b>, so account reads deliberately
/// <c>IgnoreQueryFilters</c> the R-20 default-deny — the host acts for the deployment, over the accounts this
/// process's one credential set can reach (ADR-0015). Ownership is read, never re-assigned.
/// </para>
/// </remarks>
public sealed class FlattenCheckInService
{
    private readonly TradingCopilotDbContext _database;
    private readonly IProjectXVenueFactory _venueFactory;
    private readonly IDeadMansSwitch _deadMansSwitch;
    private readonly ProjectXConnectionOptions _projectX;
    private readonly FlattenOptions _flatten;
    private readonly ILogger<FlattenCheckInService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="venueFactory">Builds a venue for a connection's firm conventions.</param>
    /// <param name="deadMansSwitch">The outbound seam to the external monitor.</param>
    /// <param name="projectXOptions">Carries the credential key this process serves (ADR-0015).</param>
    /// <param name="flattenOptions">The per-instrument schedule — the deadlines check-ins hang off.</param>
    /// <param name="logger">The logger.</param>
    public FlattenCheckInService(
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IDeadMansSwitch deadMansSwitch,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        IOptions<FlattenOptions> flattenOptions,
        ILogger<FlattenCheckInService> logger)
    {
        ArgumentNullException.ThrowIfNull(projectXOptions);
        ArgumentNullException.ThrowIfNull(flattenOptions);

        _database = database;
        _venueFactory = venueFactory;
        _deadMansSwitch = deadMansSwitch;
        _projectX = projectXOptions.Value;
        _flatten = flattenOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs one check-in pass over every account this process serves.
    /// </summary>
    /// <param name="now">The instant to evaluate the schedules against.</param>
    /// <param name="lastCheckedIn">The market day each instrument last reported flat on.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The instruments reported this pass, with the market day recorded for each.</returns>
    public async Task<IReadOnlyDictionary<InstrumentId, DateOnly>> RunPassAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<InstrumentId, DateOnly> lastCheckedIn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lastCheckedIn);

        IReadOnlyList<FlattenSchedule> schedules = _flatten.ToSchedules();

        // The cheap gate first: if nothing could be reported, touch neither the database nor the venue.
        if (!schedules.Any(schedule => FlattenCheckIn.IsDue(schedule, now, Last(lastCheckedIn, schedule))))
        {
            return new Dictionary<InstrumentId, DateOnly>();
        }

        // No user context: the R-20 default-deny is bypassed deliberately (see the class note).
        List<Account> accounts = await _database.Accounts
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        if (accounts.Count == 0)
        {
            return new Dictionary<InstrumentId, DateOnly>();
        }

        List<Guid> connectionIds = [.. accounts.Select(account => account.ConnectionId).Distinct()];
        Dictionary<Guid, Connection> connections = await _database.Connections
            .IgnoreQueryFilters()
            .Where(connection => connectionIds.Contains(connection.Id))
            .ToDictionaryAsync(connection => connection.Id, cancellationToken);

        // Resolve every connection's roster BEFORE reporting anything. The decision below is about the pass as a
        // whole, and a report is an irrevocable statement to an external monitor — so nothing may be sent until it
        // is known that every held account could be read.
        List<(ITradingVenue Venue, List<VenueAccountId> Accounts)> readable = [];
        List<string> unrostered = [];

        foreach (IGrouping<Guid, Account> byConnection in accounts.GroupBy(account => account.ConnectionId))
        {
            if (!connections.TryGetValue(byConnection.Key, out Connection? connection))
            {
                continue;
            }

            // One credential set per process (ADR-0015): an account on another key belongs to a sibling process.
            if (!string.Equals(connection.CredentialKey, _projectX.CredentialKey, StringComparison.Ordinal))
            {
                continue;
            }

            FirmConventions conventions = await _database.ConventionsForConnectionAsync(connection.Id, cancellationToken);
            ITradingVenue venue = _venueFactory.Create(conventions);

            IReadOnlyList<VenueAccount> roster = await venue.GetAccountsAsync(cancellationToken);
            List<VenueAccountId> venueAccounts = [];
            foreach (Account account in byConnection)
            {
                VenueAccount? match = roster.FirstOrDefault(candidate => candidate.Id.Key == account.VenueAccountKey);
                if (match is null)
                {
                    unrostered.Add(account.VenueAccountKey);
                    continue;
                }

                venueAccounts.Add(match.Id);
            }

            if (venueAccounts.Count > 0)
            {
                readable.Add((venue, venueAccounts));
            }
        }

        // gh#850 — unknown exposure is treated as exposure. A held account the venue's roster did not report cannot
        // have its positions read, so it contributes nothing to the aggregate this pass; it used to be filtered out
        // silently, and the remaining accounts could then make an instrument look flat on evidence that EXCLUDED an
        // account we hold. That is the one thing this tier must never do: vouch to the outside world for a flatness
        // nobody verified, withdrawing the page at the moment it is most needed.
        //
        // It withholds the WHOLE pass, not the affected connection: the report is per instrument and aggregated
        // across every account this process serves, so a sibling connection reporting the same instrument flat
        // would reinstate exactly the claim being withheld. And nothing bounds WHICH instrument an unreadable
        // account is exposed in, so no narrower withhold is sound.
        //
        // Silence is the alarm (ADR-0019): the monitor pages, which is the correct outcome for a process that
        // cannot see all of its own accounts. A persistent roster gap therefore pages daily until the account is
        // rediscovered or the stale row removed — the primary tier's flatten.unrostered journal (gh#527) and the
        // watchdog's (gh#850) name the account so that is actionable rather than mysterious.
        if (unrostered.Count > 0)
        {
            _logger.LogWarning(
                "Dead-man's switch withholding every instrument this pass: {Count} held account(s) absent from the venue roster ({Accounts}). Their exposure could not be read, so flatness cannot be vouched for.",
                unrostered.Count,
                string.Join(", ", unrostered));
            return new Dictionary<InstrumentId, DateOnly>();
        }

        Dictionary<InstrumentId, DateOnly> reported = [];
        foreach ((ITradingVenue venue, List<VenueAccountId> venueAccounts) in readable)
        {
            IReadOnlyDictionary<InstrumentId, DateOnly> sent = await ReportForAccountsAsync(
                venueAccounts, venue, schedules, now, lastCheckedIn, cancellationToken);

            foreach (KeyValuePair<InstrumentId, DateOnly> entry in sent)
            {
                reported[entry.Key] = entry.Value;
            }
        }

        return reported;
    }

    /// <summary>
    /// Evaluates and reports every due instrument across one venue's accounts. Exposure is aggregated across
    /// <b>all</b> of them: one flat account does not make an instrument flat.
    /// </summary>
    /// <param name="accounts">The venue accounts to aggregate exposure over.</param>
    /// <param name="venue">The venue to read position truth from.</param>
    /// <param name="schedules">The per-instrument schedules in force.</param>
    /// <param name="now">The instant to evaluate against.</param>
    /// <param name="lastCheckedIn">The market day each instrument last reported flat on.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The instruments successfully reported, with the market day recorded for each.</returns>
    internal async Task<IReadOnlyDictionary<InstrumentId, DateOnly>> ReportForAccountsAsync(
        IReadOnlyList<VenueAccountId> accounts,
        ITradingVenue venue,
        IReadOnlyList<FlattenSchedule> schedules,
        DateTimeOffset now,
        IReadOnlyDictionary<InstrumentId, DateOnly> lastCheckedIn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(venue);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(lastCheckedIn);

        List<FlattenSchedule> due =
            [.. schedules.Where(schedule => FlattenCheckIn.IsDue(schedule, now, Last(lastCheckedIn, schedule)))];
        if (due.Count == 0)
        {
            return new Dictionary<InstrumentId, DateOnly>();
        }

        // Venue truth, never a local belief (ADR-0013) -- a check-in derived from a stale snapshot would assert a
        // flatness nobody verified.
        HashSet<string> exposedRoots = new(StringComparer.Ordinal);
        foreach (VenueAccountId account in accounts)
        {
            IReadOnlyList<PositionSnapshot> positions = await venue.GetPositionsAsync(account, cancellationToken);
            foreach (PositionSnapshot position in positions.Where(position => !position.IsFlat))
            {
                exposedRoots.Add(ProductRoot(position.Contract.Key));
            }
        }

        Dictionary<InstrumentId, DateOnly> reported = [];
        foreach (FlattenSchedule schedule in due)
        {
            ResolvedContract resolved = await venue.ResolveContractAsync(schedule.Instrument, cancellationToken);
            bool exposed = exposedRoots.Contains(ProductRoot(resolved.Contract.Key));

            CheckInDecision decision = FlattenCheckIn.Decide(
                schedule, now, exposed, Last(lastCheckedIn, schedule));

            if (decision.Action != CheckInAction.Send)
            {
                // Withheld is the interesting case and the one that pages, so it is logged at warning: when the
                // operator's phone goes off, the reason is already in the log.
                if (decision.Action == CheckInAction.Withhold)
                {
                    _logger.LogWarning("Dead-man's switch withholding: {Reason}", decision.Reason);
                }

                continue;
            }

            if (await _deadMansSwitch.ReportFlatAsync(schedule.Instrument, cancellationToken))
            {
                reported[schedule.Instrument] = decision.MarketDay;
            }

            // A rejected report is deliberately NOT recorded: leaving it unrecorded retries on the next pass,
            // where recording it would silently cost the day's check-in over one transient failure.
        }

        return reported;
    }

    private static DateOnly? Last(IReadOnlyDictionary<InstrumentId, DateOnly> map, FlattenSchedule schedule) =>
        map.TryGetValue(schedule.Instrument, out DateOnly day) ? day : null;

    // The product root shared by every month of a contract (F.US.EP for ES) -- the same matching the flatten uses
    // (gh#163), so a position in any month finds its instrument.
    private static string ProductRoot(string contractKey) => ProjectXMapping.ToQuoteSymbol(contractKey);
}
