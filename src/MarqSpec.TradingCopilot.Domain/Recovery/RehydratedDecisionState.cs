using MarqSpec.TradingCopilot.Domain.Execution;

namespace MarqSpec.TradingCopilot.Domain.Recovery;

/// <summary>
/// The decision surface a restart brings back, projected to the <b>owner-carrying</b> facts the pure consistency
/// analysis needs (gh#221, ADR-0013). Domain-level snapshots — deliberately free of the <c>Data</c> layer — so the
/// analysis stays a pure, deterministic function the composition layer feeds from EF rows.
/// </summary>
/// <remarks>
/// Every snapshot carries its <see cref="Owner"/> (R-20): the rehydration reads across all owners (background
/// plumbing bypasses the default-deny filter), but ownership is never dropped, and a stop plan whose owner has
/// drifted from its order's is itself an inconsistency.
/// </remarks>
/// <param name="Id">The order's id.</param>
/// <param name="Owner">The owning operator (R-20).</param>
/// <param name="Status">The order's lifecycle status.</param>
/// <param name="HasVenueKey">Whether the order carries a native venue handle (i.e. it is at the venue).</param>
public sealed record RehydratedOrder(Guid Id, Guid Owner, OrderStatus Status, bool HasVenueKey);

/// <summary>A rehydrated conditional-entry order (gh#221) — its status and the order it fired, if any.</summary>
/// <param name="Id">The conditional order's id.</param>
/// <param name="Owner">The owning operator (R-20).</param>
/// <param name="Status">Where the conditional is in its lifecycle.</param>
/// <param name="FiredOrderId">The real order it fired, set only once <see cref="ConditionalStatus.Fired"/>.</param>
public sealed record RehydratedConditional(Guid Id, Guid Owner, ConditionalStatus Status, Guid? FiredOrderId);

/// <summary>A rehydrated stop plan (gh#221) — where its actual stop rests and which order it protects.</summary>
/// <param name="Id">The stop plan's id.</param>
/// <param name="Owner">The owning operator (R-20).</param>
/// <param name="OrderId">The order this plan protects — must resolve to a rehydrated order.</param>
/// <param name="Staging">Where the actual stop physically rests (hidden / native / orphaned).</param>
public sealed record RehydratedStopPlan(Guid Id, Guid Owner, Guid OrderId, StopStaging Staging);
