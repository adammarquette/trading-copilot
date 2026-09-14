using MarqSpec.TradingCopilot.Data.Tenancy;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A native execution (fill) of an <see cref="Order"/> (data dictionary §4) — venue facts only: price, size,
/// fees, time.
/// </summary>
/// <remarks>
/// Mode is deliberately absent: a fill inherits it through its order, whose mode the R-14 guard already
/// enforced at write.
/// </remarks>
public class Fill : IUserOwned
{
    /// <summary>The fill record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The order this fill executes.</summary>
    public Guid OrderId { get; set; }

    /// <summary>The venue's own fill handle, when it reports one.</summary>
    public string? VenueFillKey { get; set; }

    /// <summary>The execution price.</summary>
    public required decimal Price { get; set; }

    /// <summary>The filled size in contracts. Positive — a DB check refuses the rest.</summary>
    public required int Size { get; set; }

    /// <summary>Fees and commissions attributed to this fill.</summary>
    public decimal Fees { get; set; }

    /// <summary>When the fill executed.</summary>
    public required DateTimeOffset ExecutedAt { get; set; }
}
