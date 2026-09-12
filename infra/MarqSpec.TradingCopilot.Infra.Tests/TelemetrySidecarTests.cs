using FluentAssertions;
using MarqSpec.TradingCopilot.Infra;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// The OTLP sidecar is present, not essential, bound to loopback, and carries the checked-in config.
/// </summary>
public sealed class TelemetrySidecarTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_app_task_carries_a_non_essential_sidecar_by_digest(string env)
    {
        var t = templates.For(env);
        var sidecar = t.Container("-app", "otel-collector");
        sidecar["Essential"]!.GetValue<bool>().Should().BeFalse();
        sidecar["Image"]!.GetValue<string>().Should().Be(EnvironmentStack.OtelCollectorImage);
        sidecar["Image"]!.GetValue<string>().Should().Contain("@sha256:");
        sidecar.ContainsKey("PortMappings").Should().BeFalse("publishing 4317 would expose an unauthenticated receiver");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_app_exports_to_the_loopback_and_the_sidecar_carries_the_checked_in_config(string env)
    {
        var t = templates.For(env);
        var app = t.Container("-app", "app");
        Synthesised.EnvironmentOf(app)["Telemetry__OtlpEndpoint"]!.GetValue<string>()
            .Should().Be(EnvironmentStack.OtelLoopbackEndpoint);

        var sidecar = t.Container("-app", "otel-collector");
        var config = Synthesised.EnvironmentOf(sidecar)["OTEL_COLLECTOR_CONFIG"]!.GetValue<string>();
        config.Should().Be(CollectorConfiguration.Yaml);
        config.Should().Contain("127.0.0.1:4317");
        config.Should().NotContain("0.0.0.0");
    }

    [Fact]
    public void Absent_telemetry_omits_the_sidecar_and_the_endpoint()
    {
        var t = Synthesised.Environment("staging", EnvironmentTemplates.FixtureShape, telemetry: null);
        t.TaskDefinition("-app").Containers.Should().ContainSingle()
            .Which!["Name"]!.GetValue<string>().Should().Be("app");
        Synthesised.EnvironmentOf(t.Container("-app", "app")).Keys.Should().NotContain("Telemetry__OtlpEndpoint");
    }
}
