using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace MarqSpec.TradingCopilot.Infra;

/// <summary>
/// The GitHub OIDC provider and the two deploy roles (ADR-0030 decision 4 / 8 / 11). No long-lived
/// AWS key exists in GitHub, in a workflow, or anywhere else: each run presents a token bound to a
/// ref or an environment, and each role trusts exactly the claims the pipeline runs under.
/// </summary>
/// <remarks>
/// Shape is the TopstepX <c>GitHubOidcStack</c> in MarqSpec.Mcp.TopstepX (pattern library). This
/// product's repository, environment name, and SSM prefix are ours. No account IDs, hostnames,
/// Cognito, or MCP bits copied from that repo.
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

        // The L1 rather than the L2 OpenIdConnectProvider: the L2 is a custom resource — a Lambda
        // and a role with wildcard permissions — from before CloudFormation supported the type
        // natively.
        //
        // NO THUMBPRINT LIST. AWS has verified GitHub's issuer against its own trusted CA library
        // since 2023 and ignores the property for it. A 40-hex-character literal nobody re-verifies
        // reads exactly like a current one after the CA rotates; the template test refuses one.
        var provider = new CfnOIDCProvider(this, "GitHub", new CfnOIDCProviderProps
        {
            Url = $"https://{Issuer}",
            ClientIdList = ["sts.amazonaws.com"],
        });

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

    private void DeployRole(CfnOIDCProvider provider, string envName, IDictionary<string, object> conditions)
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
            RoleName = $"GitHubDeploy-{envName}",
            Description = $"GitHub Actions deploys the {envName} environment through OIDC (ADR-0030); no long-lived key exists.",
            AssumedBy = new FederatedPrincipal(provider.AttrArn, conditions, "sts:AssumeRoleWithWebIdentity"),
            MaxSessionDuration = Duration.Hours(1),
            InlinePolicies = new Dictionary<string, PolicyDocument>
            {
                ["deploy"] = new(new PolicyDocumentProps { Statements = [.. statements] }),
            },
        });
    }
}
