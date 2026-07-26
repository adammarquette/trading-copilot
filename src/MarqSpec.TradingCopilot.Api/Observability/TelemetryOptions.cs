namespace MarqSpec.TradingCopilot.Api.Observability;

/// <summary>
/// OpenTelemetry configuration (gh#230, ADR-0002), bound from the <c>Telemetry</c> section.
/// </summary>
/// <remarks>
/// <b>Unconfigured is a supported state.</b> With no <see cref="OtlpEndpoint"/> the SDK is still wired and the
/// instrumentation still runs, but nothing is exported — the app starts and trades normally. Instrumentation must
/// never be able to take the process down (engineering §9): an observability outage is a reporting problem, and a
/// trading outage is not.
/// </remarks>
public sealed class TelemetryOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Telemetry";

    /// <summary>
    /// The OTLP collector endpoint (e.g. <c>http://localhost:4317</c>). Empty disables export — see the remarks.
    /// </summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>The service name stamped on every signal, so signals are attributable per service.</summary>
    public string ServiceName { get; init; } = "trading-copilot-api";

    /// <summary>Whether an exporter is configured at all.</summary>
    public bool ExportEnabled => !string.IsNullOrWhiteSpace(OtlpEndpoint);
}
