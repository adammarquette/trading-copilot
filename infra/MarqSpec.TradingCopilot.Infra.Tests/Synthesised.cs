using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.CDK;
using Amazon.CDK.Assertions;
using MarqSpec.TradingCopilot.Infra;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// One synthesised stack: the CDK <see cref="Template"/> plus the same template as a
/// <see cref="JsonNode"/> for questions the matchers cannot ask.
/// </summary>
public sealed record Synthesised(Template Template, JsonObject Json)
{
    public static TelemetryProps DeployedTelemetry => new();

    public static Synthesised Environment(
        string envName,
        OutboundPath outboundPath,
        TelemetryProps? telemetry,
        string? stackId = null)
    {
        var app = new App();
        var stack = new EnvironmentStack(app, stackId ?? $"trading-copilot-{envName}", new EnvironmentStackProps
        {
            EnvName = envName,
            OutboundPath = outboundPath,
            Telemetry = telemetry,
        });
        return Of(stack);
    }

    public static Synthesised Production(OutboundPath outboundPath, TelemetryProps? telemetry = null) =>
        Environment("production", outboundPath, telemetry ?? DeployedTelemetry);

    public static Synthesised Staging(OutboundPath outboundPath, TelemetryProps? telemetry = null) =>
        Environment("staging", outboundPath, telemetry ?? DeployedTelemetry);

    public static Synthesised GitHubOidc() =>
        Of(new GitHubOidcStack(new App(), "trading-copilot-github-oidc"));

    public static Synthesised Of(Stack stack)
    {
        var template = Template.FromStack(stack);
        var json = JsonSerializer.SerializeToNode(template.ToJSON())?.AsObject()
            ?? throw new InvalidOperationException("The synthesised template serialised to null.");
        return new Synthesised(template, json);
    }

    public IReadOnlyDictionary<string, JsonObject> Resources(string type) =>
        Json["Resources"]!.AsObject()
            .Where(r => r.Value!["Type"]!.GetValue<string>() == type)
            .ToDictionary(r => r.Key, r => r.Value!.AsObject());

    public (string LogicalId, JsonObject Resource) Single(string type)
    {
        var all = Resources(type);
        return all.Count == 1
            ? (all.Keys.Single(), all.Values.Single())
            : throw new InvalidOperationException($"Expected exactly one {type}, found {all.Count}: {string.Join(", ", all.Keys)}.");
    }

    public JsonObject Properties(JsonObject resource) => resource["Properties"]!.AsObject();

    public JsonObject? Parameter(string name) => Json["Parameters"]?[name]?.AsObject();

    public (string LogicalId, JsonObject Resource) SecurityGroup(string description)
    {
        var matches = Resources("AWS::EC2::SecurityGroup")
            .Where(sg => Properties(sg.Value)["GroupDescription"]?.GetValue<string>() == description)
            .ToList();
        return matches.Count == 1
            ? (matches[0].Key, matches[0].Value)
            : throw new InvalidOperationException($"Expected one security group described '{description}', found {matches.Count}.");
    }

    public static string? LogicalIdOf(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        if (obj["Ref"] is JsonValue @ref)
        {
            return @ref.GetValue<string>();
        }

        if (obj["Fn::GetAtt"] is JsonArray getAtt)
        {
            return getAtt[0]!.GetValue<string>();
        }

        return null;
    }

    public static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";

    public (string LogicalId, JsonObject TaskDefinition, JsonArray Containers) TaskDefinition(string familySuffix)
    {
        var matches = Resources("AWS::ECS::TaskDefinition")
            .Where(td => Properties(td.Value)["Family"]?.GetValue<string>()?.EndsWith(familySuffix, StringComparison.Ordinal) == true)
            .ToList();
        if (matches.Count != 1)
        {
            throw new InvalidOperationException($"Expected one task definition with family ending '{familySuffix}', found {matches.Count}.");
        }

        var (id, resource) = (matches[0].Key, matches[0].Value);
        return (id, resource, Properties(resource)["ContainerDefinitions"]!.AsArray());
    }

    public JsonObject Container(string familySuffix, string containerName)
    {
        var (_, _, containers) = TaskDefinition(familySuffix);
        var matches = containers.Where(c => c!["Name"]!.GetValue<string>() == containerName).ToList();
        return matches.Count == 1
            ? matches[0]!.AsObject()
            : throw new InvalidOperationException(
                $"Expected one container named '{containerName}' in the '{familySuffix}' task, found {matches.Count}.");
    }

    public static IReadOnlyDictionary<string, JsonNode?> EnvironmentOf(JsonObject container) =>
        (container["Environment"]?.AsArray() ?? [])
            .ToDictionary(e => e!["Name"]!.GetValue<string>(), e => e!["Value"]);

    public static IReadOnlyDictionary<string, JsonNode?> SecretsOf(JsonObject container) =>
        (container["Secrets"]?.AsArray() ?? [])
            .ToDictionary(s => s!["Name"]!.GetValue<string>(), s => s!["ValueFrom"]);

    public string SecretNameOf(JsonNode? valueFrom)
    {
        var parts = valueFrom?["Fn::Join"]?[1]?.AsArray()
            ?? throw new InvalidOperationException($"Not a JSON-key valueFrom: {Text(valueFrom)}");
        var logicalId = parts.Select(LogicalIdOf).FirstOrDefault(id => id is not null)
            ?? throw new InvalidOperationException($"No Ref inside {Text(valueFrom)}");
        return Properties(Resources("AWS::SecretsManager::Secret")[logicalId])["Name"]!.GetValue<string>();
    }

    /// <summary>Every statement of every inline <c>AWS::IAM::Policy</c> and every role's embedded policies.</summary>
    public IEnumerable<JsonObject> PolicyStatements()
    {
        foreach (var policy in Resources("AWS::IAM::Policy").Values)
        {
            foreach (var statement in Properties(policy)["PolicyDocument"]!["Statement"]!.AsArray())
            {
                yield return statement!.AsObject();
            }
        }

        foreach (var role in Resources("AWS::IAM::Role").Values)
        {
            foreach (var policy in Properties(role)["Policies"]?.AsArray() ?? [])
            {
                foreach (var statement in policy!["PolicyDocument"]!["Statement"]!.AsArray())
                {
                    yield return statement!.AsObject();
                }
            }
        }
    }
}
