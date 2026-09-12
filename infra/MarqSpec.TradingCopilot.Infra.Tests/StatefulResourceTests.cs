using System.Text.Json;
using FluentAssertions;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// Stateful resources carry RETAIN on both delete and replace. Secret shells hold empty JSON keys,
/// never values, and nothing in the template reads like a credential.
/// </summary>
public sealed class StatefulResourceTests(EnvironmentTemplates templates) : IClassFixture<EnvironmentTemplates>
{
    private static readonly string[] _statefulTypes =
    [
        "AWS::EFS::FileSystem",
        "AWS::EFS::AccessPoint",
        "AWS::SecretsManager::Secret",
        "AWS::Logs::LogGroup",
    ];

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Every_stateful_resource_is_retained_on_delete_and_on_replace(string env)
    {
        var t = templates.For(env);
        foreach (var type in _statefulTypes)
        {
            var resources = t.Resources(type);
            resources.Should().NotBeEmpty($"the stack owns at least one {type}");
            foreach (var (id, resource) in resources)
            {
                resource["DeletionPolicy"]?.GetValue<string>().Should().Be("Retain", $"{id} ({type})");
                resource["UpdateReplacePolicy"]?.GetValue<string>().Should().Be("Retain", $"{id} ({type})");
            }
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void Secret_shells_exist_under_the_environment_prefix_and_carry_no_value(string env)
    {
        var t = templates.For(env);
        var secrets = t.Resources("AWS::SecretsManager::Secret").Values.Select(t.Properties).ToList();

        secrets.Select(s => s["Name"]!.GetValue<string>())
            .Should().BeEquivalentTo(
            [
                $"trading-copilot/{env}/postgres",
                $"trading-copilot/{env}/jwt",
                $"trading-copilot/{env}/bootstrap",
                $"trading-copilot/{env}/projectx",
                $"trading-copilot/{env}/providers",
                $"trading-copilot/{env}/llm",
                $"trading-copilot/{env}/pushover",
                $"trading-copilot/{env}/checkin",
            ]);

        foreach (var secret in secrets)
        {
            secret.ContainsKey("GenerateSecretString").Should().BeFalse("a generated value is a credential nobody wrote down");
            var shell = JsonDocument.Parse(secret["SecretString"]!.GetValue<string>()).RootElement;
            shell.EnumerateObject().Should().NotBeEmpty();
            shell.EnumerateObject().Should().OnlyContain(p => p.Value.GetString() == string.Empty);
        }
    }

    [Theory]
    [MemberData(nameof(EnvironmentTemplates.Both), MemberType = typeof(EnvironmentTemplates))]
    public void There_is_no_cognito_and_no_invented_account_or_hostname(string env)
    {
        var t = templates.For(env);
        t.Resources("AWS::Cognito::UserPool").Should().BeEmpty("app auth stays JWT (R-18, ADR-0030 decision 7)");
        t.Resources("AWS::Cognito::UserPoolClient").Should().BeEmpty();

        var text = t.Json.ToJsonString();
        text.Should().NotContain("marqspec.com");
        text.Should().NotContain("topstepx");
        text.Should().NotContain("123456789012");
    }
}
