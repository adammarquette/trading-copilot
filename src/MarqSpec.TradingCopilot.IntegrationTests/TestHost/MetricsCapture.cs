using System.Diagnostics.Metrics;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// An in-process collector double (gh#330): a <see cref="MeterListener"/> filtered to a <b>single meter name</b>, so
/// a test observes only its own <c>ExecutionMetrics</c> instance and never a parallel class's measurements. Captures
/// every counter/histogram measurement as it happens, and scrapes the observable gauges on demand — the pre-merge
/// stand-in for Prometheus that lets the execution-SLI guards assert on what the pipeline actually received.
/// </summary>
public sealed class MetricsCapture : IDisposable
{
    private readonly MeterListener _listener;
    private readonly List<Measurement> _measurements = [];
    private readonly Lock _gate = new();

    /// <summary>One recorded measurement: the instrument, its value, and its dimensions.</summary>
    /// <param name="Instrument">The instrument name (e.g. <c>trading.gate.decisions</c>).</param>
    /// <param name="Value">The measured value.</param>
    /// <param name="Tags">The dimensions attached to the measurement.</param>
    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>Starts listening to the meter named <paramref name="meterName"/>.</summary>
    public MetricsCapture(string meterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>A snapshot of everything captured so far.</summary>
    public IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (_gate)
            {
                return [.. _measurements];
            }
        }
    }

    /// <summary>Polls every observable instrument (the gauges) now — one scrape, as a backend would.</summary>
    public void Scrape() => _listener.RecordObservableInstruments();

    /// <summary>Every measurement recorded for one instrument.</summary>
    public IReadOnlyList<Measurement> For(string instrument) =>
        [.. Measurements.Where(measurement => measurement.Instrument == instrument)];

    /// <summary>The distinct values a given tag key took across an instrument's measurements.</summary>
    public IReadOnlyList<string> TagValues(string instrument, string tagKey) =>
        [.. For(instrument)
            .Where(measurement => measurement.Tags.ContainsKey(tagKey))
            .Select(measurement => measurement.Tags[tagKey]?.ToString() ?? string.Empty)
            .Distinct()];

    /// <summary>The union of every tag KEY seen on an instrument — the label set, for a cardinality check.</summary>
    public IReadOnlyCollection<string> TagKeys(string instrument) =>
        [.. For(instrument).SelectMany(measurement => measurement.Tags.Keys).Distinct()];

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, object?> copy = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _measurements.Add(new Measurement(instrument.Name, value, copy));
        }
    }

    /// <summary>Stops listening and releases the meter subscription.</summary>
    public void Dispose() => _listener.Dispose();
}
