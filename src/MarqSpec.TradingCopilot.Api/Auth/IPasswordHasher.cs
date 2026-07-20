namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>Hashes and verifies passwords. The plaintext password is never stored.</summary>
public interface IPasswordHasher
{
    /// <summary>Hashes <paramref name="password"/> into a salted, storable string.</summary>
    /// <param name="password">The plaintext password.</param>
    /// <returns>The hash to persist.</returns>
    string Hash(string password);

    /// <summary>Verifies <paramref name="password"/> against a stored <paramref name="hash"/>.</summary>
    /// <param name="hash">The stored hash.</param>
    /// <param name="password">The candidate plaintext password.</param>
    /// <returns><see langword="true"/> if the password matches.</returns>
    bool Verify(string hash, string password);
}
