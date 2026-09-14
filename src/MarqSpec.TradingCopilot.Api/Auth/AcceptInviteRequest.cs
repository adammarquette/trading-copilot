namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>A request to accept an invitation and create the account.</summary>
/// <param name="Token">The invite token from the invitation.</param>
/// <param name="Password">The chosen plaintext password.</param>
/// <param name="DisplayName">The chosen display name.</param>
public sealed record AcceptInviteRequest(string Token, string Password, string DisplayName);
