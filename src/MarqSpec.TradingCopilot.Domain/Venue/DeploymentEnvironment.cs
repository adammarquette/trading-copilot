namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The environment the platform is running in. Mirrors the branch/environment ladder
/// (<c>develop</c> → <c>staging</c> → <c>main</c>) and gates what may be traded (R-14).
/// </summary>
public enum DeploymentEnvironment
{
    /// <summary>Local / development. Practice accounts only.</summary>
    Development,

    /// <summary>Staging — where integration and smoke tests run. Practice accounts only.</summary>
    Staging,

    /// <summary>Production. The only environment a live real-money account may be reached from.</summary>
    Production,
}
