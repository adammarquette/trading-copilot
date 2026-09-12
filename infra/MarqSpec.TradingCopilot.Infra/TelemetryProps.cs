namespace MarqSpec.TradingCopilot.Infra;

/// <summary>
/// The OTLP collector sidecar in the app task (ADR-0030 decision 13). Presence on
/// <see cref="EnvironmentStackProps.Telemetry"/> is the switch — null is no sidecar and no
/// <c>Telemetry__*</c> key, which is today's compose behaviour when the endpoint is unset.
/// </summary>
public sealed record TelemetryProps
{
    /// <summary>
    /// The collector image, by digest. Defaults to <see cref="EnvironmentStack.OtelCollectorImage"/>.
    /// Override only to test a bump; bump the constant in a pull request that says why.
    /// </summary>
    public string CollectorImage { get; init; } = EnvironmentStack.OtelCollectorImage;

    /// <summary>
    /// Hard memory ceiling on the sidecar, MiB, inside the app task. The container is also
    /// <c>Essential=false</c>, so a collector that queues because CloudWatch is refusing cannot
    /// take the app — and flatten — down with it.
    /// </summary>
    public int MemoryLimitMiB { get; init; } = 128;
}
