using MarqSpec.TradingCopilot.Domain.Execution;

namespace MarqSpec.TradingCopilot.Domain.Recovery;

/// <summary>
/// The pure heart of decision-state rehydration (gh#221, ADR-0013): given the decision surface a restart brought
/// back, count what returned and find any <b>impossible combination</b> a crash mid-write left. Deterministic and
/// side-effect-free — the composition layer feeds it from EF rows and acts on the result (observe, and on any
/// inconsistency fail safe + loud), so the safety logic lives below the model and is exhaustively unit-testable.
/// </summary>
/// <remarks>
/// It classifies, it does not resume: no order is sent, no conditional fired, no stop promoted. Those paths are
/// request- or quote-driven and re-validate against fresh truth (R-12); rehydration only makes the surface's return
/// visible and its impossible states loud. The checks are <b>cross-entity</b> — a crash between two independent
/// writes leaves each row individually valid but the whole contradictory, which no single-row DB check can catch.
/// </remarks>
public static class DecisionStateRehydration
{
    /// <summary>
    /// Analyses the rehydrated decision surface: counts the inert state and collects every impossible combination.
    /// </summary>
    /// <param name="orders">Every rehydrated order, across all owners.</param>
    /// <param name="conditionals">Every rehydrated conditional-entry order.</param>
    /// <param name="stopPlans">Every rehydrated stop plan.</param>
    /// <param name="activeSuggestions">How many suggestions are still active (inert until taken).</param>
    /// <returns>The surface report — counts plus any inconsistencies.</returns>
    public static DecisionSurfaceReport Analyze(
        IReadOnlyList<RehydratedOrder> orders,
        IReadOnlyList<RehydratedConditional> conditionals,
        IReadOnlyList<RehydratedStopPlan> stopPlans,
        int activeSuggestions)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(conditionals);
        ArgumentNullException.ThrowIfNull(stopPlans);

        Dictionary<Guid, RehydratedOrder> ordersById = orders.ToDictionary(order => order.Id);
        List<DecisionInconsistency> issues = [];

        foreach (RehydratedOrder order in orders)
        {
            // Staged means "armed server-side, NOT at the venue" -- a venue handle on it is a crash between the
            // take (which places it and stamps the key) and the journal that would have marked it Working.
            if (order.Status == OrderStatus.Staged && order.HasVenueKey)
            {
                issues.Add(new DecisionInconsistency(
                    DecisionInconsistencyKind.StagedOrderAtVenue, order.Owner, order.Id,
                    "A staged order carries a venue order key; a staged order is not at the venue."));
            }
        }

        foreach (RehydratedConditional conditional in conditionals)
        {
            bool fired = conditional.Status == ConditionalStatus.Fired;
            switch (fired, conditional.FiredOrderId)
            {
                case (true, null):
                    issues.Add(new DecisionInconsistency(
                        DecisionInconsistencyKind.ConditionalFiredWithoutOrder, conditional.Owner, conditional.Id,
                        "A fired conditional order has no linked order id."));
                    break;
                case (true, { } firedOrderId) when !ordersById.ContainsKey(firedOrderId):
                    issues.Add(new DecisionInconsistency(
                        DecisionInconsistencyKind.ConditionalFiredOrderMissing, conditional.Owner, conditional.Id,
                        $"A fired conditional order links order {firedOrderId}, which is absent."));
                    break;
                case (false, { } linkedOrderId):
                    issues.Add(new DecisionInconsistency(
                        DecisionInconsistencyKind.ConditionalUnfiredWithOrder, conditional.Owner, conditional.Id,
                        $"A {conditional.Status} conditional order links order {linkedOrderId}; only a fired one may."));
                    break;
                default:
                    break; // fired with a present order, or unfired with none -- coherent
            }
        }

        foreach (RehydratedStopPlan plan in stopPlans)
        {
            if (!ordersById.TryGetValue(plan.OrderId, out RehydratedOrder? parent))
            {
                issues.Add(new DecisionInconsistency(
                    DecisionInconsistencyKind.StopPlanWithoutOrder, plan.Owner, plan.Id,
                    $"A stop plan protects order {plan.OrderId}, which is absent."));
                continue; // the remaining stop-plan checks need the parent
            }

            if (plan.Owner != parent.Owner)
            {
                issues.Add(new DecisionInconsistency(
                    DecisionInconsistencyKind.StopPlanOwnerMismatch, plan.Owner, plan.Id,
                    $"A stop plan's owner differs from its order's owner ({parent.Owner})."));
            }

            // A native stop is an exchange-held working order -- it can only exist for a LIVE position, so its
            // order must have reached the venue (working / partially filled / filled), never be staged or dead.
            if (plan.Staging == StopStaging.Native && !IsLive(parent.Status))
            {
                issues.Add(new DecisionInconsistency(
                    DecisionInconsistencyKind.NativeStopWithoutLiveOrder, plan.Owner, plan.Id,
                    $"A native (at-venue) stop protects order {parent.Id}, which is {parent.Status}, not live."));
            }
        }

        return new DecisionSurfaceReport(
            Orders: orders.Count,
            StagedOrders: orders.Count(order => order.Status == OrderStatus.Staged),
            Conditionals: conditionals.Count,
            PendingConditionals: conditionals.Count(conditional => conditional.Status == ConditionalStatus.Pending),
            HiddenStops: stopPlans.Count(plan => plan.Staging == StopStaging.Hidden),
            NativeStops: stopPlans.Count(plan => plan.Staging == StopStaging.Native),
            OrphanedStops: stopPlans.Count(plan => plan.Staging == StopStaging.Orphaned),
            ActiveSuggestions: activeSuggestions,
            Inconsistencies: issues);
    }

    /// <summary>
    /// Whether an order is <b>live</b> at the venue — its entry reached the book, so a native protective stop may
    /// rest against the position it opened. Staged / cancelled / rejected orders are not live.
    /// </summary>
    /// <param name="status">The order's status.</param>
    /// <returns><see langword="true"/> when a native stop may legitimately protect it.</returns>
    private static bool IsLive(OrderStatus status) =>
        status is OrderStatus.Working or OrderStatus.PartiallyFilled or OrderStatus.Filled;
}
