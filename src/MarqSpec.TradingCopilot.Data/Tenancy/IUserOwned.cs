namespace MarqSpec.TradingCopilot.Data.Tenancy;

/// <summary>
/// Marks an operator-owned entity — a row that belongs to exactly one user and is invisible to every other user.
/// Every entity implementing this interface is automatically given a <b>default-deny</b> per-user query filter by
/// the DbContext, so multi-user isolation (R-20 / ADR-0011) cannot be forgotten on a new entity.
/// </summary>
public interface IUserOwned
{
    /// <summary>The owning user's id. Rows are visible only to their owner.</summary>
    Guid UserId { get; }
}
