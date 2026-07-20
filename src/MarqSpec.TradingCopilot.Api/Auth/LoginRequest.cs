namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>A login request.</summary>
/// <param name="Email">The user's email.</param>
/// <param name="Password">The user's plaintext password.</param>
public sealed record LoginRequest(string Email, string Password);
