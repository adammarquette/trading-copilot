using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>
/// Tradovate's setup-time declaration (ADR-0023, gh#64 / gh#41) — the onboarding half of the adapter, alongside the
/// runtime venue that lands with the execution increment. It declares, as data, the credential fields a Tradovate
/// login needs: the <b>seven-field</b> shape ADR-0023 named as the case the fixed two-field ProjectX form could not
/// serve. It holds no credential <em>values</em> — only their schema — so it is constructed without configuration and
/// registered as a singleton at the composition root.
/// </summary>
/// <remarks>
/// The fields mirror the client's <c>TradovateOptions</c>: <c>Username</c> and <c>Password</c> are the two the client
/// validates as required; the access-token application fields (<c>AppId</c>, <c>AppVersion</c>, <c>DeviceId</c>) and
/// the OAuth pair (<c>Cid</c>, <c>Secret</c>) are optional, needed for API-key access. Only <c>Password</c> and the
/// OAuth <c>Secret</c> are masked. The <em>endpoint model</em> (Tradovate's demo/live host pair) and the
/// <em>mode-reporting mechanism</em> ADR-0023 §1 also names ride the runtime venue increment (gh#977), where the
/// demo/live pair is actually exercised; this increment carries identity and the credential schema, exactly as
/// ProjectX's contract first landed.
/// </remarks>
public sealed class TradovateSetupContract : IVenueSetupContract
{
    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("tradovate");

    /// <inheritdoc />
    public string DisplayName => "Tradovate";

    /// <inheritdoc />
    public VenueCredentialSchema Credentials { get; } = VenueCredentialSchema.Of(
        VenueCredentialField.Create(
            key: "Username",
            label: "Username",
            secret: false,
            required: true,
            helpText: "Your Tradovate login name."),

        VenueCredentialField.Create(
            key: "Password",
            label: "Password",
            secret: true,
            required: true,
            helpText: "Your Tradovate password."),

        VenueCredentialField.Create(
            key: "AppId",
            label: "App ID",
            secret: false,
            required: false,
            helpText: "Application identifier sent with the access-token request, from your Tradovate API application."),

        VenueCredentialField.Create(
            key: "AppVersion",
            label: "App version",
            secret: false,
            required: false,
            helpText: "Application version sent with the access-token request."),

        VenueCredentialField.Create(
            key: "DeviceId",
            label: "Device ID",
            secret: false,
            required: false,
            helpText: "Device identifier sent with the access-token request."),

        VenueCredentialField.Create(
            key: "Cid",
            label: "OAuth client ID",
            secret: false,
            required: false,
            helpText: "OAuth client id (sent as \"cid\"), from your Tradovate API application — needed for API-key access."),

        VenueCredentialField.Create(
            key: "Secret",
            label: "OAuth client secret",
            secret: true,
            required: false,
            helpText: "OAuth client secret (sent as \"sec\"), from your Tradovate API application — needed for API-key access."));
}
