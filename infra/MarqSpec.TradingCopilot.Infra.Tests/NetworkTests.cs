using FluentAssertions;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// Who may reach each port is answered on the security groups, not in a compose comment.
/// </summary>
public sealed class NetworkTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Four_named_groups_exist(string env)
    {
        var t = templates.For(env);
        t.SecurityGroup($"trading-copilot/{env}/alb");
        t.SecurityGroup($"trading-copilot/{env}/app");
        t.SecurityGroup($"trading-copilot/{env}/postgres");
        t.SecurityGroup($"trading-copilot/{env}/efs");
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void The_alb_admits_443_and_80_from_the_internet(string env)
    {
        var t = templates.For(env);
        var (_, alb) = t.SecurityGroup($"trading-copilot/{env}/alb");
        var ingress = t.Properties(alb)["SecurityGroupIngress"]!.AsArray();
        ingress.Select(r => r!["FromPort"]!.GetValue<int>()).Should().BeEquivalentTo([80, 443]);
        ingress.Should().OnlyContain(r => r!["CidrIp"]!.GetValue<string>() == "0.0.0.0/0");
    }
}
