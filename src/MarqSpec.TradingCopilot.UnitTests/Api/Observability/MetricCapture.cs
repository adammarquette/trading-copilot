using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Api.Observability;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Observability;

/// <summary>
/// Captures measurements from a private <see cref="ExecutionMetrics"/> instance for the gh#295 wiring tests —
/// <see cref="ExecutionMetrics"/> is a sealed sink with no interface to fake, so a test asserts emission through a
/// <see cref="MeterListener"/> instead (the pattern <c>ExecutionMetricsTests</c> established).
/// </summary>
/// <remarks>
/// Each instance owns a <b>uniquely-named</b> meter, so its listener observes only its own measurements. Without
/// that, a listener filtering on the shared meter name also receives measurements from the real
/// <see cref="ExecutionMetrics"/> that other suites construct in parallel — a data race on the buffer, not a
/// hypothetical (it is why <see cref="ExecutionMetrics"/> has the meter-name constructor overload).
/// </remarks>
public sealed class MetricCapture : IDisposable
{
    private readonly string _meterName = ExecutionMetrics.MeterName + ".Test." + Guid.NewGuid().ToString("N");
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, double Value, Dictionary<string, string?> Tags)> _measurements = [];

    /// <summary>The metrics sink to hand to the service under test.</summary>
    public ExecutionMetrics Metrics { get; }

    /// <summary>Creates the capture and starts listening on its private meter.</summary>
    public MetricCapture()
    {
        Metrics = new ExecutionMetrics(_meterName);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == _meterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Add((instrument.Name, value, TagsOf(tags))));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _measurements.Add((instrument.Name, value, TagsOf(tags))));

        _listener.Start();
    }

    /// <summary>Pumps the observable gauges — their value only appears after an explicit read.</summary>
    public void PumpGauges() => _listener.RecordObservableInstruments();

    /// <summary>Every measurement recorded for one instrument, in order.</summary>
    public IReadOnlyList<(string Instrument, double Value, Dictionary<string, string?> Tags)> For(string instrument) =>
        [.. _measurements.Where(measurement => measurement.Instrument == instrument)];

    /// <inheritdoc />
    public void Dispose()
    {
        _listener.Dispose();
        Metrics.Dispose();
    }

    private static Dictionary<string, string?> TagsOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string?> map = [];
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString();
        }

        return map;
    }
}
