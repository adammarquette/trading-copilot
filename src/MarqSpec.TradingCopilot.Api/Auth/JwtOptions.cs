namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>JWT issuance / validation settings. The signing key comes from configuration (env / user-secrets), never source.</summary>
public sealed class JwtOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Jwt";

    /// <summary>The symmetric signing key (secret — from env / user-secrets).</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>The token issuer.</summary>
    public string Issuer { get; set; } = "MarqSpec.TradingCopilot";

    /// <summary>The token audience.</summary>
    public string Audience { get; set; } = "MarqSpec.TradingCopilot";

    /// <summary>Access-token lifetime, in minutes.</summary>
    public int LifetimeMinutes { get; set; } = 60;
}
