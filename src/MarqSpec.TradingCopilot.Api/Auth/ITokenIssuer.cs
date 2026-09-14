using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>Issues signed access tokens (JWT) for authenticated users.</summary>
public interface ITokenIssuer
{
    /// <summary>Issues a signed JWT carrying <paramref name="user"/>'s id and email.</summary>
    /// <param name="user">The authenticated user.</param>
    /// <returns>The encoded JWT.</returns>
    string Issue(User user);
}
