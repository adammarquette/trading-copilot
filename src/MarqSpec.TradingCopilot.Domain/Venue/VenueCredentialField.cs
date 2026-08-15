namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// One field an operator supplies to onboard a venue, as the setup contract declares it (ADR-0023, gh#64): its
/// configuration <see cref="Key"/>, the human <see cref="Label"/> shown at the point of entry, whether it is
/// <see cref="Secret"/>, whether it is <see cref="Required"/>, and optional <see cref="HelpText"/>.
/// </summary>
/// <remarks>
/// The <see cref="Label"/> is the load-bearing field. A credential's <em>name</em> is folklore today — ProjectX's
/// <c>ApiKey</c> is really the username and <c>ApiSecret</c> is really the API key — and that mapping lives only in an
/// <c>.env.example</c> comment the operator never sees. Carrying the true label as data turns the trap into text read
/// where the value is entered. <see cref="Secret"/> fields are masked in the UI and never logged or echoed back; this
/// type describes the <em>schema</em> of a credential, never holds its value.
/// </remarks>
public sealed record VenueCredentialField
{
    private VenueCredentialField(string key, string label, bool secret, bool required, string? helpText)
    {
        Key = key;
        Label = label;
        Secret = secret;
        Required = required;
        HelpText = helpText;
    }

    /// <summary>The configuration key the value is supplied under (e.g. <c>ApiKey</c>), within the venue's section.</summary>
    public string Key { get; }

    /// <summary>The human label shown at the point of entry — what the field actually <em>is</em>, not its config name.</summary>
    public string Label { get; }

    /// <summary>Whether the value is a secret: masked in the UI, never logged, never returned once stored.</summary>
    public bool Secret { get; }

    /// <summary>Whether onboarding requires the field.</summary>
    public bool Required { get; }

    /// <summary>Optional guidance shown beside the field, or <see langword="null"/> when there is none.</summary>
    public string? HelpText { get; }

    /// <summary>Declares a credential field, trimming <paramref name="key"/>/<paramref name="label"/> and normalizing blank help text to <see langword="null"/>.</summary>
    /// <param name="key">The configuration key the value is supplied under; must be non-blank.</param>
    /// <param name="label">The human label shown at entry; must be non-blank (a blank label re-opens the naming trap).</param>
    /// <param name="secret">Whether the value is a secret (masked, never logged or returned).</param>
    /// <param name="required">Whether onboarding requires the field.</param>
    /// <param name="helpText">Optional guidance; blank is normalized to <see langword="null"/>.</param>
    /// <returns>The declared field.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> or <paramref name="label"/> is null, empty, or whitespace.</exception>
    public static VenueCredentialField Create(
        string key,
        string label,
        bool secret,
        bool required,
        string? helpText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        return new VenueCredentialField(
            key.Trim(),
            label.Trim(),
            secret,
            required,
            string.IsNullOrWhiteSpace(helpText) ? null : helpText.Trim());
    }
}
