using System.Text.Json.Nodes;
using FluentAssertions;
using MarqSpec.TradingCopilot.Infra;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// The app task: the GHCR image by digest from a CloudFormation parameter, never :latest or a
/// floating branch tag (ADR-0030 decision 3, ADR-0018).
/// </summary>
public sealed class AppTaskTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    private static JsonObject AppContainer(Synthesised t) => t.Container("-app", "app");

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_image_is_built_from_the_digest_parameter_never_a_literal_or_a_dynamic_reference(string env)
    {
        var t = templates.For(env);
        var image = Synthesised.Text(AppContainer(t)["Image"]);

        image.Should().Contain($"{EnvironmentStack.AppImageRepository}@");
        image.Should().Contain("{\"Ref\":\"ImageDigest\"}");
        image.Should().NotContain("resolve:");
        image.Should().NotContain("sha256:", "a literal digest in the template is a deploy that cannot move");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void No_image_reference_anywhere_contains_latest_or_a_floating_tag(string env)
    {
        var t = templates.For(env);
        var images = t.Resources("AWS::ECS::TaskDefinition").Values
            .SelectMany(td => t.Properties(td)["ContainerDefinitions"]!.AsArray())
            .Select(c => Synthesised.Text(c!["Image"]))
            .ToList();

        images.Should().NotBeEmpty();
        foreach (var image in images)
        {
            image.Should().NotContain(":latest");
            image.Should().NotContain(":develop");
            image.Should().NotContain(":staging");
            image.Should().NotContain(":main");
            image.Should().Contain("@", "every image is a digest (ADR-0030 decision 3)");
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Digest_and_version_are_parameters_with_no_default(string env)
    {
        var t = templates.For(env);
        var digest = t.Parameter("ImageDigest").Should().NotBeNull().And.Subject!;
        digest["Type"]!.GetValue<string>().Should().Be("String");
        digest.ContainsKey("Default").Should().BeFalse("a default digest is a deploy that ran the wrong image silently");
        digest["AllowedPattern"]!.GetValue<string>().Should().Contain("sha256:");

        var version = t.Parameter("Version").Should().NotBeNull().And.Subject!;
        version["Type"]!.GetValue<string>().Should().Be("String");
        version.ContainsKey("Default").Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Hostname_and_root_domain_are_parameters_with_no_default(string env)
    {
        var t = templates.For(env);
        foreach (var name in new[] { "Hostname", "RootDomain" })
        {
            var parameter = t.Parameter(name).Should().NotBeNull().And.Subject!;
            parameter.ContainsKey("Default").Should().BeFalse($"{name} is operator-supplied (ADR-0030 decision 14)");
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_data_tier_is_a_parameter_with_allowed_values_and_no_default(string env)
    {
        var t = templates.For(env);
        var tier = t.Parameter("ProjectXDataTier").Should().NotBeNull().And.Subject!;
        tier["AllowedValues"]!.AsArray().Select(v => v!.GetValue<string>()).Should().BeEquivalentTo(["Simulated", "Live"]);
        tier.ContainsKey("Default").Should().BeFalse();
        Synthesised.Text(Synthesised.EnvironmentOf(AppContainer(t))["ProjectX__DataTier"]).Should().Be("{\"Ref\":\"ProjectXDataTier\"}");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Aspnet_environment_maps_the_R14_ladder(string env)
    {
        var t = templates.For(env);
        var expected = env == "production" ? "Production" : "Staging";
        Synthesised.EnvironmentOf(AppContainer(t))["ASPNETCORE_ENVIRONMENT"]!.GetValue<string>().Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Jwt_signing_key_is_injected_from_the_jwt_shell(string env)
    {
        var t = templates.For(env);
        var secrets = Synthesised.SecretsOf(AppContainer(t));
        t.SecretNameOf(secrets["Jwt__SigningKey"]).Should().Be($"trading-copilot/{env}/jwt");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Desired_count_is_at_least_one_with_a_circuit_breaker(string env)
    {
        var t = templates.For(env);
        var app = t.Resources("AWS::ECS::Service").Values
            .Single(s => t.Properties(s)["ServiceName"]?.GetValue<string>()?.EndsWith("-app", StringComparison.Ordinal) == true);
        var props = t.Properties(app);
        props["DesiredCount"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(1);
        props["DeploymentConfiguration"]!["DeploymentCircuitBreaker"]!["Enable"]!.GetValue<bool>().Should().BeTrue();
        props["DeploymentConfiguration"]!["DeploymentCircuitBreaker"]!["Rollback"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_ssm_history_comes_from_the_same_parameters(string env)
    {
        var t = templates.For(env);
        var parameters = t.Resources("AWS::SSM::Parameter").Values.Select(t.Properties)
            .ToDictionary(p => p["Name"]!.GetValue<string>(), p => Synthesised.Text(p["Value"]));
        parameters[$"/trading-copilot/{env}/image-digest"].Should().Be("{\"Ref\":\"ImageDigest\"}");
        parameters[$"/trading-copilot/{env}/version"].Should().Be("{\"Ref\":\"Version\"}");
    }

    [Fact]
    public void There_is_no_third_aws_environment_for_develop()
    {
        var app = new Amazon.CDK.App();
        _ = new EnvironmentStack(app, "trading-copilot-production", new EnvironmentStackProps
        {
            EnvName = "production",
            OutboundPath = EnvironmentTemplates.FixtureShape,
            ZoneMode = ZoneMode.Create,
            Telemetry = Synthesised.DeployedTelemetry,
        });
        _ = new EnvironmentStack(app, "trading-copilot-staging", new EnvironmentStackProps
        {
            EnvName = "staging",
            OutboundPath = EnvironmentTemplates.FixtureShape,
            ZoneMode = ZoneMode.Create,
            Telemetry = Synthesised.DeployedTelemetry,
        });

        var assembly = app.Synth();
        assembly.Stacks.Select(s => s.StackName).Should().BeEquivalentTo(
            ["trading-copilot-production", "trading-copilot-staging"]);
        assembly.Stacks.Select(s => s.StackName).Should().NotContain(n => n.Contains("develop", StringComparison.OrdinalIgnoreCase));
    }
}
