import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useCallback, useRef, useState } from 'react';

import { cancelOrder, takeStagedOrder } from '../api/orders';
import type { StagedTicket } from '../api/suggestions';
import { GateDecisionPanel } from './GateDecisionPanel';
import { describeSizing } from './gateDecision';

/**
 * Review and send a ticket that a suggestion take **already armed** (gh#655, R-11 / R-12, ADR-0007) — the "where it
 * comes together" step the suggestion card hands off to (gh#654's `onArmed`).
 *
 * This is the *review → send* half of the order ticket for a ticket that is **already staged** server-side: the
 * suggestion take (`POST /suggestions/{id}/take`) staged it and returned the gate's decision, so there is nothing to
 * arm here. What must not be lost across the handoff is the one rule the order ticket exists to make visible —
 * **the size transmitted is the one the gate approved, never the one asked for** — so the resize is shown here too,
 * at the point of sending, not only on the card that armed it.
 *
 * **Send transmits the staged row by id.** `takeStagedOrder(orderId)` re-validates fresh and transmits (R-12); it
 * never re-arms or re-sends a proposal, so a resize cannot be undone by this surface. A refused send leaves the
 * ticket staged and the reason on screen — the operator decides whether to cancel. `onDone` fires once the ticket
 * has left this surface (sent or cancelled), so the host can return to the list.
 */
export interface StagedTicketReviewProps {
  readonly ticket: StagedTicket;
  /** What the operator asked for — the suggestion's size — so the gate's approved number can be shown against it. */
  readonly requested: number;
  /** A short human label for what was armed, e.g. `LONG MES`. */
  readonly summary?: string;
  /** Fired once the ticket has been sent or cancelled — the host drops the review and returns to the list. */
  readonly onDone: () => void;
}

export function StagedTicketReview({
  ticket,
  requested,
  summary,
  onDone,
}: StagedTicketReviewProps) {
  const [sent, setSent] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  // The re-entrancy guard is a ref, not the `pending` state: two clicks in one tick both read the old state, but a
  // ref is written synchronously and excludes the second. Order transmission is not idempotent (ProjectX ADR-0002),
  // so a double-click on Send could put the gate-approved size on the account twice.
  const inFlight = useRef(false);

  const begin = useCallback(() => {
    if (inFlight.current) {
      return false;
    }
    inFlight.current = true;
    setPending(true);
    return true;
  }, []);

  const handleSend = useCallback(() => {
    if (!begin()) {
      return; // never a second transmission of the same staged ticket
    }
    setRefusal(null);
    void takeStagedOrder(ticket.orderId)
      .then((result) => {
        if (!result.ok) {
          // The staged row survives a refused send, so the review stays open with the reason.
          setRefusal(result.kind === 'refused' ? result.reason : result.error);
          inFlight.current = false;
          setPending(false);
          return;
        }
        setSent(`Sent ${result.data.approvedQuantity} — ${result.data.reason}`);
        // Hold the confirmation until the operator acknowledges it: a real send must not vanish silently into a
        // refreshed list. `onDone` fires from the Done control below, which then hands back to the host.
      })
      .catch(() => {
        setRefusal('The request failed. Nothing was sent; try again.');
        inFlight.current = false;
        setPending(false);
      });
  }, [begin, ticket.orderId]);

  const handleCancel = useCallback(() => {
    if (!begin()) {
      return;
    }
    setRefusal(null);
    void cancelOrder(ticket.orderId).finally(() => {
      // Cancel is idempotent enough to treat as done regardless — the row is staged, not live. Hand back either way.
      onDone();
    });
  }, [begin, onDone, ticket.orderId]);

  const sizing = describeSizing(requested, ticket);

  return (
    <Box
      data-testid="staged-ticket-review"
      sx={{ display: 'flex', flexDirection: 'column', gap: 1.5, p: 2 }}
    >
      <Typography variant="subtitle2">
        Review &amp; send{summary === undefined ? '' : ` — ${summary}`}
      </Typography>

      <GateDecisionPanel
        requested={requested}
        decision={{
          outcome: ticket.outcome,
          approvedQuantity: ticket.approvedQuantity,
          bindingLayer: ticket.bindingLayer,
          reason: ticket.reason,
        }}
      />

      {refusal !== null ? <Alert severity="error">{refusal}</Alert> : null}
      {sent !== null ? <Alert severity="success">{sent}</Alert> : null}

      {sent === null ? (
        <Stack direction="row" spacing={1}>
          {sizing.sendable ? (
            <Button variant="contained" color="primary" onClick={handleSend} disabled={pending}>
              Send
            </Button>
          ) : null}
          <Button onClick={handleCancel} disabled={pending}>
            Cancel
          </Button>
        </Stack>
      ) : (
        // The order is gone; the only thing left is to acknowledge and return to the list.
        <Stack direction="row" spacing={1}>
          <Button variant="contained" onClick={onDone}>
            Done
          </Button>
        </Stack>
      )}
    </Box>
  );
}
