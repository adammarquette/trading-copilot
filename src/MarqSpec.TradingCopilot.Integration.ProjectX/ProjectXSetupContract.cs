using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Integration.ProjectX;

/// <summary>
/// ProjectX's setup-time declaration (ADR-0023, gh#64) — the onboarding half of the adapter, alongside the runtime
/// <see cref="ProjectXVenue"/>. It declares, as data, the credential fields a firm login needs, with the labels that
/// make ProjectX's naming trap legible: the config keys are <c>ApiKey</c> / <c>ApiSecret</c>, but the values are the
/// <em>username</em> and the <em>API key</em>. It holds no credential values — only their schema — so it is
/// constructed without configuration and registered as a singleton at the composition root.
/// </summary>
public sealed class ProjectXSetupContract : IVenueSetupContract
{
    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("projectx");

    /// <inheritdoc />
    public string DisplayName => "ProjectX / TopstepX";

    /// <inheritdoc />
    public VenueCredentialSchema Credentials { get; } = VenueCredentialSchema.Of(
        // The config key is "ApiKey", but the gateway wants the TopstepX USERNAME here (sent as "username"). Labelling
        // it "Username" is the whole point: entering an API key here authenticates as a user who does not exist and
        // fails as a bare "Authentication failed: Unknown error" (a 200 carrying success=false).
        VenueCredentialField.Create(
            key: "ApiKey",
            label: "Username",
            secret: false,
            required: true,
            helpText: "Your TopstepX username. Sent to the gateway as \"username\" — despite the ProjectX__ApiKey "
                + "configuration name, this field is the username, not a key."),

        // The config key is "ApiSecret", but this is the actual API key (sent as "apikey"), and it is the secret.
        VenueCredentialField.Create(
            key: "ApiSecret",
            label: "API key",
            secret: true,
            required: true,
            helpText: "Your TopstepX API key, from the firm's portal. Sent to the gateway as \"apikey\"."));
}
