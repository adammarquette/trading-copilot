using System.Diagnostics;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Observability;

/// <summary>
/// The metric → trace <b>exemplar</b> pivot (gh#338, ADR-0002, engineering §7): <i>"Prometheus exemplars link a
/// latency spike to its exact trace"</i>. Found missing by QA gh#329 — the metrics pipeline configured no exemplar
/// filter, and OpenTelemetry's default is off, so an operator clicking through from an ack-latency spike to the
/// causing trace had nothing to click.
/// </summary>
/// <remarks>
/// Asserted against the <b>real</b> telemetry configuration (<see cref="TelemetryRegistration"/>) rather than a
/// hand-built provider: the defect was in that configuration, so a test that built its own would have passed while
/// production stayed dark.
/// </remarks>
public class ExemplarPipelineTests
{
    [Fact]
    public void Telemetry_ShouldAttachAnExemplar_WhenAMeasurementIsRecordedInsideASampledSpan()
    {
        using ActivitySource source = new($"exemplar-test-{Guid.NewGuid():N}");
        using ActivityListener listener = new()
        {
            ShouldListenTo = candidate => candidate.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        CapturingMetricExporter exporter = new();
        using BaseExportingMetricReader reader = new(exporter);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddTradingCopilotTelemetry();
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddReader(reader));

        using WebApplication app = builder.Build();

        // Resolve the provider explicitly: the host is never Run here, and without this the SDK never subscribes
        // to the meter, nothing is collected, and every exemplar assertion would pass or fail vacuously.
        app.Services.GetRequiredService<MeterProvider>();
        ExecutionMetrics metrics = app.Services.GetRequiredService<ExecutionMetrics>();

        using (Activity? span = source.StartActivity("exemplar-probe"))
        {
            span.Should().NotBeNull("the listener samples everything, so the span must exist");

            // A histogram measurement taken inside a recorded span — the shape the pivot exists for: an ack-latency
            // spike an operator clicks through to the trace that caused it.
            metrics.RecordOrderAck(TimeSpan.FromMilliseconds(42));

            reader.Collect();

            exporter.PointsExported.Should().BeGreaterThan(0, "the pipeline collected something — otherwise the exemplar assertion is vacuous");
            exporter.ExemplarTraceIds.Should().Contain(
                span!.TraceId,
                "a measurement recorded inside a sampled span must carry an exemplar naming that span's trace — "
                + "without it the metric → trace pivot ADR-0002 promises does not exist");
        }
    }

    [Fact]
    public void Telemetry_ShouldAttachNoExemplar_WhenTheMeasurementIsRecordedOutsideAnySpan()
    {
        // Trace-BASED, not always-on: a measurement with no span to point at must not manufacture an exemplar, or
        // every series carries a dead link and the storage cost buys nothing.
        CapturingMetricExporter exporter = new();
        using BaseExportingMetricReader reader = new(exporter);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddTradingCopilotTelemetry();
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddReader(reader));

        using WebApplication app = builder.Build();

        // Resolve the provider explicitly: the host is never Run here, and without this the SDK never subscribes
        // to the meter, nothing is collected, and every exemplar assertion would pass or fail vacuously.
        app.Services.GetRequiredService<MeterProvider>();
        ExecutionMetrics metrics = app.Services.GetRequiredService<ExecutionMetrics>();

        Activity.Current = null;
        metrics.RecordTimeToFlat(FlattenTier.Primary, TimeSpan.FromMilliseconds(7));
        reader.Collect();

        exporter.PointsExported.Should().BeGreaterThan(0, "the pipeline collected something — otherwise this assertion is vacuous");
        exporter.ExemplarTraceIds.Should().NotContain(
            default(ActivityTraceId), "a measurement outside a trace attaches no exemplar rather than an empty one");
    }

    /// <summary>
    /// Collects the trace ids of every exemplar the pipeline attaches. Read <b>inside</b> <c>Export</c> because the
    /// SDK reuses its metric and point structures once the batch returns.
    /// </summary>
    private sealed class CapturingMetricExporter : BaseExporter<Metric>
    {
        public List<ActivityTraceId> ExemplarTraceIds { get; } = [];

        /// <summary>How many metric points were exported at all — guards against a vacuously empty assertion.</summary>
        public int PointsExported { get; private set; }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
            {
                foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
                {
                    PointsExported++;
                    if (point.TryGetExemplars(out ReadOnlyExemplarCollection exemplars))
                    {
                        foreach (ref readonly Exemplar exemplar in exemplars)
                        {
                            ExemplarTraceIds.Add(exemplar.TraceId);
                        }
                    }
                }
            }

            return ExportResult.Success;
        }
    }
}
