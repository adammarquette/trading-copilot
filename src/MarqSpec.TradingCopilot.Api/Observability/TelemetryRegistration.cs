using System.Diagnostics;
using System.Reflection;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Observability;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MarqSpec.TradingCopilot.Api.Observability;

/// <summary>
/// Wires the three OpenTelemetry signals — traces, metrics and logs — over OTLP (gh#230, ADR-0002, §7).
/// </summary>
public static class TelemetryRegistration
{
    /// <summary>The application's own <see cref="ActivitySource"/> — spans we create, as opposed to instrumented ones.</summary>
    public static ActivitySource Source { get; } = new("MarqSpec.TradingCopilot");

    /// <summary>Adds the SDK, the instrumentations, and the OTLP exporters when one is configured.</summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static WebApplicationBuilder AddTradingCopilotTelemetry(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<ExecutionMetrics>();

        // Callers depend on the Domain seam; the concrete Meter-backed sink is the composition root's choice.
        //
        // Wrapped so a sink fault can never reach the trading action being measured (gh#343, engineering §9). Done
        // here, at the ONE place the seam is bound, rather than by guarding each of the many call sites — which
        // would leave the invariant to be re-remembered by whoever adds the next measurement.
        builder.Services.AddSingleton<IExecutionMetrics>(provider => new FailureTolerantExecutionMetrics(
            provider.GetRequiredService<ExecutionMetrics>(),
            provider.GetRequiredService<ILogger<FailureTolerantExecutionMetrics>>()));
        builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection(TelemetryOptions.SectionName));
        TelemetryOptions options =
            builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();

        // Resource attributes make a signal attributable: WHICH service, WHICH build, WHICH environment. Without
        // the environment here, a staging trace and a production trace are indistinguishable in one backend --
        // and this system's whole R-14 posture rests on knowing which environment an action came from.
        ResourceBuilder resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: options.ServiceName,
                serviceVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0")
            .AddAttributes([new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)]);

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .AddSource(Source.Name)
                    .AddAspNetCoreInstrumentation(instrumentation => instrumentation.RecordException = true)
                    .AddHttpClientInstrumentation()
                    // EF Core's Postgres provider emits its own spans; adding the source is how DB commands
                    // appear in the trace without a separate (still pre-release) EF instrumentation package.
                    .AddSource("Npgsql");

                if (options.ExportEnabled)
                {
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint!));
                }
            })
            .WithMetrics(metrics =>
            {
                // RED comes from these two instrumentations: request rate, error rate, and duration as a
                // HISTOGRAM (the SDK's default for these instruments), so p50/p95/p99 are derivable. A mean would
                // hide exactly the tail that matters on an execution path.
                metrics
                    .SetResourceBuilder(resource)
                    // Exemplars are what make a latency spike CLICKABLE: each carries the trace id of the span the
                    // measurement was taken in, so Grafana jumps metric -> trace (ADR-0002, §7). Without this the
                    // SDK's default is off and the pivot both documents promise silently does not exist (gh#338).
                    //
                    // TRACE-BASED, not always-on: an exemplar is attached only when the measurement was recorded
                    // inside a sampled span, which is the only case the pivot can be followed. Always-on would
                    // decorate every series with a dead link and pay the storage for nothing.
                    .SetExemplarFilter(ExemplarFilterType.TraceBased)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // The execution-specific SLIs (gh#232) -- this system's own signals, beside the generic RED.
                    .AddMeter(ExecutionMetrics.MeterName)
                    // AI spend: tokens / cost / latency per embed call (gh#403).
                    .AddMeter(EmbeddingMetrics.MeterName);

                if (options.ExportEnabled)
                {
                    metrics.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint!));
                }
            });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resource);

            // The trace ↔ logs pivot (§7): every record carries the active trace_id / span_id, so a latency spike
            // in Grafana jumps straight to the log lines emitted inside that span.
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;

            if (options.ExportEnabled)
            {
                logging.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint!));
            }
        });

        return builder;
    }
}
