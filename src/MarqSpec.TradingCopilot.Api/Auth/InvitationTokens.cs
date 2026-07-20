using System.Security.Cryptography;
using System.Text;

namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>
/// Generates and hashes single-use invitation tokens. The plaintext token is shown once (the invite link); only
/// its hash is stored, so a leaked database cannot yield usable invites. Tokens are high-entropy, so a fast hash
/// (SHA-256) is sufficient — unlike passwords, which need a slow hash.
/// </summary>
public static class InvitationTokens
{
    /// <summary>Generates a new random token and its storable hash.</summary>
    /// <returns>The plaintext <c>Token</c> (to send the invitee) and its <c>TokenHash</c> (to store).</returns>
    public static (string Token, string TokenHash) Generate()
    {
        string token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token));
    }

    /// <summary>Hashes an invitation token for storage / lookup.</summary>
    /// <param name="token">The plaintext token.</param>
    /// <returns>The base64 SHA-256 hash.</returns>
    public static string Hash(string token)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
