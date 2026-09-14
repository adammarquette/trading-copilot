namespace MarqSpec.TradingCopilot.Domain.Chat;

/// <summary>
/// Who authored a chat message (gh#18, R-6). <see cref="Unknown"/> is the refusable zero — a DB check refuses it,
/// so a defaulted or corrupt row can never read as a real message (the gh#60 fail-closed-zero convention).
/// </summary>
public enum ChatRole
{
    /// <summary>Unset — refused by a DB check. A message always has a real author.</summary>
    Unknown = 0,

    /// <summary>The operator's turn.</summary>
    User = 1,

    /// <summary>The co-pilot's reply.</summary>
    Assistant = 2,

    /// <summary>A system message — grounding / instructions, not shown to the operator as a turn.</summary>
    System = 3,
}
