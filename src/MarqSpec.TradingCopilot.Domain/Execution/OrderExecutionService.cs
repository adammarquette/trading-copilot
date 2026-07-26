using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// The single enforcing path to a broker (R-11, ADR-0007). Guards run in a deliberate order, and an order that
/// fails either one **does not reach the venue at all** — refusal is not advisory.
/// </summary>
/// <remarks>
/// <para>
/// The size transmitted is the one the <b>gate approved</b>, never the one asked for. Sending the requested
/// quantity after a resize would make the gate advisory, which is the single thing it must never be.
/// </para>
/// <para>
/// Guards that establish the request <i>means</i> what it appears to mean run <b>before</b> the gate: an
/// authorization computed against one account or instrument says nothing about another, so evaluating an
/// incoherent request would produce a decision that does not describe what would be sent.
/// </para>
/// <para>
/// The R-14 guard is only ever as good as the <see cref="TradingMode"/> it is handed. This class enforces
/// correctly on whatever it is given; <i>how</i> that mode is established belongs to the venue layer, and a
/// venue flag alone does not establish it — see <see cref="TradingMode"/> and gh#60.
/// </para>
/// </remarks>
public sealed class OrderExecutionService : IOrderExecutionService
{
    private readonly IRiskGate _gate;
    private readonly IOrderExecutor _venue;
    private readonly DeploymentEnvironment _environment;
    private readonly IKillSwitch _killSwitch;

    /// <summary>Creates the execution service.</summary>
    /// <param name="gate">The enforcing risk gate (R-5 / R-16).</param>
    /// <param name="venue">The venue's execution slice — the only thing here that reaches a broker.</param>
    /// <param name="environment">
    /// Where this process is actually running, supplied once at composition from trusted host configuration.
    /// Deliberately <b>not</b> a per-send argument: R-14 is an enforcement boundary, and a caller able to pass
    /// <see cref="DeploymentEnvironment.Production"/> from a development host could walk a live account straight
    /// through the guard.
    /// </param>
    /// <param name="killSwitch">The kill-switch state; while it is engaged, every send is refused (ADR-0007, gh#189).</param>
    public OrderExecutionService(IRiskGate gate, IOrderExecutor venue, DeploymentEnvironment environment, IKillSwitch killSwitch)
    {
        _gate = gate;
        _venue = venue;
        _environment = environment;
        _killSwitch = killSwitch;
    }

    /// <summary>
    /// Runs the <b>entire</b> ladder — R-14 mode × environment, account state, mismatch, type, then the gate —
    /// and stops short of transmission (the arm/edit path, gh#11). The same checks as
    /// <see cref="SendAsync"/> in the same order, from the same code, so an armed ticket was judged exactly as
    /// a sent one would be.
    /// </summary>
    /// <param name="request">The assembled execution request.</param>
    /// <returns>
    /// <see cref="ExecutionOutcome.Evaluated"/> carrying the gate's decision, or the pre-gate refusal.
    /// </returns>
    public ExecutionResult Evaluate(ExecutionRequest request)
    {
        // R-14's *environment* restriction -- practice accounts only outside production -- first, before the
        // order is even priced: an account this environment may not trade should be refused outright rather
        // than sized and then rejected.
        //
        // This is not R-14's other obligation, the "mode guard" of PRD R-14: that an Order or Suggestion cannot
        // be *persisted* with a mode conflicting with its parent Account. That one is about journal integrity,
        // belongs to the repository layer plus a DB check constraint, and has nothing to enforce here -- the
        // venue ticket carries no mode, and nothing is persisted on this path yet.
        if (!TradingModePolicy.IsAllowed(request.Account.Mode, _environment))
        {
            return ExecutionResult.RefusedByMode(
                $"Account '{request.Account.Name}' is {request.Account.Mode} and may not be traded from "
                + $"{_environment} (R-14).");
        }

        if (!request.Account.CanTrade)
        {
            // The venue's own eligibility statement -- a passed, failed or closed prop account reports it.
            // Leaving this to the broker would mean the ticket left the enforcing path before being refused.
            return ExecutionResult.RefusedByAccountState(
                $"The venue reports account '{request.Account.Name}' ({request.Account.Id}) as not tradable.");
        }

        if (Mismatch(request) is { } mismatch)
        {
            return ExecutionResult.RefusedByMismatch(mismatch);
        }

        if (Unrepresentable(request.Type) is { } unrepresentable)
        {
            return ExecutionResult.RefusedByUnsupportedType(unrepresentable);
        }

        if (WrongSideTarget(request.Proposal) is { } invalidTarget)
        {
            // A wrong-side take-profit is a self-contradiction, like a mismatched request -- refused before the
            // gate, so arm and send alike reject it rather than discovering it at take (gh#170).
            return ExecutionResult.RefusedByInvalidTarget(invalidTarget);
        }

        return ExecutionResult.Evaluated(_gate.Evaluate(request.Proposal, request.Risk));
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> SendAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        // The kill switch (ADR-0007, gh#189) sits above the normal flow: while engaged, outbound orders are
        // disabled, so every transmission -- a manual send, a take, or a conditional firing -- is refused before
        // the order is even sized. Reducing / protective actions (auto-flatten's close, stop promotion) do not
        // pass through this path, so a killed system can still be flattened and stays protected.
        if (_killSwitch.IsEngaged)
        {
            return ExecutionResult.RefusedByKillSwitch(
                "The kill switch is engaged — outbound orders are disabled. Disengage it before trading again.");
        }

        ExecutionResult evaluated = Evaluate(request);
        if (evaluated.Outcome != ExecutionOutcome.Evaluated || evaluated.Decision is null)
        {
            return evaluated; // a pre-gate refusal -- mode, state, mismatch, or type
        }

        GateDecision decision = evaluated.Decision;

        // Whitelist, not "anything but Blocked". GateDecision has a public constructor and IRiskGate is
        // replaceable, so an unrecognized or later-added outcome must not become authorization by default --
        // a new outcome opts in here deliberately. A zero approved quantity is the gate's "no trade" whatever
        // outcome accompanies it.
        bool authorized = decision.Outcome is GateOutcome.Allowed or GateOutcome.Resized;

        if (!authorized || decision.ApprovedQuantity <= 0)
        {
            return ExecutionResult.RefusedByRisk(decision);
        }

        // The always-native safety stop (ADR-0007): every entry carries its safety stop as an exchange-held
        // protective bracket, so a live position is never without a real stop. If the venue can't hold one,
        // transmitting would open an UNPROTECTED position -- fail-closed and refuse, better no trade than that.
        if (!_venue.Capabilities.Supports(VenueCapability.BracketOrders))
        {
            return ExecutionResult.RefusedByUnprotectableStop(
                $"Venue '{_venue.Id}' cannot hold an exchange-native protective stop, so the position would be "
                + "unprotected. The entry was not sent.");
        }

        OrderRequest order = new(
            request.Account.Id,
            request.Contract.Contract,
            request.Proposal.Side,
            request.Type,
            decision.ApprovedQuantity,
            LimitPriceFor(request),
            StopPriceFor(request))
        {
            ProtectiveStop = request.Proposal.SafetyStop,
            ProfitTarget = request.Proposal.Target,
        };

        PlacedOrder placed = await _venue.PlaceOrderAsync(order, cancellationToken);

        return ExecutionResult.Placed(placed, decision);
    }

    /// <summary>
    /// Re-gates and, if it survives, transmits an in-place <b>modify</b> of a working order — a reprice (gh#259)
    /// and/or a <b>resize</b> (gh#292). Runs the same ladder as <see cref="SendAsync"/> in the same order, so a
    /// modify is judged exactly as the original send was; it is <b>not</b> a way around the gate.
    /// </summary>
    /// <remarks>
    /// Deliberate differences from <see cref="SendAsync"/>, all safety-driven:
    /// <list type="bullet">
    /// <item>Unlike a cancel (risk-reducing, gate-exempt), a modify is <b>outbound and can add risk</b> — moving
    /// an entry to a level likelier to fill, widening the stop distance, or increasing the size — so it is
    /// refused while the <b>kill switch</b> is engaged, exactly as a send is (a downsize routes through here too and
    /// so is refused under the kill switch this increment; the operator can still cancel to reduce risk).</item>
    /// <item>A pure reprice (<paramref name="resize"/> == <see langword="false"/>) holds size invariant: the gate
    /// must approve the <b>unchanged</b> size outright (<see cref="GateOutcome.Allowed"/> at the requested
    /// quantity), and a <see cref="GateOutcome.Resized"/> <b>refuses</b> it — silently downsizing a reprice would
    /// strand the attached safety bracket (gh#259). It is sent with <c>size: null</c> to preserve queue position.</item>
    /// <item>A <b>resize</b> (<paramref name="resize"/> == <see langword="true"/>) is a deliberate size change, so
    /// it honours the gate exactly as <see cref="SendAsync"/> does — <see cref="GateOutcome.Allowed"/> or
    /// <see cref="GateOutcome.Resized"/> — and transmits the <b>gate-approved</b> quantity (never the asked one).
    /// The gh#259 desync fear does not apply: the always-native safety bracket carries no size of its own, and the
    /// gateway attaches it <i>on fill</i> sized to the realized fill, so the eventual fill is protected at the
    /// transmitted quantity by construction (gh#292; verified on practice before live, gh#293).</item>
    /// </list>
    /// </remarks>
    /// <param name="request">The re-priced / re-sized execution request — its proposal carries the requested quantity.</param>
    /// <param name="venueOrderId">The venue handle of the working order to modify.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="resize">
    /// <see langword="true"/> when the order's size changes (gh#292): honour the gate's approved quantity and send
    /// it to the venue. <see langword="false"/> (default) for a pure reprice: hold size invariant, send <c>null</c>.
    /// </param>
    /// <returns><see cref="ExecutionOutcome.Modified"/> on a transmitted modify, or the refusal that stopped it.</returns>
    public async Task<ExecutionResult> ModifyAsync(
        ExecutionRequest request, string venueOrderId, CancellationToken cancellationToken, bool resize = false)
    {
        if (_killSwitch.IsEngaged)
        {
            return ExecutionResult.RefusedByKillSwitch(
                "The kill switch is engaged — outbound orders are disabled. Disengage it before modifying an order.");
        }

        ExecutionResult evaluated = Evaluate(request);
        if (evaluated.Outcome != ExecutionOutcome.Evaluated || evaluated.Decision is null)
        {
            return evaluated; // a pre-gate refusal -- mode, state, mismatch, or type
        }

        GateDecision decision = evaluated.Decision;

        // Two whitelists, both stricter than "anything but Blocked" (GateDecision has a public ctor and IRiskGate is
        // replaceable, so authorization is opt-in):
        //   * A pure REPRICE (resize == false) holds SIZE invariant, so the gate must approve the UNCHANGED size
        //     OUTRIGHT -- Allowed at exactly the requested quantity. A Resize (the gate wanting fewer), a Block, a
        //     zero, or any later-added outcome REFUSES it: silently changing size on a reprice would strand the
        //     attached safety bracket (gh#259).
        //   * A RESIZE (resize == true) is a deliberate size change, so it honours the gate exactly as SendAsync does
        //     -- Allowed OR Resized, at whatever quantity the gate approved (always <= asked). The gh#259 desync fear
        //     does not apply: the safety bracket carries no size of its own, and the gateway sizes it to the realized
        //     fill on attach, so the eventual fill is protected at the transmitted quantity (gh#292; verified on
        //     practice before live, gh#293).
        bool authorized = resize
            ? decision.Outcome is GateOutcome.Allowed or GateOutcome.Resized
            : decision.Outcome is GateOutcome.Allowed
                && decision.ApprovedQuantity == request.Proposal.RequestedQuantity;

        if (!authorized || decision.ApprovedQuantity <= 0)
        {
            return ExecutionResult.RefusedByRisk(decision);
        }

        // The always-native safety stop must still be holdable: on fill, the position rests protected at the
        // safety level, which the gate has just re-validated as protective against the NEW entry. A venue that
        // cannot hold one would leave a fill unprotected -- fail-closed and refuse, exactly as a send does.
        if (!_venue.Capabilities.Supports(VenueCapability.BracketOrders))
        {
            return ExecutionResult.RefusedByUnprotectableStop(
                $"Venue '{_venue.Id}' cannot hold an exchange-native protective stop, so a fill would be "
                + "unprotected. The modify was not sent.");
        }

        // A pure reprice holds size invariant, so it is sent as "leave unchanged" (null) rather than re-asserting the
        // value (gh#259 review, gh#270): re-sending a size can reset queue priority on some gateways -- the very thing
        // an in-place modify exists to keep -- and could re-inflate a partially-filled resting quantity in the
        // pre-journal window. A RESIZE must send the gate-approved size to change the quantity, accepting that
        // queue-priority tradeoff (the re-inflation hazard does not apply -- the caller refuses any non-Working, thus
        // partially-filled, order, gh#292). The size transmitted is the one the gate APPROVED, never the one asked.
        await _venue.ModifyOrderAsync(
            request.Account.Id,
            venueOrderId,
            LimitPriceFor(request),
            StopPriceFor(request),
            size: resize ? decision.ApprovedQuantity : null,
            cancellationToken);

        return ExecutionResult.Modified(decision);
    }

    /// <summary>
    /// Whether the request's parts describe the same trade, and what disagrees if not. Neither pairing is
    /// structurally enforced, and both fail in the same direction: the gate authorizes one thing and the venue
    /// receives another.
    /// </summary>
    private string? Mismatch(ExecutionRequest request)
    {
        // Handles collide freely across venues -- a bare "9001" is a different account at every broker. Letting
        // a foreign request through means relying on each adapter to notice: ProjectX throws from its mapping,
        // which is an exception rather than the refusal this path promises, and another executor need not check
        // at all.
        if (request.Account.Id.Venue != _venue.Id || request.Contract.Contract.Venue != _venue.Id)
        {
            return $"The request is tagged for venue '{request.Account.Id.Venue}' / "
                + $"'{request.Contract.Contract.Venue}' but the executor is '{_venue.Id}'. Nothing was sized.";
        }

        // Equity, drawdown floor and daily limits are all account-specific. Sizing against one account and
        // transmitting to another produces a quantity the target account never justified.
        if (request.Account.Id != request.Risk.Account)
        {
            return $"The risk snapshot describes account '{request.Risk.Account}' but the order would be sent to "
                + $"'{request.Account.Id}'. Nothing was sized.";
        }

        // Tick size and point value come from the proposal's instrument; the contract is what actually gets
        // traded. An ES-sized order on an NQ contract is authorized for exposure it does not have.
        return request.Contract.Instrument != request.Proposal.Instrument.Id
            ? $"The proposal is sized for '{request.Proposal.Instrument.Id}' but the contract was resolved for "
                + $"'{request.Contract.Instrument}'. Nothing was sized."
            : null;
    }

    /// <summary>
    /// Whether the neutral ticket can express this order type, and why not if it cannot. A <b>whitelist</b>:
    /// an unrecognized value — a numeric deserialization, a cast, a type added later without revisiting the
    /// price selection — must be refused rather than transmitted with whatever prices the switches happen to
    /// leave unset.
    /// </summary>
    private static string? Unrepresentable(OrderType type)
    {
        return type switch
        {
            OrderType.Market or OrderType.Limit or OrderType.Stop or OrderType.StopLimit => null,

            OrderType.TrailingStop =>
                "A trailing stop cannot be expressed: the ticket carries no trail distance. Stop management "
                + "arrives with staged stops (gh#11).",

            _ => $"Order type '{(int)type}' is not a recognized type and cannot be expressed.",
        };
    }

    /// <summary>
    /// Whether a take-profit target sits on the wrong side of entry, and why if so. A take-profit only means
    /// anything on the <b>winning</b> side — above entry for a long, below it for a short (the mirror of the
    /// staged stop's safety-beyond-actual ordering). A wrong-side target contradicts the position's direction,
    /// so it is refused rather than transmitted as a bracket that would take a loss or flipped into something the
    /// operator did not ask for. An absent target is nothing to check.
    /// </summary>
    private static string? WrongSideTarget(OrderProposal proposal)
    {
        if (proposal.Target is not { } target)
        {
            return null;
        }

        bool onWinningSide = proposal.Side switch
        {
            OrderSide.Buy => target.Value > proposal.Entry.Value,
            OrderSide.Sell => target.Value < proposal.Entry.Value,
            _ => false, // an unrecognized side is refused, never assumed (the fail-open lesson)
        };

        return onWinningSide
            ? null
            : $"A {proposal.Side} take-profit at {target.Value} is not on the winning side of entry "
                + $"{proposal.Entry.Value} — a long profits above entry, a short below it. Nothing was sized.";
    }

    private static Price? LimitPriceFor(ExecutionRequest request)
    {
        return request.Type switch
        {
            OrderType.Limit or OrderType.StopLimit => request.Proposal.Entry,
            _ => null,
        };
    }

    private static Price? StopPriceFor(ExecutionRequest request)
    {
        // TrailingStop is absent deliberately -- it never reaches here, and giving it the entry as an ordinary
        // stop price is what made it look representable.
        return request.Type switch
        {
            OrderType.Stop or OrderType.StopLimit => request.Proposal.Entry,
            _ => null,
        };
    }
}
