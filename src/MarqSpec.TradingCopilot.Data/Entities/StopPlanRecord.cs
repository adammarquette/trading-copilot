using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// The persisted staged-stop plan for one order (data dictionary "StopPlan", ADR-0007, gh#11): the hidden
/// actual stop, the always-native safety stop beyond it, the promotion band, and where the actual stop
/// currently rests.
/// </summary>
/// <remarks>
/// The instrument's tick size and symbol are <b>not</b> duplicated here — they live on the parent
/// <see cref="Order"/>, and <c>StopPlanMapping</c> combines the two to rebuild the domain
/// <see cref="StopPlan"/>. One plan per order.
/// </remarks>
public class StopPlanRecord : IUserOwned
{
    /// <summary>The plan's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The order this plan protects — one plan per order.</summary>
    public Guid OrderId { get; set; }

    /// <summary>The position side; decides which way "beyond" runs.</summary>
    public required OrderSide Side { get; set; }

    /// <summary>The entry price the stops are measured from.</summary>
    public required decimal EntryPrice { get; set; }

    /// <summary>The working stop — hidden until promoted.</summary>
    public required decimal ActualStopPrice { get; set; }

    /// <summary>
    /// The catastrophic-insurance stop. A DB check enforces that it rests <b>beyond</b>
    /// <see cref="ActualStopPrice"/> for the side — the safety-beyond-actual invariant, below the domain.
    /// </summary>
    public required decimal SafetyStopPrice { get; set; }

    /// <summary>How the promotion band is measured. <c>Unknown</c> is refused by a DB check.</summary>
    public required StopProximityMetric ProximityMetric { get; set; }

    /// <summary>The band's magnitude. Positive — a DB check refuses the rest.</summary>
    public required decimal ProximityValue { get; set; }

    /// <summary>Where the actual stop rests. <c>Unknown</c> is refused by a DB check.</summary>
    public required StopStaging Staging { get; set; }
}
