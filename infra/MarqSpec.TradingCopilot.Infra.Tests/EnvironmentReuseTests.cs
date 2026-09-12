using System.Text.Json.Nodes;
using FluentAssertions;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// Staging and production are the same template with a different environment name (ADR-0030).
/// Synthesised under one stack id so logical-id hashes do not count as a second class.
/// </summary>
public sealed class EnvironmentReuseTests
{
    private static readonly Synthesised _productionShape =
        Synthesised.Environment("production", EnvironmentTemplates.FixtureShape, Synthesised.DeployedTelemetry, "trading-copilot");

    private static readonly Synthesised _stagingShape =
        Synthesised.Environment("staging", EnvironmentTemplates.FixtureShape, Synthesised.DeployedTelemetry, "trading-copilot");

    [Fact]
    public void The_two_stacks_have_the_same_resource_types()
    {
        var production = _productionShape.Json["Resources"]!.AsObject()
            .ToDictionary(r => r.Key, r => r.Value!["Type"]!.GetValue<string>());
        var staging = _stagingShape.Json["Resources"]!.AsObject()
            .ToDictionary(r => r.Key, r => r.Value!["Type"]!.GetValue<string>());

        production.Keys.Should().BeEquivalentTo(staging.Keys);
        foreach (var key in production.Keys)
        {
            production[key].Should().Be(staging[key], key);
        }
    }

    [Fact]
    public void The_two_stacks_differ_only_where_the_environment_name_appears()
    {
        var production = Flatten(Normalise(_productionShape.Json, "production", "Production"));
        var staging = Flatten(Normalise(_stagingShape.Json, "staging", "Staging"));

        var unexplained = production.Keys.Union(staging.Keys)
            .Where(path => !production.TryGetValue(path, out var p) || !staging.TryGetValue(path, out var s) || p != s)
            .ToList();

        unexplained.Should().BeEmpty("every remaining difference is a second stack class in disguise:\n" + string.Join('\n', unexplained));
    }

    private static JsonNode Normalise(JsonObject template, string envName, string aspnetName)
    {
        var json = template.ToJsonString()
            .Replace(envName, "ENV", StringComparison.Ordinal)
            .Replace(aspnetName, "ASPNET", StringComparison.Ordinal);
        return JsonNode.Parse(json)!;
    }

    private static Dictionary<string, string> Flatten(JsonNode node, string prefix = "")
    {
        var result = new Dictionary<string, string>();
        Walk(node, prefix, result);
        return result;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    Walk(value, path.Length == 0 ? key : $"{path}.{key}", into);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], $"{path}[{i}]", into);
                }

                break;
            default:
                into[path] = node?.ToJsonString() ?? "null";
                break;
        }
    }
}
