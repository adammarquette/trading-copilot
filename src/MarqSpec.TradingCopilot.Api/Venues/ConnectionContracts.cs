using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Api.Venues;

/// <summary>The request to register a firm login (ADR-0016: one per firm × platform).</summary>
/// <param name="FirmId">The firm this login belongs to.</param>
/// <param name="Platform">The platform key (e.g. <c>projectx</c>). Only wired adapters are accepted.</param>
/// <param name="CredentialKey">The <b>non-secret</b> env-entry name the credentials live under.</param>
public sealed record CreateConnectionRequest(Guid FirmId, string Platform, string CredentialKey);

/// <summary>A connection as returned to the operator. Never carries a secret.</summary>
/// <param name="Id">The connection's id.</param>
/// <param name="FirmId">The firm it belongs to.</param>
/// <param name="Platform">The platform key.</param>
/// <param name="CredentialKey">The env-entry name (not the credentials).</param>
public sealed record ConnectionResponse(Guid Id, Guid FirmId, string Platform, string CredentialKey)
{
    /// <summary>Projects a persisted connection to the response shape.</summary>
    /// <param name="connection">The connection.</param>
    /// <returns>The response.</returns>
    public static ConnectionResponse From(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ConnectionResponse(connection.Id, connection.FirmId, connection.Platform, connection.CredentialKey);
    }
}

/// <summary>A discovered account, with the mode the firm's conventions currently resolve for it.</summary>
/// <param name="Id">The persisted account record's id.</param>
/// <param name="VenueAccountKey">The venue's account handle.</param>
/// <param name="Name">The venue's account name.</param>
/// <param name="Stage">The stage the adapter resolved (conservatively; <c>Unknown</c> when unrecognised).</param>
/// <param name="Mode">
/// Computed from <paramref name="Stage"/> × the firm's declared conventions — never persisted, so it cannot go
/// stale when a declaration changes (gh#60). <c>Undeclared</c> is tradeable nowhere.
/// </param>
/// <param name="CanTrade">Whether the venue permits trading it.</param>
/// <param name="IsVisible">Whether the operator has left it visible.</param>
/// <param name="Balance">The balance as reported at discovery.</param>
public sealed record DiscoveredAccountResponse(
    Guid Id,
    string VenueAccountKey,
    string Name,
    AccountStage Stage,
    TradingMode Mode,
    bool CanTrade,
    bool IsVisible,
    decimal Balance);
