using FluentAssertions;
using MarqSpec.TradingCopilot.Infra;

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
    public void The_a_record_name_is_absolute_and_does_not_repeat_the_zone(string env)
    {
        var t = templates.For(env);
        var (_, record) = t.Single("AWS::Route53::RecordSet");
        var name = t.Properties(record)["Name"];

        // RecordName is relative to the zone unless it ends with ".": Hostname is already the
        // full FQDN, so the record must be made absolute rather than let CDK append the zone
        // name again (gh#1205). A correct join is exactly [{Ref: Hostname}, "."] — anything
        // that also references the zone (RootDomain here; a literal zone name under Lookup,
        // asserted separately below) means the zone got appended a second time.
        var parts = name!["Fn::Join"]![1]!.AsArray();
        parts.Should().HaveCount(2, "the join must be just the Hostname ref plus the absolute-name dot, not a second zone reference");
        parts[0]!["Ref"]!.GetValue<string>().Should().Be("Hostname");
        parts[1]!.GetValue<string>().Should().Be(".");
    }

    [Fact]
    public void The_a_record_name_under_lookup_is_the_hostname_alone_not_suffixed_with_the_looked_up_zone()
    {
        var t = Synthesised.StagingLookup(EnvironmentTemplates.FixtureShape);
        var (_, record) = t.Single("AWS::Route53::RecordSet");
        var name = Synthesised.Text(t.Properties(record)["Name"]);

        name.Should().NotContain("staging.marqspec.com", "the looked-up zone name must not be appended to the already-fully-qualified Hostname (gh#1205)");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_hosted_zone_is_created_in_stack_from_the_root_domain_parameter(string env)
    {
        var t = templates.For(env);
        var (_, zone) = t.Single("AWS::Route53::HostedZone");
        Synthesised.Text(t.Properties(zone)["Name"]).Should().Contain("RootDomain");
        t.Json.ToJsonString().Should().NotContain("HostedZoneFromLookup", "the Create fixture must not look up a zone (CI synth --no-lookups)");
        t.Json.ToJsonString().Should().Contain("HostedZoneNameServers", "Create still emits NS for a registrar that does not already delegate");
    }

    [Fact]
    public void Lookup_creates_no_zone_and_emits_no_name_servers()
    {
        var t = Synthesised.StagingLookup(EnvironmentTemplates.FixtureShape);
        t.Resources("AWS::Route53::HostedZone").Should().BeEmpty(
            "staging.marqspec.com zone Z00545362JA49XMTT3U7Q already exists and Cloudflare already delegates "
            + "to its NS. Create would mint a second zone and undo that swap (gh#1188)");
        t.Json.ToJsonString().Should().NotContain("HostedZoneNameServers", "Lookup must not ask the operator to re-delegate NS they already swapped");
    }

    [Fact]
    public void Lookup_without_a_synth_time_root_domain_is_refused()
    {
        var act = () => Synthesised.Environment(
            "staging",
            EnvironmentTemplates.FixtureShape,
            Synthesised.DeployedTelemetry,
            zoneMode: ZoneMode.Lookup,
            env: Synthesised.TestEnv);

        act.Should().Throw<ArgumentException>().WithMessage("*RootDomain*");
    }

    [Fact]
    public void Lookup_without_account_and_region_is_refused()
    {
        var act = () => Synthesised.Environment(
            "staging",
            EnvironmentTemplates.FixtureShape,
            Synthesised.DeployedTelemetry,
            zoneMode: ZoneMode.Lookup,
            rootDomain: "staging.marqspec.com");

        act.Should().Throw<ArgumentException>().WithMessage("*account*");
    }

    [Fact]
    public void A_stack_cannot_be_synthesised_without_naming_a_zone_mode()
    {
        var act = () => new EnvironmentStack(new Amazon.CDK.App(), "trading-copilot-staging", new EnvironmentStackProps
        {
            EnvName = "staging",
            OutboundPath = EnvironmentTemplates.FixtureShape,
            ZoneMode = default,
        });

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*ZoneMode*");
    }
}
