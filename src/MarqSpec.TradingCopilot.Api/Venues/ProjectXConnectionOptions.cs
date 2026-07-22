using MarqSpec.TradingCopilot.Integration.ProjectX;

namespace MarqSpec.TradingCopilot.Api.Venues;

/// <summary>
/// The app-side half of the <c>ProjectX</c> configuration section (the client library binds its own credential
/// fields from the same section). Identifies <b>which connection's</b> credentials this process holds.
/// </summary>
/// <remarks>
/// The ProjectX client's websocket is a singleton — <b>one credential set per process</b> (ADR-0015). This
/// process therefore serves the single <c>Connection</c> whose <c>CredentialKey</c> equals
/// <see cref="CredentialKey"/>; discovery against any other connection is refused with a clear error rather than
/// silently using the wrong login. Multiple simultaneous logins wait on the client library's per-connection
/// lifetime work, not an app-side workaround.
/// </remarks>
public sealed class ProjectXConnectionOptions
{
    /// <summary>The configuration section, shared with the client library's own options.</summary>
    public const string SectionName = "ProjectX";

    /// <summary>
    /// The <b>non-secret</b> credential-key name this process's ProjectX credentials belong to — must match the
    /// <c>Connection.CredentialKey</c> of the connection being served.
    /// </summary>
    public string CredentialKey { get; set; } = string.Empty;

    /// <summary>
    /// The market-data universe these credentials are entitled to. Defaults to
    /// <see cref="ProjectXDataTier.Simulated"/> — the practice universe — the fail-safe direction (R-14).
    /// </summary>
    public ProjectXDataTier DataTier { get; set; } = ProjectXDataTier.Simulated;
}
