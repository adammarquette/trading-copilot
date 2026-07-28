using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Observability;

/// <summary>
/// Publishes the protection census (gh#370, ADR-0019): how many positions are open, and how many of those have
/// <b>no protective stop resting at the venue</b>.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0019 names <i>"open position with no native safety stop"</i> as a <b>P1</b> and gh#245 could not write the
/// rule, because nothing measured it. This is the measurement. It is the closest thing the system has to a direct
/// check of its own central promise — that a live position is never without a real exchange-held stop.
/// </para>
/// <para>
/// <b>Venue truth on both sides.</b> Positions and working orders both come from the venue; no local state is
/// consulted. Every local record of protection — a <c>StopPlan</c>'s staging, an order row — is a <i>belief</i>
/// about the venue, and this exists precisely to catch the case where that belief is wrong. Reconciling against
/// our own records would make it agree with itself and detect nothing.
/// </para>
/// <para>
/// <b>Unknown is not zero.</b> If the venue cannot be read, the census is not published at all rather than
/// published as zero: a stale or invented zero reads as "nothing unprotected", turning an outage into a false
/// all-clear on the one metric ADR-0019 pages on. An unwritten gauge goes stale visibly, and the P1 on a silent
/// telemetry pipeline covers that case.
/// </para>
/// <para>
/// Runs as background plumbing with <b>no authenticated user</b>, so its account query deliberately
/// <c>IgnoreQueryFilters</c> the R-20 default-deny filter — it measures the deployment's exposure, not one user's.
/// </para>
/// </remarks>
public sealed class ProtectionMonitorService
{
    private readonly TradingCopilotDbContext _database;
    private readonly IExecutionMetrics _metrics;
    private readonly ILogger<ProtectionMonitorService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The scoped database — used only to enumerate the accounts to ask about.</param>
    /// <param name="metrics">Where the census is published.</param>
    /// <param name="logger">The logger.</param>
    public ProtectionMonitorService(
        TradingCopilotDbContext database,
        IExecutionMetrics metrics,
        ILogger<ProtectionMonitorService> logger)
    {
        _database = database;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles every account's positions against its working orders and publishes the totals.
    /// </summary>
    /// <param name="venue">The venue to ask.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public async Task ObserveAsync(ITradingVenue venue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(venue);

        // Background plumbing: no user context, so the R-20 filter is bypassed deliberately (see the class note).
        List<string> accountKeys = await _database.Accounts
            .IgnoreQueryFilters()
            .Select(account => account.VenueAccountKey)
            .Distinct()
            .ToListAsync(cancellationToken);

        int open = 0;
        int unprotected = 0;

        foreach (string accountKey in accountKeys)
        {
            VenueAccountId account = VenueAccountId.Create(venue.Id, accountKey);

            IReadOnlyList<PositionSnapshot> positions;
            IReadOnlyList<WorkingOrder> workingOrders;
            try
            {
                positions = await venue.GetPositionsAsync(account, cancellationToken);
                workingOrders = await venue.GetWorkingOrdersAsync(account, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // UNKNOWN, not zero. Abandon the whole pass rather than publish a partial census: a total that
                // silently omits an unreachable account is indistinguishable from one where that account is flat.
                _logger.LogWarning(
                    error,
                    "Protection census could not read venue truth for account {Account}; publishing nothing this "
                    + "pass rather than an incomplete count.",
                    accountKey);
                return;
            }

            ProtectionCensus census = PositionProtection.Census(positions, workingOrders);
            open += census.Open;
            unprotected += census.Unprotected;
        }

        _metrics.SetPositionProtection(open, unprotected);

        if (unprotected > 0)
        {
            // Logged as well as metered: the metric raises the page, but the log is what an operator reads when
            // they get it, and the alerting stack is opt-in (ADR-0002) while this log is always on.
            _logger.LogError(
                "{Unprotected} of {Open} open position(s) have NO protective stop resting at the venue.",
                unprotected, open);
        }
    }
}
