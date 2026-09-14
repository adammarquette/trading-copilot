using Amazon.CDK;
using MarqSpec.TradingCopilot.Infra;

// The CDK app (ADR-0030): the same EnvironmentStack twice, and the OIDC stack that lets GitHub
// Actions deploy them. Run through infra/cdk.json; never by hand.
var app = new App();

Amazon.CDK.Tags.Of(app).Add("Project", "trading-copilot");

// NOT DECIDED HERE. ADR-0030 leaves NAT vs. public IP vs. VPC endpoints to the operator. The app
// refuses to synthesise until one shape is named. CI synthesises every value. When the operator
// decides, the choice becomes a literal here. Staging apply passes PublicIpPerTask as synth
// context only (gh#1188) — that is not a Program.cs literal and does not close the fork.
var outbound = Enum.TryParse<OutboundPath>(RequiredContext("outbound"), ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
    ? parsed
    : throw new InvalidOperationException(
        $"Context value 'outbound' must be one of {string.Join(", ", Enum.GetNames<OutboundPath>())} — the tasks' outbound path is " +
        "undecided (ADR-0030) and nothing here chooses for the operator.");

// Account / region stay apply-time context (ADR-0030 decision 14; TopstepX Program.cs shape).
// CI synth omits them so `cdk synth --no-lookups` stays environment-agnostic and Create-path.
// A credentialed apply passes `-c account=045296582762 -c region=us-east-1` (runbook, not a
// literal in this file).
var account = app.Node.TryGetContext("account") as string;
var region = app.Node.TryGetContext("region") as string;
Amazon.CDK.Environment? awsEnv = !string.IsNullOrWhiteSpace(account) && !string.IsNullOrWhiteSpace(region)
    ? new Amazon.CDK.Environment { Account = account, Region = region }
    : null;

// Staging apply looks up the existing zone. Create here would mint a second
// staging.marqspec.com zone and undo TopstepX's Cloudflare NS swap (Z00545362JA49XMTT3U7Q).
// CI without account/region keeps Create so synth --no-lookups needs no AWS call.
var stagingLookup = awsEnv is not null;
var stagingRoot = stagingLookup
    ? app.Node.TryGetContext("rootDomain") as string
        ?? throw new InvalidOperationException(
            "Context value 'rootDomain' is required when account/region are set: staging looks up that zone (gh#1188).")
    : null;

_ = new EnvironmentStack(app, "trading-copilot-production", new EnvironmentStackProps
{
    EnvName = "production",
    OutboundPath = outbound,
    ZoneMode = ZoneMode.Create,
    Telemetry = new TelemetryProps(),
    Env = awsEnv,
});

_ = new EnvironmentStack(app, "trading-copilot-staging", new EnvironmentStackProps
{
    EnvName = "staging",
    OutboundPath = outbound,
    ZoneMode = stagingLookup ? ZoneMode.Lookup : ZoneMode.Create,
    RootDomain = stagingRoot,
    Telemetry = new TelemetryProps(),
    Env = awsEnv,
});

_ = new GitHubOidcStack(app, "trading-copilot-github-oidc", awsEnv is null ? null : new StackProps { Env = awsEnv });

app.Synth();

string RequiredContext(string key) =>
    app.Node.TryGetContext(key) as string
    ?? throw new InvalidOperationException($"Context value '{key}' is required: set it in infra/cdk.json or pass -c {key}=<value>.");
