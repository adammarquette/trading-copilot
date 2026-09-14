using Amazon.CDK;
using FluentAssertions;
using MarqSpec.TradingCopilot.Infra;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// The tasks' outbound path is the fork ADR-0030 leaves to the operator. Nothing here chooses:
/// every shape the property admits is asserted, and the one thing the stack refuses is a props
/// record that named none.
/// </summary>
public sealed class OutboundPathTests
{
    [Fact]
    public void A_stack_cannot_be_synthesised_without_naming_a_shape()
    {
        var app = new App();
        var act = () => new EnvironmentStack(app, "trading-copilot-staging", new EnvironmentStackProps
        {
            EnvName = "staging",
            OutboundPath = default,
            ZoneMode = ZoneMode.Create,
        });

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*OutboundPath*");
    }

    [Theory]
    [InlineData(OutboundPath.PublicIpPerTask, "ENABLED", 0, false)]
    [InlineData(OutboundPath.NatGateway, "DISABLED", 1, false)]
    [InlineData(OutboundPath.VpcEndpointsWithPublicIp, "ENABLED", 0, true)]
    [InlineData(OutboundPath.VpcEndpointsWithNatGateway, "DISABLED", 1, true)]
    public void Each_shape_produces_exactly_the_address_the_nat_and_the_endpoints_it_names(
        OutboundPath shape, string assignPublicIp, int natGateways, bool endpoints)
    {
        var t = Synthesised.Staging(shape);

        var services = t.Resources("AWS::ECS::Service").Values.ToList();
        services.Should().HaveCount(2);
        foreach (var service in services)
        {
            var awsvpc = t.Properties(service)["NetworkConfiguration"]!["AwsvpcConfiguration"]!;
            awsvpc["AssignPublicIp"]!.GetValue<string>().Should().Be(assignPublicIp);
        }

        t.Resources("AWS::EC2::NatGateway").Should().HaveCount(natGateways);

        var interfaceEndpoints = t.Resources("AWS::EC2::VPCEndpoint").Values
            .Count(e => t.Properties(e)["VpcEndpointType"]?.GetValue<string>() == "Interface");
        (interfaceEndpoints > 0).Should().Be(endpoints);
    }
}
