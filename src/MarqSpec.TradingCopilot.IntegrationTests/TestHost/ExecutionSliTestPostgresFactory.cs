using System.Diagnostics.Metrics;
using MarqSpec.TradingCopilot.Api.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// The host for the execution-SLI verification suite (gh#330). It inherits the deterministic flatten host (venue
/// stub, hosted services stripped so the only pass is the test's) and swaps the <b>concrete</b>
/// <see cref="ExecutionMetrics"/> for one on a <b>private meter name</b>, so a listener observes only this host's
/// measurements and never another suite's running in parallel.
/// </summary>
/// <remarks>
/// <para>
/// The swap replaces the <b>concrete sink</b>, deliberately <b>not</b> the <c>IExecutionMetrics</c> registration:
/// the composition root binds the interface <i>through</i> the concrete type, so any decorator it wraps the seam in
/// still applies. Replacing the interface registration would silently bypass such a decorator and test a
/// composition that does not exist in production — the exact false-green gh#343 uncovered in gh#331's harness.
/// </para>
/// <para>
/// A <b>restarted</b> host gets its own meter name (see <see cref="CreateRestartedHost"/>), because the gauges live
/// in memory: a restart that reused this instance would read the pre-restart value and prove nothing about
/// rehydration.
/// </para>
/// </remarks>
public sealed class ExecutionSliTestPostgresFactory : StubbedVenuePostgresFactory
{
    /// <summary>The meter this host records to — private to this fixture.</summary>
    public string MeterName { get; } = ExecutionMetrics.MeterName + ".Sli." + Guid.NewGuid().ToString("N");

    /// <summary>The meter a <see cref="CreateRestartedHost"/> host records to — a fresh sink, as a real restart has.</summary>
    public string RestartedMeterName { get; } = ExecutionMetrics.MeterName + ".SliRestart." + Guid.NewGuid().ToString("N");

    /// <summary>Every measurement this fixture's hosts emitted, attributable to the meter that produced it.</summary>
    public SliCapture Capture { get; } = new();

    /// <summary>
    /// Boots a second host on the <b>same database</b>, with a <b>fresh</b> metrics sink — the "restart". Its startup
    /// tasks re-run, so whatever the durable rows say must be re-established in the new host's gauges.
    /// </summary>
    /// <returns>The restarted host.</returns>
    public WebApplicationFactory<Program> CreateRestartedHost() =>
        WithWebHostBuilder(builder => builder.ConfigureServices(services => ReplaceSink(services, RestartedMeterName)));

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Deterministic host, as the flatten suites use: the always-on services run on the real wall clock and
        // would emit flatten measurements of their own once the stub reports positions, contaminating the counts
        // this suite asserts on. Every pass here is the test's own explicit one.
        foreach (ServiceDescriptor hosted in services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(hosted);
        }

        ReplaceSink(services, MeterName);
        Capture.ListenTo(MeterName, RestartedMeterName);
    }

    private static void ReplaceSink(IServiceCollection services, string meterName)
    {
        ServiceDescriptor? concrete = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(ExecutionMetrics));
        if (concrete is not null)
        {
            services.Remove(concrete);
        }

        services.AddSingleton(new ExecutionMetrics(meterName));
    }
}

/// <summary>
/// Captures execution-SLI measurements off a <see cref="MeterListener"/>, tagged with the meter that produced them
/// so a pre- and post-restart host can be told apart (gh#330).
/// </summary>
public sealed class SliCapture
{
    private readonly List<Measurement> _measurements = [];
    private readonly Lock _gate = new();
    private readonly HashSet<string> _meterNames = new(StringComparer.Ordinal);
    private MeterListener? _listener;

    /// <summary>One recorded measurement.</summary>
    /// <param name="MeterName">The meter that produced it — identifies which host, across a restart.</param>
    /// <param name="Instrument">The instrument name (e.g. <c>trading.gate.decisions</c>).</param>
    /// <param name="Value">The recorded value.</param>
    /// <param name="Tags">Its dimensions.</param>
    public sealed record Measurement(string MeterName, string Instrument, double Value, IReadOnlyDictionary<string, string?> Tags);

    /// <summary>Starts listening to the named meters. Idempotent — the fixture may configure several hosts.</summary>
    /// <param name="meterNames">The meters to observe.</param>
    public void ListenTo(params string[] meterNames)
    {
        lock (_gate)
        {
            foreach (string name in meterNames ?? [])
            {
                _meterNames.Add(name);
            }

            if (_listener is not null)
            {
                return;
            }

            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    bool listening;
                    lock (_gate)
                    {
                        listening = _meterNames.Contains(instrument.Meter.Name);
                    }

                    if (listening)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.Start();
        }
    }

    /// <summary>Pumps the observable gauges — their value only materialises on an explicit read.</summary>
    public void PumpGauges() => _listener?.RecordObservableInstruments();

    /// <summary>Everything captured so far.</summary>
    public IReadOnlyList<Measurement> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _measurements];
            }
        }
    }

    /// <summary>Every measurement for one instrument, in order.</summary>
    /// <param name="instrument">The instrument name.</param>
    /// <returns>Its measurements.</returns>
    public IReadOnlyList<Measurement> For(string instrument) =>
        [.. All.Where(measurement => string.Equals(measurement.Instrument, instrument, StringComparison.Ordinal))];

    /// <summary>Forgets everything captured — call at the start of a test that counts.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _measurements.Clear();
        }
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string?> map = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString();
        }

        lock (_gate)
        {
            _measurements.Add(new Measurement(instrument.Meter.Name, instrument.Name, value, map));
        }
    }
}
