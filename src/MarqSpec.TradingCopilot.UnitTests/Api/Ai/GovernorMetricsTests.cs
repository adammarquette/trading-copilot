using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Api.Ai;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The governor's configured daily budget, published as a gauge (gh#506) so the gh#412 dashboard can compute
/// headroom from Prometheus alone instead of a hand-maintained Grafana constant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Observability only.</b> Enforcement is unchanged and stays on the persisted <c>AIUsage</c> ledger floor,
/// because a <c>System.Diagnostics.Metrics</c> meter is export-only and cannot be read back in-process (gh#448).
/// This gauge is the operator's warning light, never the control.
/// </para>
/// <para>
/// The property worth a test rather than a glance: <b>an unset budget emits no series at all</b>. Publishing 0
/// would make "no cap configured" — the inert default — arithmetically indistinguishable from "a cap of zero", and
/// the headroom panel divides by this value. Zero would render every deployment without a cap as either infinite
/// overspend or a divide-by-zero, which on a cost view is a lie in the alarming direction.
/// </para>
/// </remarks>
public sealed class GovernorMetricsTests : IDisposable
{
    // A unique meter per instance: xUnit runs classes in parallel and a listener filtering on the shared meter NAME
    // would observe another instance's measurements.
    private readonly string _meterName = EmbeddingMetrics.MeterName + ".GovernorTest." + Guid.NewGuid().ToString("N");
    private readonly List<(string Instrument, double Value)> _observed = [];
    private MeterListener? _listener;
    private GovernorMetrics? _metrics;

    public void Dispose()
    {
        _listener?.Dispose();
        _metrics?.Dispose();
    }

    [Fact]
    public void DailyBudget_ShouldPublishTheConfiguredCap_SoHeadroomIsComputedFromPrometheusAlone()
    {
        Observe(new GovernorOptions { DailyBudgetUsd = 12.50m });

        _observed.Should().ContainSingle(m => m.Instrument == GovernorMetrics.DailyBudget)
            .Which.Value.Should().Be(12.50d, "the dashboard divides by this instead of a hand-copied $budget constant");
    }

    [Fact]
    public void DailyBudget_ShouldPublishNothing_WhenNoCapIsConfigured()
    {
        // The whole reason this is an ObservableGauge returning a SEQUENCE rather than a value.
        Observe(new GovernorOptions { DailyBudgetUsd = null });

        _observed.Should().NotContain(m => m.Instrument == GovernorMetrics.DailyBudget,
            "an absent cap must stay distinguishable from a cap of zero — the headroom panel divides by this");
    }

    [Fact]
    public void DailyBudget_ShouldDeclareAnAnnotationUnit_SoTheExporterDoesNotAppendItToTheName()
    {
        // gh#505's lesson, applied at the point a new cost-shaped instrument is added rather than rediscovered
        // against a live Prometheus later: a bare "USD" is appended verbatim, giving ai_governor_daily_budget_usd_USD.
        string? unit = null;
        _metrics = new GovernorMetrics(Options.Create(new GovernorOptions { DailyBudgetUsd = 1m }), _meterName);
        using MeterListener probe = new();
        probe.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == _meterName && instrument.Name == GovernorMetrics.DailyBudget)
            {
                unit = instrument.Unit;
            }
        };
        probe.Start();

        unit.Should().StartWith("{").And.EndWith("}");
    }

    /// <summary>Builds the meter with these options and forces one observation pass.</summary>
    private void Observe(GovernorOptions options)
    {
        _metrics = new GovernorMetrics(Options.Create(options), _meterName);
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == _meterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            _observed.Add((instrument.Name, value)));
        _listener.Start();
        _listener.RecordObservableInstruments();
    }
}
