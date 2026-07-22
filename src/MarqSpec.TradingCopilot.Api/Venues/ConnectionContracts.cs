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

/// <summary>The request to declare an account's stage, overriding the conservative name-resolver (gh#76).</summary>
/// <param name="Stage">The declared stage. <c>Unknown</c> is refused — clearing the override says that.</param>
public sealed record SetStageOverrideRequest(AccountStage Stage);

/// <summary>An account with the mode the firm's conventions currently resolve for it.</summary>
/// <param name="Id">The persisted account record's id.</param>
/// <param name="VenueAccountKey">The venue's account handle.</param>
/// <param name="Name">The venue's account name.</param>
/// <param name="Stage">The stage the resolver read from the name (conservatively; <c>Unknown</c> when unrecognised).</param>
/// <param name="StageOverride">The operator's declared stage, when set — it wins over <paramref name="Stage"/>.</param>
/// <param name="Mode">
/// Computed from the <b>effective</b> stage (<paramref name="StageOverride"/> ?? <paramref name="Stage"/>) × the
/// firm's declared conventions — never persisted, so it cannot go stale when a declaration changes (gh#60).
/// <c>Undeclared</c> is tradeable nowhere.
/// </param>
/// <param name="CanTrade">Whether the venue permits trading it.</param>
/// <param name="IsVisible">Whether the operator has left it visible.</param>
/// <param name="Balance">The balance as last reported by the venue.</param>
public sealed record AccountResponse(
    Guid Id,
    string VenueAccountKey,
    string Name,
    AccountStage Stage,
    AccountStage? StageOverride,
    TradingMode Mode,
    bool CanTrade,
    bool IsVisible,
    decimal Balance)
{
    /// <summary>Projects a persisted account, computing the mode from its effective stage.</summary>
    /// <param name="account">The account.</param>
    /// <param name="conventions">The owning firm's declared conventions.</param>
    /// <returns>The response.</returns>
    public static AccountResponse From(Account account, FirmConventions conventions)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(conventions);
        return new AccountResponse(
            account.Id,
            account.VenueAccountKey,
            account.Name,
            account.Stage,
            account.StageOverride,
            conventions.ModeFor(account.StageOverride ?? account.Stage),
            account.CanTrade,
            account.IsVisible,
            account.Balance);
    }
}
