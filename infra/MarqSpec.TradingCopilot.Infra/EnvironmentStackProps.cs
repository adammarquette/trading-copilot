namespace MarqSpec.TradingCopilot.Infra;

/// <summary>
/// Everything that makes one environment differ from another (ADR-0030). Staging and production
/// are the same <see cref="EnvironmentStack"/> with different values here, never a second stack class.
/// </summary>
public sealed record EnvironmentStackProps
{
    /// <summary>
    /// The environment's short name — <c>production</c> or <c>staging</c>. It names Secrets Manager
    /// and SSM prefixes, log groups, and the Cloud Map namespace, so it has to be a DNS label.
    /// </summary>
    public required string EnvName { get; init; }

    /// <summary>
    /// The tasks' outbound path. Required with no default — ADR-0030 leaves NAT vs. public IP vs.
    /// VPC endpoints to the operator.
    /// </summary>
    public required OutboundPath OutboundPath { get; init; }

    /// <summary>
    /// The OTLP collector sidecar, or <c>null</c> for no telemetry at all. Null is not a degraded
    /// mode: it is compose with <c>Telemetry__OtlpEndpoint</c> unset.
    /// </summary>
    public TelemetryProps? Telemetry { get; init; }
}
