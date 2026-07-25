using MarqSpec.TradingCopilot.Domain;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// Where the dead-man's switch reports to, and how often (gh#244, ADR-0019). Bound from the <c>CheckIn</c> config
/// section.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="FlattenOptions"/>, this has <b>no safe built-in default</b>. A fabricated URL would report
/// flat to nowhere while looking configured, which is worse than being unconfigured — so absence disables the
/// switch and says so loudly at startup, and a malformed URL fails then rather than at the deadline.
/// </para>
/// <para>
/// Every URL here is a <b>capability URL</b>: whoever holds one can forge an all-clear on a safety path. They come
/// from environment via the Options pattern, never source, and are never logged.
/// </para>
/// </remarks>
public sealed class CheckInOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "CheckIn";

    /// <summary>The monitor check pinged by the liveness heartbeat, or <see langword="null"/> to send none.</summary>
    public string? HeartbeatUrl { get; init; }

    /// <summary>
    /// How often the liveness heartbeat is sent. Unconditional — the deployment is always-on (R-1), so a gap is
    /// worth knowing about whenever it happens.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; init; } = 60;

    /// <summary>The heartbeat cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatIntervalSeconds);

    /// <summary>
    /// How often the daily flatten check-in is evaluated. A minute is ample: the check fires once per instrument
    /// per day, and the monitor's grace is measured in minutes.
    /// </summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>The evaluation cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    /// <summary>The per-instrument monitor checks — one per market the operator wants vouched for.</summary>
    public CheckInUrlOption[] Instruments { get; init; } = [];

    /// <summary>
    /// Whether anything is configured at all. When <see langword="false"/> the switch is inert and the app logs a
    /// prominent startup warning — running unmonitored is allowed, running unmonitored <i>silently</i> is not.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(HeartbeatUrl) || Instruments.Any(check => !string.IsNullOrWhiteSpace(check.Url));

    /// <summary>The heartbeat check's URL, or <see langword="null"/> when none is configured.</summary>
    /// <exception cref="FormatException">The configured value is not an absolute HTTP(S) URL.</exception>
    public Uri? HeartbeatUri => Parse(HeartbeatUrl, "heartbeat");

    /// <summary>Resolves the monitor check for an instrument.</summary>
    /// <param name="instrument">The instrument.</param>
    /// <returns>Its check URL, or <see langword="null"/> when the operator monitors no check for it.</returns>
    /// <exception cref="FormatException">The configured value is not an absolute HTTP(S) URL.</exception>
    public Uri? UrlFor(InstrumentId instrument)
    {
        CheckInUrlOption? match = Instruments.FirstOrDefault(
            check => string.Equals(check.Symbol?.Trim(), instrument.ToString(), StringComparison.OrdinalIgnoreCase));

        return match is null ? null : Parse(match.Url, instrument.ToString());
    }

    private static Uri? Parse(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Loud at startup beats wrong at the deadline -- the same stance the deadline parser takes.
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new FormatException(
                $"The check-in URL for '{what}' must be an absolute http(s) URL. "
                + "Check the CheckIn configuration; the value itself is not logged because it is a capability URL.");
        }

        return parsed;
    }
}

/// <summary>One instrument's monitor check (gh#244).</summary>
public sealed class CheckInUrlOption
{
    /// <summary>The venue-neutral instrument symbol (e.g. <c>ES</c>).</summary>
    public required string Symbol { get; init; }

    /// <summary>The monitor's ping URL for this instrument. A capability URL — environment only, never logged.</summary>
    public required string Url { get; init; }
}
