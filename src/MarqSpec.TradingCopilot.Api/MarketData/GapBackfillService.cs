using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The recovery path for an event-log retention gap (gh#306, R-1, ADR-0001): what a consumer missed while blind,
/// reconstructed from the <b>clean-historical bar store</b> (gh#302) rather than from the log, which by
/// definition no longer has it.
/// </summary>
/// <remarks>
/// <para>
/// gh#227 gave a trailing consumer a <i>signal</i> and deliberately stopped there — a consumer was told it had
/// lost events and could only resume at the head. R-1 asks for more: <i>"bars and ticks persisted with gap
/// detection and backfill on reconnect"</i>. This is that backfill.
/// </para>
/// <para>
/// <b>Time, not sequence.</b> A gap is reported as a pair of log sequences, but the store is keyed on time
/// buckets — so recovery works on the window between the consumer's last commit and the oldest surviving event's
/// occurred-at (<c>EventRetentionGap.OldestAvailableOccurredAt</c>).
/// </para>
/// <para>
/// <b>Extremes, not a replay of every bar.</b> Both decisions this feeds — "did price come within the promotion
/// band?" and "did price cross this trigger?" — are monotone in the window's extremes, so the deepest adverse
/// excursion answers them exactly. Replaying each bar in turn would cost a venue round trip per bar to reach the
/// same answer.
/// </para>
/// <para>
/// <b>The two consumers are treated differently on purpose</b>, and the difference is the point:
/// <b>stop promotion recovers</b>, because promoting is protective, one-way, and re-gated against venue truth —
/// so acting late is strictly safer than not acting. <b>Conditional firing does not</b>: firing an entry on a
/// cross that happened minutes ago would be acting on stale grounds at a price that has moved on, which ADR-0013
/// forbids. It is detected and reported instead, so the operator learns what they were blind to and decides.
/// </para>
/// </remarks>
public sealed class GapBackfillService
{
    private readonly TradingCopilotDbContext _database;
    private readonly StopPromotionService _promotion;
    private readonly IExecutionMetrics _metrics;
    private readonly ILogger<GapBackfillService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="promotion">The promotion decision the recovered extremes are fed through.</param>
    /// <param name="metrics">Raises a shortfall as a signal a rule can read, not only a log line (gh#482).</param>
    /// <param name="logger">The logger.</param>
    public GapBackfillService(
        TradingCopilotDbContext database,
        StopPromotionService promotion,
        IExecutionMetrics metrics,
        ILogger<GapBackfillService> logger)
    {
        _database = database;
        _promotion = promotion;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Re-evaluates every hidden stop against the <b>worst price the blind window actually reached</b>, so a stop
    /// whose promotion band was crossed while the consumer was blind is promoted rather than silently left hidden.
    /// </summary>
    /// <remarks>
    /// Promotion stays fully gated: the venue is still asked whether the position is open, the stop is still sized
    /// to the live remaining, and the transmit-then-record ordering is unchanged — this supplies a price, not an
    /// exemption. <b>Idempotent by construction</b>: a plan already <c>Native</c> never re-promotes, so a redelivery
    /// after a restart (at-least-once, ADR-0001) is a no-op rather than a second order.
    /// </remarks>
    /// <param name="from">The start of the blind window — the consumer's last commit.</param>
    /// <param name="to">The end of the blind window — the oldest surviving event.</param>
    /// <param name="venueId">The venue whose quotes were missed.</param>
    /// <param name="venue">The venue, for the position check and the transmit.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>What was promoted, and how much of the window the store could actually cover.</returns>
    public async Task<GapBackfillOutcome> ReplayForStopPromotionAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        VenueId venueId,
        ITradingVenue venue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(venue);

        // Background plumbing with no authenticated user, so the R-20 default-deny filter is bypassed deliberately
        // -- exactly as the live watcher does (StopPromotionService's class note).
        List<Guid> hiddenOrderIds = await _database.StopPlans
            .IgnoreQueryFilters()
            .Where(plan => plan.Staging == StopStaging.Hidden)
            .Select(plan => plan.OrderId)
            .ToListAsync(cancellationToken);

        List<InstrumentKey> instruments = await _database.Orders
            .IgnoreQueryFilters()
            .Where(order => hiddenOrderIds.Contains(order.Id))
            .Select(order => new InstrumentKey(order.Instrument, order.Symbol ?? order.Instrument))
            .Distinct()
            .ToListAsync(cancellationToken);

        List<InstrumentCoverage> coverage = [];
        int promoted = 0;

        foreach (InstrumentKey instrument in instruments)
        {
            WindowExtremes? extremes = await ExtremesAsync(instrument.Symbol, from, to, cancellationToken);
            coverage.Add(new InstrumentCoverage(
                instrument.ContractKey, instrument.Symbol, extremes?.Coverage ?? new GapCoverage(from, to, null, null)));

            if (extremes is null)
            {
                continue;
            }

            // The window's LOW as the bid and its HIGH as the ask: a long is protected by a stop below and is
            // measured on the bid, a short by one above measured on the ask -- so one call gives each side its own
            // worst case. A synthetic quote, deliberately the widest adverse excursion rather than any real tick.
            promoted += await _promotion.PromoteForQuoteAsync(
                venueId, instrument.ContractKey, extremes.Low, extremes.High, venue, cancellationToken);
        }

        GapBackfillOutcome outcome = new(promoted, coverage);

        // Metered HERE rather than at the caller (gh#482): this is where the shortfall is known, so every consumer
        // of the backfill gets the signal rather than only the one host that remembered to log it. Emitted only on
        // a shortfall -- a complete recovery is silent, so the rule cannot fire on a healthy session (ADR-0019 §4).
        foreach (InstrumentCoverage shortfall in outcome.Shortfalls)
        {
            _metrics.RecordBackfillShortfall(shortfall.ContractKey, shortfall.Coverage.Shortfall);
        }

        return outcome;
    }

    /// <summary>
    /// Finds the pending conditionals whose trigger price the blind window crossed — <b>without firing them</b>.
    /// </summary>
    /// <remarks>
    /// The deliberate non-recovery, recorded rather than omitted (gh#306). A conditional fires an <i>entry</i>, and
    /// firing one on a cross that happened minutes ago would open a position at a price that has moved on — acting
    /// on stale grounds, which ADR-0013 forbids and R-12's validity tolerance exists to prevent. So the failure
    /// direction stays "did not fire": the conditional remains <c>Pending</c> and re-decides on the next quote
    /// against fresh truth. What changes is that the operator is no longer told merely that <i>something</i> was
    /// missed — they are told <b>which</b> conditionals were crossed while blind, which is what makes the signal
    /// actionable rather than alarming.
    /// </remarks>
    /// <param name="from">The start of the blind window.</param>
    /// <param name="to">The end of the blind window.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The conditionals whose trigger the window crossed; empty when none did.</returns>
    public async Task<IReadOnlyList<MissedTrigger>> DetectMissedTriggersAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        List<ConditionalOrderRecord> pending = await _database.ConditionalOrders
            .IgnoreQueryFilters()
            .Where(order => order.Status == ConditionalStatus.Pending)
            .ToListAsync(cancellationToken);

        List<MissedTrigger> missed = [];
        Dictionary<string, WindowExtremes?> extremesBySymbol = [];

        foreach (ConditionalOrderRecord order in pending)
        {
            string symbol = order.Symbol ?? order.Instrument;
            if (!extremesBySymbol.TryGetValue(symbol, out WindowExtremes? extremes))
            {
                extremes = await ExtremesAsync(symbol, from, to, cancellationToken);
                extremesBySymbol[symbol] = extremes;
            }

            if (extremes is null)
            {
                continue;
            }

            // The trigger's own domain decision, asked of the window's extreme in the direction it cares about --
            // a rise-to trigger against the high, a fall-to against the low. Same rule as the live path, so a
            // "missed" report can never disagree with what a live quote would have done.
            ConditionalTrigger trigger = new(
                new Price(order.TriggerPrice), order.TriggerDirection);
            Price probe = order.TriggerDirection == ConditionalCrossDirection.RisesTo
                ? new Price(extremes.High)
                : new Price(extremes.Low);

            if (trigger.HasFired(probe))
            {
                missed.Add(new MissedTrigger(
                    order.Id, order.Instrument, order.TriggerPrice, order.TriggerDirection, probe.Value));
            }
        }

        return missed;
    }

    /// <summary>
    /// The highest and lowest price the store recorded for <paramref name="symbol"/> across the window, with how
    /// much of the window those bars actually covered — or <see langword="null"/> when the store holds nothing.
    /// </summary>
    private async Task<WindowExtremes?> ExtremesAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        // The FINEST resolution the store holds for this window. Extremes are the same at any bar size, but a
        // smaller bucket measures coverage more precisely -- and coverage is the number that must not overstate.
        List<BarRecord> bars = await _database.Bars
            .Where(bar => bar.Instrument == symbol && bar.BucketStart >= from && bar.BucketStart < to)
            .ToListAsync(cancellationToken);
        if (bars.Count == 0)
        {
            _logger.LogWarning(
                "Gap backfill found NO bars for {Instrument} between {From} and {To}. Nothing about that window "
                + "can be recovered for it — check that Backfill:Instruments archives this instrument.",
                symbol, from, to);
            return null;
        }

        int resolution = bars.Min(bar => bar.ResolutionMinutes);
        List<BarRecord> finest = [.. bars.Where(bar => bar.ResolutionMinutes == resolution)];

        GapCoverage coverage = GapCoverage.Of(
            from, to, [.. finest.Select(bar => bar.BucketStart)], TimeSpan.FromMinutes(resolution));

        return new WindowExtremes(finest.Max(bar => bar.High), finest.Min(bar => bar.Low), coverage);
    }

    private sealed record WindowExtremes(decimal High, decimal Low, GapCoverage Coverage);

    private sealed record InstrumentKey(string ContractKey, string Symbol);
}

/// <summary>What a stop-promotion gap backfill recovered, and what it could not (gh#306).</summary>
/// <param name="Promoted">How many hidden stops the recovered window promoted.</param>
/// <param name="Coverage">Per instrument, how much of the window the store actually covered.</param>
public sealed record GapBackfillOutcome(int Promoted, IReadOnlyList<InstrumentCoverage> Coverage)
{
    /// <summary>
    /// Whether every instrument's window was fully covered. <b>False is the answer that matters</b> — a partial
    /// backfill reported as a complete one would be worse than the silence gh#227 replaced, because it would be
    /// believed.
    /// </summary>
    public bool IsComplete => Coverage.All(instrument => instrument.Coverage.IsComplete);

    /// <summary>The instruments whose window the store could not fully cover.</summary>
    public IReadOnlyList<InstrumentCoverage> Shortfalls =>
        [.. Coverage.Where(instrument => !instrument.Coverage.IsComplete)];
}

/// <summary>How much of a blind window the store covered for one instrument (gh#306).</summary>
/// <param name="ContractKey">The venue contract key the stops are on.</param>
/// <param name="Symbol">The venue-neutral symbol the bar store is keyed on.</param>
/// <param name="Coverage">The coverage measured for it.</param>
public sealed record InstrumentCoverage(string ContractKey, string Symbol, GapCoverage Coverage);

/// <summary>
/// A pending conditional whose trigger the blind window crossed — reported, deliberately never fired (gh#306).
/// </summary>
/// <param name="ConditionalOrderId">The conditional order.</param>
/// <param name="ContractKey">The contract it is armed on.</param>
/// <param name="TriggerPrice">The price it would have fired at.</param>
/// <param name="Direction">Which way it needed to cross.</param>
/// <param name="ReachedPrice">The extreme the window actually reached in that direction.</param>
public sealed record MissedTrigger(
    Guid ConditionalOrderId,
    string ContractKey,
    decimal TriggerPrice,
    ConditionalCrossDirection Direction,
    decimal ReachedPrice);
