using MarqSpec.TradingCopilot.Api.Observability;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// Maps the execution SLIs' <b>instrument</b> names onto the names Prometheus actually serves, and answers which of
/// them a scraped name list is missing (gh#332).
/// </summary>
/// <remarks>
/// <para>
/// A pure function on purpose. The staging tier cannot run in pre-merge CI, so the matching rule — the part most
/// likely to be wrong, and the reason a first staging run would fail for the <i>wrong</i> reason — is extracted here
/// where it <b>can</b> be unit-tested and proven able to fail.
/// </para>
/// <para>
/// <b>Why prefix matching rather than exact names.</b> The OTLP → Prometheus path rewrites <c>.</c> to <c>_</c> and
/// then decorates by instrument kind: a monotonic counter gains <c>_total</c>, a histogram becomes
/// <c>_bucket</c> / <c>_sum</c> / <c>_count</c>, a gauge is served bare. Pinning exact strings would encode one
/// exporter's suffix convention and break on a collector upgrade that is not a regression in the system under test.
/// Matching the normalised base name asserts what the issue actually cares about — <i>the family is being scraped</i>.
/// </para>
/// </remarks>
public static class PrometheusMetricNames
{
    /// <summary>Every execution SLI instrument gh#232 / gh#295 defines — the set staging must be serving.</summary>
    public static IReadOnlyList<string> ExecutionSliInstruments { get; } =
    [
        ExecutionMetrics.GateDecisions,
        ExecutionMetrics.FlattenDeadlines,
        ExecutionMetrics.TimeToFlat,
        ExecutionMetrics.OrderAckLatency,
        ExecutionMetrics.KillSwitchEngaged,
        ExecutionMetrics.OrphanedStops,
        ExecutionMetrics.RetentionGaps,
        ExecutionMetrics.PipelineLag,
    ];

    /// <summary>The Prometheus base name for an instrument — dots become underscores.</summary>
    /// <param name="instrument">The instrument name (e.g. <c>trading.gate.decisions</c>).</param>
    /// <returns>The normalised base (e.g. <c>trading_gate_decisions</c>).</returns>
    public static string ToBaseName(string instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return instrument.Replace('.', '_');
    }

    /// <summary>
    /// The execution SLIs that <paramref name="scrapedNames"/> does <b>not</b> contain — empty when every family is
    /// being served. A family counts as present when a scraped name equals its base or extends it with an
    /// exporter suffix (<c>_total</c>, <c>_bucket</c>, …).
    /// </summary>
    /// <param name="scrapedNames">The metric names Prometheus reports.</param>
    /// <returns>The missing instrument names, in declaration order.</returns>
    public static IReadOnlyList<string> MissingExecutionSlis(IEnumerable<string> scrapedNames)
    {
        ArgumentNullException.ThrowIfNull(scrapedNames);
        HashSet<string> scraped = [.. scrapedNames];

        return
        [
            .. ExecutionSliInstruments.Where(instrument =>
            {
                string expected = ToBaseName(instrument);
                return !scraped.Any(name =>
                    string.Equals(name, expected, StringComparison.Ordinal)
                    || (name.StartsWith(expected, StringComparison.Ordinal)
                        && name.Length > expected.Length
                        && name[expected.Length] == '_'));
            }),
        ];
    }
}
