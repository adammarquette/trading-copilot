import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useRef, useState } from 'react';

import {
  ConditionalCrossDirection,
  type ConditionalOrderResponse,
  type CreateConditionalOrderRequest,
  OrderType,
  SELECTABLE_ORDER_TYPES,
  type SendOrderRequest,
  type StagedOrderResponse,
  armOrder,
  cancelConditionalOrder,
  cancelOrder,
  createConditionalOrder,
  takeStagedOrder,
} from '../api/orders';
import { ConditionalPending } from './ConditionalPending';
import { SELECTABLE_DIRECTIONS, directionLabel, isTriggerFireable } from './conditional';
import { GateDecisionPanel } from './GateDecisionPanel';
import { describeSizing } from './gateDecision';

/**
 * The order ticket (gh#655, R-11 / R-12, ADR-0007) — the surface that sends real orders.
 *
 * **Arm → review → send stays three steps, and the middle one is the point.** Arming stages the order and returns
 * the gate's decision *without* reaching the venue; sending is a separate, explicit action against the staged row.
 * That opt-in posture is R-11's, and collapsing it into one click for convenience would make the gate's decision
 * something the operator skips past rather than reads.
 *
 * **Send transmits the staged order, never the proposal.** The staged row already carries the gate-approved size;
 * re-sending the proposal would transmit the *requested* quantity and make the gate advisory — the failure
 * ADR-0007 names directly. `GateDecisionPanel` owns whether a decision is sendable at all, so the send control
 * follows the gate's verdict rather than the operator's intent.
 *
 * **`TrailingStop` is never offered.** The send path refuses it outright and this neutral ticket carries no trail
 * distance, so a control offering it would always fail — absent beats always-failing. That exclusion lives in
 * `SELECTABLE_ORDER_TYPES` rather than here, so it cannot drift.
 *
 * A refusal — of the arm or of the send — is rendered, not swallowed: a refused send leaves the ticket **armed**,
 * because the staged row still exists server-side and the operator needs the reason in front of them to decide
 * whether to amend or cancel.
 */

/** The entry the operator is proposing, plus the account it belongs to. */
export interface OrderProposal extends SendOrderRequest {
  readonly accountId: string;
}

const TYPE_LABELS: Readonly<Record<number, string>> = {
  [OrderType.Market]: 'Market',
  [OrderType.Limit]: 'Limit',
  [OrderType.Stop]: 'Stop',
  [OrderType.StopLimit]: 'Stop limit',
};

export function OrderTicket({ proposal }: { readonly proposal: OrderProposal }) {
  const [staged, setStaged] = useState<StagedOrderResponse | null>(null);
  const [sent, setSent] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [type, setType] = useState<OrderType>(proposal.type);
  const [pending, setPending] = useState(false);

  // "Send when conditions met" (gh#655, R-11 / R-12): the opt-in on-trigger mode. Off by default, so the common
  // path stays the three-step arm → review → send. The trigger is drafted as text and parsed on submit; the
  // direction defaults to a breakout (rises to), and the cancel band / expiry are optional.
  const [whenConditionsMet, setWhenConditionsMet] = useState(false);
  const [conditional, setConditional] = useState<ConditionalOrderResponse | null>(null);
  const [triggerDraft, setTriggerDraft] = useState('');
  const [direction, setDirection] = useState<ConditionalCrossDirection>(
    ConditionalCrossDirection.RisesTo,
  );
  const [expiryDraft, setExpiryDraft] = useState('');
  const [cancelDraft, setCancelDraft] = useState('');
  const triggerPrice = Number(triggerDraft);
  // A blank cancel band is fine — it is optional. A non-blank one that is not a finite number is NOT: `Number('5,0')`
  // is `NaN`, which `JSON.stringify` serializes to `null`, silently discarding the operator's stale-cancel intent
  // with no error. Fail closed instead — the create control stays disabled until it is a real number or cleared.
  const cancelBandValid = cancelDraft.trim() === '' || Number.isFinite(Number(cancelDraft));
  const canCreateConditional = isTriggerFireable(triggerPrice, direction) && cancelBandValid;
  /**
   * The re-entrancy guard, a REF rather than the `pending` state above. State updates are not applied until the
   * next render, so two clicks dispatched in the same tick would both read the old value and both fire; a ref is
   * written synchronously and so actually excludes the second. `pending` exists to disable the controls -- that
   * is the visible half, this is the correct half.
   *
   * It matters most on Send: order transmission is not idempotent (ProjectX ADR-0002 -- "a retried timeout can
   * place a second live order"), so a double-click could put the gate-approved size on the account twice.
   */
  const inFlight = useRef(false);

  const begin = useCallback(() => {
    if (inFlight.current) {
      return false;
    }
    inFlight.current = true;
    setPending(true);
    return true;
  }, []);

  const finish = useCallback(() => {
    inFlight.current = false;
    setPending(false);
  }, []);

  // Switching modes clears any stale alert, so a prior "Sent …" success or a refusal from the other flow never
  // lingers beside the mode the operator just moved to. Only reachable pre-flight — the toggle is locked while a
  // request is in flight and once either flow has produced a staged order or a pending conditional.
  const handleToggleMode = useCallback((checked: boolean) => {
    setWhenConditionsMet(checked);
    setRefusal(null);
    setSent(null);
  }, []);

  const handleArm = useCallback(() => {
    if (!begin()) {
      return; // a second arm would stage a SECOND order and orphan whichever row resolved first
    }
    setRefusal(null);
    setSent(null);
    const { accountId, ...order } = proposal;
    void armOrder(accountId, { ...order, type })
      .then((result) => {
        if (!result.ok) {
          setRefusal(refusalText(result));
          return;
        }
        setStaged(result.data);
      })
      .finally(finish);
  }, [begin, finish, proposal, type]);

  const handleSend = useCallback(() => {
    if (staged === null || !begin()) {
      return; // never a second transmission of the same staged order
    }
    setRefusal(null);
    void takeStagedOrder(staged.orderId)
      .then((result) => {
        if (!result.ok) {
          // The staged row survives a refused send, so the ticket stays armed and the reason stays on screen.
          setRefusal(refusalText(result));
          return;
        }
        setSent(`Sent ${result.data.approvedQuantity} — ${result.data.reason}`);
        setStaged(null);
      })
      .finally(finish);
  }, [begin, finish, staged]);

  const handleCancel = useCallback(() => {
    if (staged === null || !begin()) {
      return;
    }
    void cancelOrder(staged.orderId)
      .then(() => {
        setStaged(null);
        setRefusal(null);
      })
      .finally(finish);
  }, [begin, finish, staged]);

  const handleCreateConditional = useCallback(() => {
    // Fail closed on an unfireable trigger or an invalid cancel band even though the control is disabled for them —
    // a conditional that could never fire would rest forever, and a NaN cancel band would silently serialize to null.
    if (!isTriggerFireable(triggerPrice, direction) || !cancelBandValid) {
      return;
    }
    const { accountId, ...order } = proposal;
    // Build the request BEFORE begin(). Every conversion here is synchronous, and a throw (an Invalid Date from a
    // malformed expiry) must happen before the re-entrancy guard is set — otherwise `finish` never runs and the
    // ticket wedges disabled. begin() is the last thing before the transmit-nothing round-trip.
    const request: CreateConditionalOrderRequest = {
      order: { ...order, type },
      triggerPrice,
      triggerDirection: direction,
      cancelDrift: cancelDraft.trim() === '' ? undefined : Number(cancelDraft),
      expiresAt: expiryDraft.trim() === '' ? undefined : new Date(expiryDraft).toISOString(),
    };
    if (!begin()) {
      return; // never a second pending conditional for the same proposal
    }
    setRefusal(null);
    setSent(null);
    void createConditionalOrder(accountId, request)
      .then((result) => {
        if (!result.ok) {
          // A pre-gate refusal (mode / mismatch / wrong-side) means nothing coherent was held — show it, do not
          // pretend a conditional is pending.
          setRefusal(refusalText(result));
          return;
        }
        setConditional(result.data);
      })
      .finally(finish);
  }, [
    begin,
    finish,
    proposal,
    type,
    triggerPrice,
    direction,
    cancelBandValid,
    cancelDraft,
    expiryDraft,
  ]);

  const handleWithdrawConditional = useCallback(() => {
    if (conditional === null || !begin()) {
      return; // never a second DELETE of the same conditional
    }
    setRefusal(null);
    void cancelConditionalOrder(conditional.conditionalOrderId)
      .then((result) => {
        if (!result.ok) {
          // A refused withdrawal (a mid-fire conditional the server will not cancel) leaves the pending panel up —
          // the conditional is still live, so the surface must not imply it is gone. The reason stays on screen.
          setRefusal(refusalText(result));
          return;
        }
        // The row is gone server-side; clear the panel. `whenConditionsMet` stays on, so the trigger form returns
        // and a fresh conditional can be composed without re-toggling the mode.
        setConditional(null);
      })
      .finally(finish);
  }, [begin, finish, conditional]);

  return (
    <Box
      data-testid="order-ticket"
      sx={{ display: 'flex', flexDirection: 'column', gap: 1.5, maxWidth: 460 }}
    >
      <Typography variant="subtitle2">
        {proposal.symbol} — {proposal.quantity} @ {proposal.entry}
      </Typography>

      {/* The catastrophic floor rests at the venue on every order (R-5). Stated, never assumed: "insured" is the
          promise this ticket makes, and an operator should be able to read it rather than trust it. */}
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        Stop {proposal.stop} · safety stop {proposal.safetyStop} (always at the venue)
      </Typography>

      <TextField
        select
        size="small"
        label="Type"
        value={type}
        onChange={(event) => setType(Number(event.target.value) as OrderType)}
        slotProps={{ select: { native: true } }}
        disabled={pending || staged !== null || conditional !== null}
      >
        {SELECTABLE_ORDER_TYPES.map((selectable) => (
          <option key={selectable} value={selectable}>
            {TYPE_LABELS[selectable]}
          </option>
        ))}
      </TextField>

      {/* The opt-in on-trigger mode (R-11 / R-12). Locked while a request is in flight AND once either flow has
          produced a staged order or a created conditional — so a mid-round-trip toggle cannot land an arm's result
          in the conditional branch (orphaning the staged row) or an immediate order on top of a pending conditional. */}
      <FormControlLabel
        control={
          <Checkbox
            size="small"
            checked={whenConditionsMet}
            onChange={(event) => handleToggleMode(event.target.checked)}
            disabled={pending || staged !== null || conditional !== null}
          />
        }
        label="Send when conditions met"
      />

      {whenConditionsMet && conditional === null ? (
        <>
          <TextField
            size="small"
            label="Trigger price"
            value={triggerDraft}
            onChange={(event) => setTriggerDraft(event.target.value)}
            inputMode="decimal"
          />
          <TextField
            select
            size="small"
            label="Direction"
            value={direction}
            onChange={(event) =>
              setDirection(Number(event.target.value) as ConditionalCrossDirection)
            }
            slotProps={{ select: { native: true } }}
          >
            {SELECTABLE_DIRECTIONS.map((selectable) => (
              <option key={selectable} value={selectable}>
                {directionLabel(selectable)}
              </option>
            ))}
          </TextField>
          <TextField
            size="small"
            type="datetime-local"
            label="Expiry (optional)"
            value={expiryDraft}
            onChange={(event) => setExpiryDraft(event.target.value)}
            slotProps={{ inputLabel: { shrink: true } }}
          />
          <TextField
            size="small"
            label="Cancel band (optional)"
            value={cancelDraft}
            onChange={(event) => setCancelDraft(event.target.value)}
            inputMode="decimal"
          />
        </>
      ) : null}

      {staged !== null ? (
        <GateDecisionPanel
          requested={proposal.quantity}
          decision={{
            outcome: staged.outcome,
            approvedQuantity: staged.approvedQuantity,
            bindingLayer: staged.bindingLayer,
            reason: staged.reason,
          }}
        />
      ) : null}

      {conditional !== null ? <ConditionalPending conditional={conditional} /> : null}

      {refusal !== null ? <Alert severity="error">{refusal}</Alert> : null}
      {sent !== null ? <Alert severity="success">{sent}</Alert> : null}

      <Stack direction="row" spacing={1}>
        {whenConditionsMet ? (
          conditional === null ? (
            <Button
              variant="contained"
              onClick={handleCreateConditional}
              disabled={pending || !canCreateConditional}
            >
              Send on trigger
            </Button>
          ) : (
            // A pending conditional will place a real order when it fires (R-12), so the operator can pull it back
            // before then. It is held off the book, so this is a plain server-side delete — nothing rests to cancel.
            <Button onClick={handleWithdrawConditional} disabled={pending}>
              Withdraw
            </Button>
          )
        ) : staged === null ? (
          <Button variant="contained" onClick={handleArm} disabled={pending}>
            Arm
          </Button>
        ) : (
          <>
            {describeSizing(proposal.quantity, staged).sendable ? (
              <Button variant="contained" color="primary" onClick={handleSend} disabled={pending}>
                Send
              </Button>
            ) : null}
            <Button onClick={handleCancel} disabled={pending}>
              Cancel
            </Button>
          </>
        )}
      </Stack>
    </Box>
  );
}

function refusalText(result: {
  readonly kind: string;
  readonly error?: string;
  readonly reason?: string;
}): string {
  return result.kind === 'refused'
    ? (result.reason ?? 'Refused.')
    : (result.error ?? 'The request failed.');
}
