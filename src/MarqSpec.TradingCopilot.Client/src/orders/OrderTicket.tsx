import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useState } from 'react';

import {
  OrderType,
  SELECTABLE_ORDER_TYPES,
  type SendOrderRequest,
  type StagedOrderResponse,
  armOrder,
  cancelOrder,
  takeStagedOrder,
} from '../api/orders';
import { GateDecisionPanel } from './GateDecisionPanel';

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

  const handleArm = useCallback(() => {
    setRefusal(null);
    const { accountId, ...order } = proposal;
    void armOrder(accountId, { ...order, type }).then((result) => {
      if (!result.ok) {
        setRefusal(refusalText(result));
        return;
      }
      setStaged(result.data);
    });
  }, [proposal, type]);

  const handleSend = useCallback(() => {
    if (staged === null) {
      return;
    }
    setRefusal(null);
    void takeStagedOrder(staged.orderId).then((result) => {
      if (!result.ok) {
        // The staged row survives a refused send, so the ticket stays armed and the reason stays on screen.
        setRefusal(refusalText(result));
        return;
      }
      setSent(`Sent ${result.data.approvedQuantity} — ${result.data.reason}`);
      setStaged(null);
    });
  }, [staged]);

  const handleCancel = useCallback(() => {
    if (staged === null) {
      return;
    }
    void cancelOrder(staged.orderId).then(() => {
      setStaged(null);
      setRefusal(null);
    });
  }, [staged]);

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
        disabled={staged !== null}
      >
        {SELECTABLE_ORDER_TYPES.map((selectable) => (
          <option key={selectable} value={selectable}>
            {TYPE_LABELS[selectable]}
          </option>
        ))}
      </TextField>

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

      {refusal !== null ? <Alert severity="error">{refusal}</Alert> : null}
      {sent !== null ? <Alert severity="success">{sent}</Alert> : null}

      <Stack direction="row" spacing={1}>
        {staged === null ? (
          <Button variant="contained" onClick={handleArm}>
            Arm
          </Button>
        ) : (
          <>
            {isSendable(staged) ? (
              <Button variant="contained" color="primary" onClick={handleSend}>
                Send
              </Button>
            ) : null}
            <Button onClick={handleCancel}>Cancel</Button>
          </>
        )}
      </Stack>
    </Box>
  );
}

/**
 * Whether a staged decision may be sent. Mirrors `GateDecisionPanel`'s verdict: a pre-gate refusal never sized
 * anything, and a zero approval is "no trade" rather than an order of nothing.
 */
function isSendable(order: StagedOrderResponse): boolean {
  const evaluated = order.outcome === 'Allowed' || order.outcome === 'Resized';
  return evaluated && order.approvedQuantity > 0;
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
