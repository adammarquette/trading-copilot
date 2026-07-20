using Microsoft.AspNetCore.Identity;

namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>An <see cref="IPasswordHasher"/> backed by ASP.NET Core Identity's PBKDF2 <see cref="PasswordHasher{TUser}"/>.</summary>
public sealed class PasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<object> _hasher = new();
    private readonly object _user = new();

    /// <inheritdoc />
    public string Hash(string password)
    {
        return _hasher.HashPassword(_user, password);
    }

    /// <inheritdoc />
    public bool Verify(string hash, string password)
    {
        return _hasher.VerifyHashedPassword(_user, hash, password)
            is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
