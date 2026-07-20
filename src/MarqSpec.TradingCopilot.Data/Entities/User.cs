namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// The tenant root — an operator with a private, isolated workspace (R-20 / ADR-0011). Every operator-owned entity
/// references its owning <see cref="User"/>. A user is not itself user-owned; onboarding is invitation-only (R-18).
/// </summary>
public class User
{
    /// <summary>The user's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The user's email (unique) — the login identifier.</summary>
    public required string Email { get; set; }

    /// <summary>The hashed credential. The plaintext password is never stored.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>The user's display name.</summary>
    public required string DisplayName { get; set; }

    /// <summary>The account lifecycle state.</summary>
    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>When the account was created (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; set; }
}
