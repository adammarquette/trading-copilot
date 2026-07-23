namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// The operator — the single owning identity of the deployment's workspace (R-20 / ADR-0017). Every operator-owned
/// entity references its owning <see cref="User"/>. A user is not itself user-owned; the operator is seeded at
/// deploy — there is no sign-up, and the invitation flow lies dormant (ADR-0017 §4).
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

    /// <summary>
    /// Whether this is the <b>primary operator</b> — the bootstrap-seeded account (ADR-0017). Declared at
    /// seeding, never derived: privileged surfaces (today: issuing invitations, gh#128) require it, and users
    /// created through <c>accept-invite</c> are never primary — which is what stops an accepted invitee from
    /// chaining invitations into uncontrolled account creation.
    /// </summary>
    public bool IsPrimaryOperator { get; set; }

    /// <summary>When the account was created (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; set; }
}
