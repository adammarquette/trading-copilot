using FluentAssertions;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// One ALB per environment is the edge (ADR-0030 decision 2): TLS at 443, HTTP redirects, /health probe.
/// </summary>
public sealed class EdgeTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_only_listener_with_a_certificate_is_443_and_80_redirects(string env)
    {
        var t = templates.For(env);
        var listeners = t.Resources("AWS::ElasticLoadBalancingV2::Listener").Values.Select(t.Properties).ToList();
        listeners.Should().HaveCount(2);

        var https = listeners.Single(l => l["Port"]!.GetValue<int>() == 443);
        https["Protocol"]!.GetValue<string>().Should().Be("HTTPS");
        https["Certificates"]!.AsArray().Should().HaveCount(1);

        var http = listeners.Single(l => l["Port"]!.GetValue<int>() == 80);
        http["Protocol"]!.GetValue<string>().Should().Be("HTTP");
        var redirect = http["DefaultActions"]!.AsArray().Should().ContainSingle().Which!;
        redirect["Type"]!.GetValue<string>().Should().Be("redirect");
        redirect["RedirectConfig"]!["Protocol"]!.GetValue<string>().Should().Be("HTTPS");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Https_answers_404_by_default_and_forwards_the_hostname_parameter(string env)
    {
        var t = templates.For(env);
        var https = t.Resources("AWS::ElasticLoadBalancingV2::Listener").Values.Select(t.Properties)
            .Single(l => l["Port"]!.GetValue<int>() == 443);

        var @default = https["DefaultActions"]!.AsArray().Should().ContainSingle().Which!;
        @default["Type"]!.GetValue<string>().Should().Be("fixed-response");
        @default["FixedResponseConfig"]!["StatusCode"]!.GetValue<string>().Should().Be("404");

        var rule = t.Resources("AWS::ElasticLoadBalancingV2::ListenerRule").Values.Select(t.Properties)
            .Should().ContainSingle().Which;
        var condition = rule["Conditions"]!.AsArray().Should().ContainSingle().Which!;
        condition["Field"]!.GetValue<string>().Should().Be("host-header");
        Synthesised.Text(condition["HostHeaderConfig"]!["Values"]).Should().Contain("Hostname");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_target_group_probes_health_on_8080_every_30_seconds_for_a_200(string env)
    {
        var t = templates.For(env);
        var (_, tg) = t.Single("AWS::ElasticLoadBalancingV2::TargetGroup");
        var props = t.Properties(tg);

        props["Protocol"]!.GetValue<string>().Should().Be("HTTP");
        props["Port"]!.GetValue<int>().Should().Be(8080);
        props["TargetType"]!.GetValue<string>().Should().Be("ip");
        props["HealthCheckPath"]!.GetValue<string>().Should().Be("/health");
        props["Matcher"]!["HttpCode"]!.GetValue<string>().Should().Be("200");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_hosted_zone_is_created_in_stack_from_the_root_domain_parameter(string env)
    {
        var t = templates.For(env);
        var (_, zone) = t.Single("AWS::Route53::HostedZone");
        Synthesised.Text(t.Properties(zone)["Name"]).Should().Contain("RootDomain");
        t.Json.ToJsonString().Should().NotContain("HostedZoneFromLookup", "synth must not look up a zone (no invented id)");
    }
}
