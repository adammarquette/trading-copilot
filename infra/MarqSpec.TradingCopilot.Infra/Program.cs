using Amazon.CDK;
using MarqSpec.TradingCopilot.Infra;

// The CDK app (ADR-0030): the same EnvironmentStack twice, and the OIDC stack that lets GitHub
// Actions deploy them. Run through infra/cdk.json; never by hand. Do not cdk deploy from a
// workflow child's checkout — first apply is the operator's (gh#1188).
var app = new App();

Amazon.CDK.Tags.Of(app).Add("Project", "trading-copilot");

// NOT DECIDED HERE. ADR-0030 leaves NAT vs. public IP vs. VPC endpoints to the operator. The app
// refuses to synthesise until one shape is named. CI synthesises every value. When the operator
// decides, the choice becomes a literal here.
var outbound = Enum.TryParse<OutboundPath>(RequiredContext("outbound"), ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
    ? parsed
    : throw new InvalidOperationException(
        $"Context value 'outbound' must be one of {string.Join(", ", Enum.GetNames<OutboundPath>())} — the tasks' outbound path is " +
        "undecided (ADR-0030) and nothing here chooses for the operator.");

// No Env.Account / Env.Region: the stacks are environment-agnostic. Account id and region are
// operator-supplied at apply time (ADR-0030 decision 14). A placeholder in this file would be an
// invented inventory.

_ = new EnvironmentStack(app, "trading-copilot-production", new EnvironmentStackProps
{
    EnvName = "production",
    OutboundPath = outbound,
    Telemetry = new TelemetryProps(),
});

_ = new EnvironmentStack(app, "trading-copilot-staging", new EnvironmentStackProps
{
    EnvName = "staging",
    OutboundPath = outbound,
    Telemetry = new TelemetryProps(),
});

// Account-scoped: one provider, two roles. No Env.Account / Env.Region — same reason as the
// environment stacks. The production-gate environment name is aws-production (gh#1187).
_ = new GitHubOidcStack(app, "trading-copilot-github-oidc");

app.Synth();

string RequiredContext(string key) =>
    app.Node.TryGetContext(key) as string
    ?? throw new InvalidOperationException($"Context value '{key}' is required: set it in infra/cdk.json or pass -c {key}=<value>.");
