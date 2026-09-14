using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using MarqSpec.TradingCopilot.Infra;

namespace MarqSpec.TradingCopilot.Infra.Tests;

/// <summary>
/// GitHub Actions deploys through OIDC and nothing else (ADR-0030 decision 4): one provider, two
/// roles, each trusting exactly the refs the pipeline runs from and nothing wider.
/// </summary>
public sealed class GitHubOidcStackTests
{
    // Literal, not interpolated from GitHubOidcStack.OidcSubjectRepository: a stack that emits the
    // name-only sub (the document that never matches this repository's tokens) must fail.
    private const string Repository = "repo:adammarquette@14438151/trading-copilot@1304474187";

    private static readonly Synthesised _stack = Synthesised.GitHubOidc();

    private static (JsonObject Role, JsonObject Statement) DeployRole(string name)
    {
        var role = _stack.Resources("AWS::IAM::Role").Values.Select(_stack.Properties)
            .Single(r => r["RoleName"]?.GetValue<string>() == name);
        var statement = role["AssumeRolePolicyDocument"]!["Statement"]!.AsArray().Should().ContainSingle().Which!.AsObject();
        return (role, statement);
    }

    [Fact]
    public void No_oidc_provider_resource_is_created_every_role_trusts_the_imported_one()
    {
        // MarqSpec.Mcp.TopstepX's topstepx-mcp-github-oidc stack already owns the only
        // AWS::IAM::OIDCProvider for this issuer in the shared account (gh#1201) — a second one
        // for the same URL collides at CloudFormation validation before any resource is created.
        var providers = _stack.Json["Resources"]!.AsObject()
            .Where(r => r.Value!["Type"]!.GetValue<string>().Contains("OIDCProvider", StringComparison.Ordinal))
            .ToList();
        providers.Should().BeEmpty("the provider is imported by ARN, never created by this stack");

        foreach (var name in new[] { "trading-copilot-GitHubDeploy-staging", "trading-copilot-GitHubDeploy-production" })
        {
            var (_, statement) = DeployRole(name);
            var federated = Synthesised.Text(statement["Principal"]!["Federated"]);
            federated.Should().Contain(":oidc-provider/token.actions.githubusercontent.com",
                "{0}: the principal is the existing TopstepX-owned provider, referenced by ARN", name);
            federated.Should().Contain("{\"Ref\":\"AWS::Partition\"}", "{0}: the imported ARN's partition is a pseudo-parameter", name);
            federated.Should().Contain("{\"Ref\":\"AWS::AccountId\"}", "{0}: the imported ARN's account is a pseudo-parameter", name);
        }
    }

    [Fact]
    public void The_staging_role_trusts_release_tags_and_main_exactly_and_nothing_wider()
    {
        var (_, statement) = DeployRole("trading-copilot-GitHubDeploy-staging");

        statement["Action"]!.GetValue<string>().Should().Be("sts:AssumeRoleWithWebIdentity");
        var condition = statement["Condition"]!;
        Synthesised.Text(condition["StringEquals"]!["token.actions.githubusercontent.com:aud"]).Should().Be("\"sts.amazonaws.com\"");
        condition["StringLike"]!["token.actions.githubusercontent.com:sub"]!.AsArray().Select(s => s!.GetValue<string>())
            .Should().BeEquivalentTo([$"{Repository}:ref:refs/tags/v*", $"{Repository}:ref:refs/heads/main"],
                "the tag is the release path and main is the workflow_dispatch rollback path, exactly");
    }

    [Fact]
    public void The_production_role_trusts_the_aws_production_environment_claim_only()
    {
        var (_, statement) = DeployRole("trading-copilot-GitHubDeploy-production");

        var condition = statement["Condition"]!;
        Synthesised.Text(condition["StringEquals"]!["token.actions.githubusercontent.com:aud"]).Should().Be("\"sts.amazonaws.com\"");
        Synthesised.Text(condition["StringEquals"]!["token.actions.githubusercontent.com:sub"]).Should().Be($"\"{Repository}:environment:aws-production\"");
        condition.AsObject().ContainsKey("StringLike").Should().BeFalse("no wildcard on the production trust");
    }

    [Fact]
    public void Neither_role_trusts_a_wildcard_repository_or_any_branch()
    {
        var text = _stack.Json.ToJsonString();
        text.Should().NotContain("repo:*");
        text.Should().NotContain("refs/heads/*");
        text.Should().NotContain(":ref:*");
        text.Should().NotContain("repo:adammarquette/trading-copilot:",
            "the name-only subject never matches a token this repository mints; a third role must not sneak it past the named tests");
    }

    [Theory]
    [InlineData("trading-copilot-GitHubDeploy-staging", "staging")]
    [InlineData("trading-copilot-GitHubDeploy-production", "production")]
    public void Each_role_reads_ssm_under_its_own_environment_only(string roleName, string env)
    {
        var (role, _) = DeployRole(roleName);
        var other = env == "staging" ? "production" : "staging";
        var policies = role["Policies"]!.AsArray().Select(p => Synthesised.Text(p!["PolicyDocument"])).ToList();
        var text = string.Join("\n", policies);

        text.Should().Contain("ssm:GetParameter").And.Contain($"parameter/trading-copilot/{env}/");
        text.Should().NotContain("ssm:PutParameter", "a put-parameter over a CloudFormation-managed resource is drift");
        text.Should().Contain("sts:AssumeRole").And.Contain("cdk-hnb659fds-", "the CDK bootstrap roles do the deploying");
        text.Should().NotContain($"/{other}/");
        text.Should().NotContain("\"Resource\":\"*\"");
        text.Should().NotContain("\"Action\":\"*\"");
    }

    [Fact]
    public void No_access_key_exists_anywhere()
    {
        _stack.Resources("AWS::IAM::AccessKey").Should().BeEmpty();
        _stack.Resources("AWS::IAM::User").Should().BeEmpty();
    }

    private static IReadOnlyList<string> Values(JsonNode? node) =>
        node is JsonArray array ? array.Select(v => v!.GetValue<string>()).ToList() : [node!.GetValue<string>()];

    /// <summary>
    /// Walks every role so a third role added later cannot arrive with a trust policy nobody
    /// asserted on. The token any public fork's workflow can mint carries the same issuer and
    /// audience; only the <c>sub</c> condition says whose run may assume the role.
    /// </summary>
    [Fact]
    public void Every_role_is_assumable_only_through_the_imported_provider_by_a_token_bound_to_aud_and_a_sub_of_this_repository()
    {
        var roles = _stack.Resources("AWS::IAM::Role");
        roles.Values.Select(r => _stack.Properties(r)["RoleName"]!.GetValue<string>())
            .Should().BeEquivalentTo(["trading-copilot-GitHubDeploy-staging", "trading-copilot-GitHubDeploy-production"], "the two roles the workflows assume, and no third");

        foreach (var (id, role) in roles)
        {
            var props = _stack.Properties(role);
            props["MaxSessionDuration"]!.GetValue<int>().Should().Be(3600, "{0}: one deploy, one hour", id);

            var statement = props["AssumeRolePolicyDocument"]!["Statement"]!.AsArray().Should().ContainSingle(id).Which!.AsObject();
            statement["Effect"]!.GetValue<string>().Should().Be("Allow");
            Values(statement["Action"]).Should().Equal("sts:AssumeRoleWithWebIdentity");
            Synthesised.LogicalIdOf(statement["Principal"]!["Federated"]).Should().BeNull(
                "{0}: the provider is imported by ARN, never a Ref/GetAtt to a resource this stack creates", id);
            Synthesised.Text(statement["Principal"]!["Federated"]).Should().Contain(":oidc-provider/token.actions.githubusercontent.com",
                "{0}: the principal is the existing TopstepX-owned provider, referenced by ARN", id);

            var condition = statement["Condition"]!.AsObject();
            condition.Select(op => op.Key).Should().BeSubsetOf(["StringEquals", "StringLike"], id);

            condition.SelectMany(op => op.Value!.AsObject().Select(claim => claim.Key))
                .Should().BeEquivalentTo(["token.actions.githubusercontent.com:aud", "token.actions.githubusercontent.com:sub"], id);
            Values(condition["StringEquals"]!["token.actions.githubusercontent.com:aud"]).Should().BeEquivalentTo(["sts.amazonaws.com"], "{0}: the audience is STS, exactly", id);

            var subs = condition
                .Select(op => op.Value!["token.actions.githubusercontent.com:sub"])
                .Where(sub => sub is not null)
                .SelectMany(Values)
                .ToList();
            subs.Should().NotBeEmpty(id);
            subs.Should().OnlyContain(s => s.StartsWith(Repository + ":", StringComparison.Ordinal), "{0}: every subject is pinned to this repository", id);
            // A wildcard may appear only as the last character of a tag-ref subject
            // (`…:ref:refs/tags/v*`). IndexOf rather than LastIndexOf, so the first `*` must
            // also be the last: exactly one, at the end.
            subs.Should().OnlyContain(
                s => !s.Contains('*')
                     || (s.StartsWith($"{Repository}:ref:refs/tags/", StringComparison.Ordinal) && s.IndexOf('*') == s.Length - 1),
                "{0}: no subject trusts every ref, every branch or every environment, and the only wildcard any of them may carry is a trailing one on a tag ref", id);
            subs.Should().OnlyContain(s => s.Contains(":ref:refs/", StringComparison.Ordinal) || s.Contains(":environment:", StringComparison.Ordinal),
                "{0}: a subject names a ref or an environment, the two claim shapes the pipeline runs under", id);
        }
    }

    [Fact]
    public void The_template_names_no_account_carries_no_thumbprint_and_builds_every_arn_from_pseudo_parameters()
    {
        var text = _stack.Json.ToJsonString();

        Regex.IsMatch(text, @"\b\d{12}\b").Should().BeFalse("no twelve-digit account id anywhere in the template");
        Regex.IsMatch(text, "[0-9a-f]{40}").Should().BeFalse("no certificate thumbprint anywhere in the template");

        // No thumbprint property to omit: the provider is imported by ARN (gh#1201), so this
        // stack has no AWS::IAM::OIDCProvider resource at all — the L1's ThumbprintList only
        // ever applied to a resource this stack creates, and it does not create one.
        _stack.Resources("AWS::IAM::OIDCProvider").Should().BeEmpty("the provider is imported, not created");

        var statements = _stack.PolicyStatements().ToList();
        statements.Should().NotBeEmpty();
        foreach (var statement in statements)
        {
            foreach (var resource in statement["Resource"] is JsonArray many ? many.Select(r => r!) : [statement["Resource"]!])
            {
                var arn = Synthesised.Text(resource);
                arn.Should().Contain("{\"Ref\":\"AWS::Partition\"}", "{0}: the partition is a pseudo-parameter", statement["Sid"]);
                arn.Should().Contain("{\"Ref\":\"AWS::AccountId\"}", "{0}: the account is a pseudo-parameter", statement["Sid"]);
            }
        }
    }

    [Fact]
    public void The_environment_the_production_role_trusts_is_one_bootstrap_sh_creates()
    {
        var (_, statement) = DeployRole("trading-copilot-GitHubDeploy-production");
        var sub = statement["Condition"]!["StringEquals"]!["token.actions.githubusercontent.com:sub"]!.GetValue<string>();
        var environment = sub[(sub.LastIndexOf(':') + 1)..];
        environment.Should().Be(GitHubOidcStack.ProductionEnvironment);

        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bootstrap.sh"));
        var list = Regex.Match(script, "^ENV_NAMES=\"([^\"]*)\"", RegexOptions.Multiline);
        list.Success.Should().BeTrue("bootstrap.sh declares the environments it creates on one ENV_NAMES=\"…\" line");
        list.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Should().Contain(environment, "the environment the production role trusts must be one bootstrap.sh creates with a required reviewer");
    }
}
