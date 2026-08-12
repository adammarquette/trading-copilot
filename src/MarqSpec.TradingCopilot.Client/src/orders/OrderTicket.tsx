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
  OrderType,
  SELECTABLE_ORDER_TYPES,
  type SendOrderRequest,
  type StagedOrderResponse,
  armOrder,
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

  const handleArm = useCallback(() => {
    if (!begin()) {
      return; // a second arm would stage a SECOND order and orphan whichever row resolved first
    }
    setRefusal(null);
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
    // Fail closed on an unfireable trigger even though the control is disabled for it — a conditional that could
    // never fire would rest forever, and the same non-idempotence guard the arm/send paths carry applies here.
    if (!isTriggerFireable(triggerPrice, direction) || !begin()) {
      return;
    }
    setRefusal(null);
    const { accountId, ...order } = proposal;
    void createConditionalOrder(accountId, {
      order: { ...order, type },
      triggerPrice,
      triggerDirection: direction,
      cancelDrift: cancelDraft.trim() === '' ? undefined : Number(cancelDraft),
      expiresAt: expiryDraft.trim() === '' ? undefined : new Date(expiryDraft).toISOString(),
    })
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
  }, [begin, finish, proposal, type, triggerPrice, direction, cancelDraft, expiryDraft]);

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
        disabled={staged !== null || conditional !== null}
      >
        {SELECTABLE_ORDER_TYPES.map((selectable) => (
          <option key={selectable} value={selectable}>
            {TYPE_LABELS[selectable]}
          </option>
        ))}
      </TextField>

      {/* The opt-in on-trigger mode (R-11 / R-12). Locked once either flow has started, so the operator cannot
          flip the ticket's meaning out from under a staged order or a created conditional. */}
      <FormControlLabel
        control={
          <Checkbox
            size="small"
            checked={whenConditionsMet}
            onChange={(event) => setWhenConditionsMet(event.target.checked)}
            disabled={staged !== null || conditional !== null}
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
              disabled={pending || !isTriggerFireable(triggerPrice, direction)}
            >
              Send on trigger
            </Button>
          ) : null
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
