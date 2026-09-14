namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The setup-time self-description a compiled-in adapter declares, alongside its runtime <see cref="IVenue"/> /
/// <c>ITradingVenue</c> (ADR-0023, gh#64). Where the runtime contract says what a venue can <em>do</em>, this says
/// what an operator needs to <em>offer</em> it — so the onboarding form is generated from the adapter's declaration
/// rather than hand-written per venue.
/// </summary>
/// <remarks>
/// <para>
/// Discovery stays <b>compile-time</b>: contracts are declared by adapters compiled into the image and registered at
/// the composition root, resolved by <see cref="Id"/>. Nothing here is dynamically loaded — a venue participates in
/// order placement and auto-flatten (R-13), and third-party code in that process is a security exposure .NET cannot
/// isolate (ADR-0023 §2). A declaration is also never an enforcement point: the risk / execution gate (ADR-0007) is
/// the backstop regardless of what a contract claims.
/// </para>
/// <para>
/// This increment carries identity and the credential schema (gh#64 increment 1). The endpoint model and the
/// mode-reporting mechanism the full contract also declares (ADR-0023 §1) join it with Tradovate (gh#41), the 7-field
/// demo/live case that actually tests their shape.
/// </para>
/// </remarks>
public interface IVenueSetupContract
{
    /// <summary>The venue this contract onboards — the same <see cref="VenueId"/> its runtime adapter carries.</summary>
    VenueId Id { get; }

    /// <summary>The venue's human display name, shown where a firm using it is set up.</summary>
    string DisplayName { get; }

    /// <summary>The ordered credential fields onboarding collects, each labelled for the operator (never their values).</summary>
    VenueCredentialSchema Credentials { get; }
}
