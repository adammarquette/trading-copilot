namespace MarqSpec.TradingCopilot.Data.Tenancy;

/// <summary>
/// Supplies the authenticated user's id to the data layer for per-user query scoping. Populated <b>server-side</b>
/// from the JWT (never client-trusted). When there is no user context, <see cref="UserId"/> is
/// <see cref="System.Guid.Empty"/>, which the default-deny query filters treat as "no rows".
/// </summary>
public interface ICurrentUser
{
    /// <summary>The authenticated user's id, or <see cref="System.Guid.Empty"/> when there is no user context.</summary>
    Guid UserId { get; }
}
