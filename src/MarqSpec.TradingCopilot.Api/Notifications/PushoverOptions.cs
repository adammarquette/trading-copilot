namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// The operator's Pushover application (gh#243, ADR-0019). Bound from the <c>Pushover</c> config section.
/// </summary>
/// <remarks>
/// Both values are <b>secrets</b> — environment only, never source, never logged. Absent, the channel is inert
/// and says so once at startup: running unreachable is allowed, running unreachable <i>silently</i> is not.
/// </remarks>
public sealed class PushoverOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Pushover";

    /// <summary>The Pushover application token.</summary>
    public string? AppToken { get; init; }

    /// <summary>The operator's Pushover user (or group) key.</summary>
    public string? UserKey { get; init; }

    /// <summary>
    /// How often an unacknowledged <b>Emergency</b> page repeats, in seconds. Pushover's floor is 30; ADR-0019
    /// chose 60 — often enough not to be missed, rare enough not to become noise while the operator is acting.
    /// </summary>
    public int PageRetrySeconds { get; init; } = 60;

    /// <summary>
    /// How long an unacknowledged page keeps repeating before Pushover gives up, in seconds. 30 minutes: past
    /// that the operator is not reachable and more nagging will not change it — the alert stays visible in the
    /// app and on the dashboard regardless.
    /// </summary>
    public int PageExpireSeconds { get; init; } = 1800;

    /// <summary>Whether both credentials are present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppToken) && !string.IsNullOrWhiteSpace(UserKey);
}
