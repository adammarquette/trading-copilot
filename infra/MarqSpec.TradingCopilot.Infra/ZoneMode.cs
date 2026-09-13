namespace MarqSpec.TradingCopilot.Infra;

/// <summary>
/// Where the environment's Route 53 hosted zone comes from (gh#1188).
/// </summary>
/// <remarks>
/// Starts at 1 so that <c>default</c> is not a member and a props record that forgot to name one
/// is refused rather than silently creating a zone. Shape is TopstepX <c>ZoneMode</c> in
/// MarqSpec.Mcp.TopstepX (pattern library) — cite it; do not copy their zone ids or hostnames.
/// </remarks>
public enum ZoneMode
{
    /// <summary>
    /// The zone already exists. Staging looks up <c>staging.marqspec.com</c> (zone
    /// <c>Z00545362JA49XMTT3U7Q</c>, created so Cloudflare could delegate a stable NS set —
    /// TopstepX gh#519). Looked up through the CDK context provider at apply time
    /// (<c>-c account=</c> / <c>-c region=</c>). The zone id is recorded in the runbook, not
    /// hardcoded in this enum.
    /// </summary>
    Lookup = 1,

    /// <summary>
    /// The stack creates the zone from the <c>RootDomain</c> parameter. Fixture and
    /// <c>cdk synth --no-lookups</c> path — no AWS call. Staging apply must not use this: a
    /// second <c>staging.marqspec.com</c> zone would mint new NS and undo the Cloudflare swap.
    /// </summary>
    Create = 2,
}
