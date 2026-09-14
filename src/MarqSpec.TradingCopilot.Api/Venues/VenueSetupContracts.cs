using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Api.Venues;

/// <summary>
/// A venue's onboarding self-description as served to the client (ADR-0023, gh#64): its identity and the labelled
/// credential schema. It <b>never carries a credential value</b> — the schema tells the UI what to collect and how
/// to label it, not what the secret is (ADR-0015).
/// </summary>
/// <param name="VenueId">The venue key (e.g. <c>projectx</c>), the same id its runtime adapter carries.</param>
/// <param name="DisplayName">The venue's human display name.</param>
/// <param name="Credentials">The ordered credential fields onboarding collects, each labelled — no values.</param>
public sealed record VenueSetupResponse(
    string VenueId,
    string DisplayName,
    IReadOnlyList<VenueCredentialFieldResponse> Credentials)
{
    /// <summary>Projects a registered setup contract to the response shape.</summary>
    /// <param name="contract">The venue's setup contract.</param>
    /// <returns>The response — schema only, never a credential value.</returns>
    public static VenueSetupResponse From(IVenueSetupContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new VenueSetupResponse(
            contract.Id.Value,
            contract.DisplayName,
            [.. contract.Credentials.Fields.Select(VenueCredentialFieldResponse.From)]);
    }
}

/// <summary>
/// One credential field's <b>schema</b> (ADR-0023): its config <paramref name="Key"/>, the human
/// <paramref name="Label"/> shown at entry, whether it is <paramref name="Secret"/> (the UI masks it) or
/// <paramref name="Required"/>, and optional <paramref name="HelpText"/>. There is deliberately no value field —
/// this describes what onboarding collects, never the secret itself.
/// </summary>
/// <param name="Key">The configuration key the value is supplied under.</param>
/// <param name="Label">The human label — what the field actually is, not its config name.</param>
/// <param name="Secret">Whether the UI masks it (and it is never logged or returned once stored).</param>
/// <param name="Required">Whether onboarding requires it.</param>
/// <param name="HelpText">Optional guidance shown beside the field.</param>
public sealed record VenueCredentialFieldResponse(
    string Key,
    string Label,
    bool Secret,
    bool Required,
    string? HelpText)
{
    /// <summary>Projects a domain credential field to the response shape.</summary>
    /// <param name="field">The credential field schema.</param>
    /// <returns>The response.</returns>
    public static VenueCredentialFieldResponse From(VenueCredentialField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new VenueCredentialFieldResponse(field.Key, field.Label, field.Secret, field.Required, field.HelpText);
    }
}
