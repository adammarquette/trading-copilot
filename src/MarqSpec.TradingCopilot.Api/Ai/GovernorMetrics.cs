using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Publishes the AI spend governor's <b>configured</b> daily budget as a gauge (gh#506, ADR-0008 / ADR-0002), on the
/// same <see cref="EmbeddingMetrics.MeterName"/> meter as the LLM and embedding spend instruments.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the cap needs to be a metric at all.</b> The gh#412 dashboard draws governor headroom — spend against the
/// configured ceiling — because ADR-0008's point is that approaching the cap should be visible <i>before</i> the
/// governor pauses agent review. But the cap lives in application configuration
/// (<see cref="GovernorOptions.DailyBudgetUsd"/>), and Prometheus cannot see application configuration, so the
/// dashboard mirrored it as a hand-maintained Grafana constant. Change the deployed value and the headroom gauge
/// silently reports against the old one — and a wrong headroom reading on a cost governor is the same class of
/// failure as a wrong cap.
/// </para>
/// <para>
/// <b>Observability only.</b> Enforcement is untouched: the governor caps on the persisted <c>AIUsage</c> ledger
/// floor, because a <c>System.Diagnostics.Metrics</c> meter is export-only and unreadable in-process (gh#448). This
/// is the operator's warning light, never the control — the same separation ADR-0008 draws everywhere else.
/// </para>
/// <para>
/// <b>An unset budget emits nothing, not zero.</b> That is the reason this is an observable gauge yielding a
/// <i>sequence</i>: publishing 0 for the inert no-cap default would make "no cap configured" arithmetically
/// identical to "a cap of zero", and the headroom panel <i>divides</i> by this value. Absence is the honest
/// encoding of absence.
/// </para>
/// </remarks>
public sealed class GovernorMetrics : IDisposable
{
    /// <summary>The configured daily spend ceiling in USD; absent entirely when no cap is set.</summary>
    public const string DailyBudget = "ai.governor.daily_budget_usd";

    private readonly Meter _meter;

    /// <summary>Creates the meter and its gauge.</summary>
    /// <param name="options">The governor configuration the gauge reports.</param>
    /// <param name="meterName">
    /// Overrides the meter name. Production leaves this null and gets <see cref="EmbeddingMetrics.MeterName"/>; a
    /// test passes a unique name so its listener observes only its own instance.
    /// </param>
    public GovernorMetrics(IOptions<GovernorOptions> options, string? meterName = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _meter = new Meter(meterName ?? EmbeddingMetrics.MeterName);

        // Read through IOptions on every observation rather than captured once, so a reloaded configuration is
        // reflected without a restart -- the drift this instrument exists to remove would otherwise just move.
        // The unit is the ANNOTATION form: a bare "USD" is appended to the exported name verbatim (gh#505).
        _meter.CreateObservableGauge(
            DailyBudget,
            () => Observe(options.Value.DailyBudgetUsd),
            unit: "{USD}",
            description: "The AI spend governor's configured daily ceiling in USD; absent when no cap is set.");
    }

    private static IEnumerable<Measurement<double>> Observe(decimal? budget) =>
        budget is null ? [] : [new Measurement<double>((double)budget.Value)];

    /// <summary>Disposes the meter.</summary>
    public void Dispose() => _meter.Dispose();
}
