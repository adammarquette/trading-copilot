using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace MarqSpec.TradingCopilot.Infra;

/// <summary>
/// The two deploy roles, trusting an OIDC provider this stack imports rather than creates
/// (ADR-0030 decision 4 / 8 / 11; gh#1201). No long-lived AWS key exists in GitHub, in a
/// workflow, or anywhere else: each run presents a token bound to a ref or an environment, and
/// each role trusts exactly the claims the pipeline runs under.
/// </summary>
/// <remarks>
/// Shape is the TopstepX <c>GitHubOidcStack</c> in MarqSpec.Mcp.TopstepX (pattern library), with
/// two account-global collisions deliberately NOT copied (gh#1201): the role names are
/// project-scoped (<c>trading-copilot-GitHubDeploy-&lt;env&gt;</c>, not TopstepX's
/// <c>GitHubDeploy-&lt;env&gt;</c>), and the OIDC provider is imported by ARN rather than
/// created, because TopstepX's <c>topstepx-mcp-github-oidc</c> stack already owns the only
/// provider for this issuer in the shared account. This product's repository, environment name,
/// and SSM prefix are otherwise ours. No account IDs, hostnames, Cognito, or MCP bits copied from
/// that repo.
/// <para>
/// The GitHub-side settings this pairs with — the <c>production</c> and <c>aws-production</c>
/// environments and their reviewer rules — are created by <c>scripts/bootstrap.sh</c> (console
/// actions CI cannot do). The template test reads that script's <c>ENV_NAMES</c> so the production
/// trust cannot drift from the setting the script creates.
/// </para>
/// </remarks>
public sealed class GitHubOidcStack : Stack
{
    public const string Repository = "adammarquette/trading-copilot";
    public const string ProductionEnvironment = "aws-production";

    /// <summary>
    /// The repository segment GitHub puts in this repo's Actions OIDC <c>sub</c>. This repository
    /// was created 2026-07-17, after the 2026-07-15 immutable-subject cutoff, so tokens carry
    /// <c>owner@id/name@id</c> and a name-only <c>repo:owner/name</c> trust never matches. Read
    /// back from <c>GET /repos/…/actions/oidc/customization/sub</c> <c>sub_claim_prefix</c>
    /// (gh#1187). These are GitHub ids, not an AWS account id.
    /// </summary>
    public const string OidcSubjectRepository = "adammarquette@14438151/trading-copilot@1304474187";

    private const string Issuer = "token.actions.githubusercontent.com";

    public GitHubOidcStack(Construct scope, string id, StackProps? props = null)
        : base(scope, id, props)
    {
        Amazon.CDK.Tags.Of(this).Add("Project", "trading-copilot");

        // IMPORT, never create (gh#1201). An IAM OIDC provider is unique per URL per account, and
        // MarqSpec.Mcp.TopstepX's topstepx-mcp-github-oidc stack already owns the only one for this
        // issuer in the shared account (045296582762) — a second AWS::IAM::OIDCProvider for the
        // same URL is refused by CloudFormation before any resource is created. The ARN is built
        // from pseudo-parameters rather than a literal or a cross-stack lookup: ADR-0030 decision
        // 14 forbids inventing an account id, and this stack must not depend on TopstepX's stack
        // outputs or exports to stay independently deployable.
        var providerArn = $"arn:{Aws.PARTITION}:iam::{Aws.ACCOUNT_ID}:oidc-provider/{Issuer}";
        var provider = OpenIdConnectProvider.FromOpenIdConnectProviderArn(this, "GitHubProvider", providerArn);

        // Staging: the release path (a v* tag) AND the workflow_dispatch redeploy/rollback path,
        // which runs on main exactly. Trusting the tag alone would refuse every rollback.
        DeployRole(provider, "staging", new Dictionary<string, object>
        {
            ["StringEquals"] = new Dictionary<string, object> { [$"{Issuer}:aud"] = "sts.amazonaws.com" },
            ["StringLike"] = new Dictionary<string, object>
            {
                [$"{Issuer}:sub"] = new[]
                {
                    $"repo:{OidcSubjectRepository}:ref:refs/tags/v*",
                    $"repo:{OidcSubjectRepository}:ref:refs/heads/main",
                },
            },
        });

        // Production: the environment claim. Both production jobs declare
        // `environment: aws-production`, and the reviewer rule on that setting is the approval
        // (ADR-0030 decision 8). Neither role binds job_workflow_ref: that claim carries the
        // workflow file path and the ref it ran at, so it changes on every rename and would need
        // its own wildcard; the sub already binds the run to a tag, a branch, or an environment.
        DeployRole(provider, "production", new Dictionary<string, object>
        {
            ["StringEquals"] = new Dictionary<string, object>
            {
                [$"{Issuer}:aud"] = "sts.amazonaws.com",
                [$"{Issuer}:sub"] = $"repo:{OidcSubjectRepository}:environment:{ProductionEnvironment}",
            },
        });
    }

    private void DeployRole(IOpenIdConnectProvider provider, string envName, IDictionary<string, object> conditions)
    {
        // Pseudo-parameters rather than this stack's Account and Region: under a concrete
        // environment those resolve to literals, and ADR-0030 decision 14 forbids inventing an
        // account id. Built from AWS::AccountId and AWS::Region, the same template deploys into
        // whichever account the credentials belong to.
        var (partition, account, region) = (Aws.PARTITION, Aws.ACCOUNT_ID, Aws.REGION);
        var statements = new List<PolicyStatement>
        {
            new(new PolicyStatementProps
            {
                Sid = "AssumeCdkBootstrapRoles",
                Actions = ["sts:AssumeRole"],
                Resources = [$"arn:{partition}:iam::{account}:role/cdk-hnb659fds-*-role-{account}-{region}"],
            }),
            // The EnvironmentStack owns the two SSM history parameters and writes them from its
            // own CloudFormation parameters on every deploy, so the pipeline only READS them
            // (ADR-0030 decision 5). A put-parameter over a CloudFormation-managed resource is
            // drift the next stack update writes back.
            new(new PolicyStatementProps
            {
                Sid = "ReadDeploymentHistory",
                Actions = ["ssm:GetParameter"],
                Resources = [$"arn:{partition}:ssm:{region}:{account}:parameter/trading-copilot/{envName}/*"],
            }),
        };

        _ = new Role(this, $"{envName}DeployRole", new RoleProps
        {
            // Project-scoped (gh#1201): IAM role names are unique per account, and the shared
            // account already has TopstepX's own GitHubDeploy-<env> roles.
            RoleName = $"trading-copilot-GitHubDeploy-{envName}",
            Description = $"GitHub Actions deploys the {envName} environment through OIDC (ADR-0030); no long-lived key exists.",
            AssumedBy = new FederatedPrincipal(provider.OpenIdConnectProviderArn, conditions, "sts:AssumeRoleWithWebIdentity"),
            MaxSessionDuration = Duration.Hours(1),
            InlinePolicies = new Dictionary<string, PolicyDocument>
            {
                ["deploy"] = new(new PolicyDocumentProps { Statements = [.. statements] }),
            },
        });
    }
}
