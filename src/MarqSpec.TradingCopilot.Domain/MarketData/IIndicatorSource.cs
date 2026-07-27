namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// Reads a pre-computed indicator value (gh#310) — the seam the execution path consults, and the reason
/// <see cref="AverageTrueRange"/> can reach a stop without <c>StopPlan</c> learning what an indicator is.
/// </summary>
/// <remarks>
/// <para>
/// The delivery decision (gh#13, 2026-07-26) put metric resolution in the <b>caller</b>: the promotion watcher
/// turns whichever <c>StopProximityMetric</c> is configured — ticks, distance-fraction, or ATR — into an
/// absolute band distance, and hands <c>StopPlan</c> that. So this seam is consulted by the watcher, never by
/// the domain value object, which stays pure, immutable and reconstructable from the database.
/// </para>
/// <para>
/// <b>A missing value is a normal answer, not an error.</b> Insufficient history, a projection that has not
/// caught up, an instrument with no bars — all yield <see langword="null"/>. The caller must treat that as
/// "cannot measure, so do not promote" rather than substituting a default: a fallback distance is precisely the
/// silent mis-measurement <c>StopPlan</c>'s refusal was written to prevent (gh#311).
/// </para>
/// </remarks>
public interface IIndicatorSource
{
    /// <summary>
    /// The most recent average true range at or before <paramref name="asOf"/>, or <see langword="null"/> when
    /// none can be measured.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="resolutionMinutes">The bar size the indicator was computed over.</param>
    /// <param name="asOf">The moment to read as of — values after it are ignored, so a replay stays honest.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The value, or <see langword="null"/> when unavailable.</returns>
    Task<decimal?> GetAverageTrueRangeAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        DateTimeOffset asOf,
        CancellationToken cancellationToken);
}
