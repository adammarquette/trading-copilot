import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import {
  type BlotterPosition,
  type BlotterRestingOrder,
  type VenueView,
  getPositions,
  getRestingOrders,
} from '../api/blotter';
import { cancelOrder } from '../api/orders';
import { useOptionalRealtime } from '../realtime/RealtimeProvider';

/**
 * The live blotter (gh#656, R-11 / R-3, ADR-0013) — positions and resting orders from **venue truth**.
 *
 * **An outage must never read as flat.** `unknown` is rendered as *unknown*: not as an empty table, not as the
 * last good snapshot, not as a spinner that looks like loading. `PositionReconciliationService` goes to real
 * trouble to declare unknown rather than present a stale live view, and collapsing the two here would throw that
 * away at the exact moment it matters — because "no open positions" reads as **safe**.
 *
 * The converse matters too: a genuinely empty **live** view is real information and is stated plainly. Hedging it
 * into "unknown" would teach the operator to ignore the warning that counts.
 *
 * **Realtime is a refresh signal, not a second source of truth.** Order-state and fill pushes, and a reconnect's
 * resync, all trigger a **re-read** rather than being folded into local state. Venue truth stays the single
 * source, there is no second state machine to drift, and a replayed push and a live one behave identically.
 *
 * **Reads are ordered by generation.** They overlap here by design — every push starts one — so an older answer
 * landing late must not overwrite a newer one, which on this surface could put a closed position back on screen
 * or drop a live one.
 */

type Protection = 'protected' | 'unprotected' | 'unknown';

export function Blotter({ accountId }: { readonly accountId: string }) {
  const [positionView, setPositionView] = useState<VenueView<BlotterPosition> | null>(null);
  const [orderView, setOrderView] = useState<VenueView<BlotterRestingOrder> | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [confirming, setConfirming] = useState<BlotterRestingOrder | null>(null);
  const realtime = useOptionalRealtime();
  const mounted = useRef(true);
  const latest = useRef(0);
  /** Synchronous re-entrancy guard -- a `pending` state would not exclude two clicks in one tick. */
  const cancelling = useRef(false);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    const generation = ++latest.current;

    void Promise.all([getPositions(accountId), getRestingOrders(accountId)]).then(
      ([positions, orders]) => {
        if (!mounted.current || generation !== latest.current) {
          return; // superseded by a newer read
        }
        // A failed read is "unavailable", never an empty account -- the same reason unknown is never flat.
        setUnavailable(!positions.ok || !orders.ok);
        if (positions.ok) {
          setPositionView(positions.data);
        }
        if (orders.ok) {
          setOrderView(orders.data);
        }
      },
    );
  }, [accountId]);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    if (realtime === null) {
      return;
    }
    // Every one of these means venue truth may have moved. None of them carries enough to BE the truth.
    const stops = [realtime.onOrderState(load), realtime.onFill(load), realtime.onResync(load)];
    return () => stops.forEach((stop) => stop());
  }, [realtime, load]);

  const confirmCancel = useCallback(() => {
    if (confirming === null || cancelling.current) {
      return; // never a second cancel of the same order
    }
    cancelling.current = true;
    const key = confirming.venueOrderKey;

    void cancelOrder(key)
      .then(() => {
        setConfirming(null);
        // Re-read rather than assume it worked: venue truth decides what is standing, not this click.
        load();
      })
      .finally(() => {
        cancelling.current = false;
      });
  }, [confirming, load]);

  const protectionOf = useCallback(
    (position: BlotterPosition): Protection => {
      // Absence of evidence is not evidence of absence: with no trustworthy orders view, protection is UNKNOWN.
      // Claiming "unprotected" would raise a false alarm; claiming "protected" would hide a real one.
      if (orderView === null || orderView.basis === 'unknown') {
        return 'unknown';
      }
      return orderView.items.some(
        (order) => order.contract === position.contract && order.isProtective,
      )
        ? 'protected'
        : 'unprotected';
    },
    [orderView],
  );

  return (
    <Box data-testid="blotter" sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      {unavailable ? (
        <Alert severity="error">The venue read is unavailable — this view is not current.</Alert>
      ) : null}

      <Box>
        <Typography variant="subtitle2" gutterBottom>
          Positions
        </Typography>
        {positionView === null ? (
          <Typography variant="body2">Reading…</Typography>
        ) : positionView.basis === 'unknown' ? (
          <Alert severity="warning" data-testid="positions-unknown">
            <strong>Position state UNKNOWN.</strong> The venue could not be reached, so this is not
            a statement that you are flat — you may hold open risk that cannot be shown here.
          </Alert>
        ) : positionView.items.length === 0 ? (
          <Typography variant="body2">No open positions.</Typography>
        ) : (
          <>
            {positionView.basis === 'settlement' ? (
              <Alert severity="info" sx={{ mb: 1 }}>
                Marked at <strong>settlement</strong> — a re-mark inside the maintenance window, not
                live movement.
              </Alert>
            ) : null}
            {positionView.items.map((position) => (
              <Box
                key={position.contract}
                data-protection={protectionOf(position)}
                sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.5 }}
              >
                <Typography variant="body2" sx={{ flex: 1 }}>
                  {position.contract} · {position.netQuantity} @ {position.averagePrice}
                </Typography>
                <ProtectionChip state={protectionOf(position)} />
              </Box>
            ))}
          </>
        )}
      </Box>

      <Box data-testid="resting-orders">
        <Typography variant="subtitle2" gutterBottom>
          Resting orders
        </Typography>
        {orderView === null ? (
          <Typography variant="body2">Reading…</Typography>
        ) : orderView.basis === 'unknown' ? (
          <Alert severity="warning">
            <strong>Resting orders UNKNOWN.</strong> The venue could not be reached — orders may be
            standing that cannot be shown here.
          </Alert>
        ) : orderView.items.length === 0 ? (
          <Typography variant="body2">No resting orders.</Typography>
        ) : (
          orderView.items.map((order) => (
            <Box
              key={order.venueOrderKey}
              sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.5 }}
            >
              <Typography variant="body2" sx={{ flex: 1 }}>
                {order.contract} · {order.size} @ {order.stopPrice ?? order.limitPrice}
                {order.isProtective ? ' · protective' : ''}
              </Typography>
              <Button size="small" color="error" onClick={() => setConfirming(order)}>
                Cancel
              </Button>
            </Box>
          ))
        )}
      </Box>
      <Dialog open={confirming !== null} onClose={() => setConfirming(null)}>
        <DialogTitle>Cancel this resting order?</DialogTitle>
        <DialogContent>
          {confirming !== null ? (
            <>
              {/* Names the SPECIFIC order, never a generic "are you sure": a blotter carries several at once,
                  and that is exactly how the wrong one gets pulled. */}
              <Typography variant="body2" gutterBottom>
                <strong>{confirming.contract}</strong> · {confirming.size} contract
                {confirming.size === 1 ? '' : 's'} @ {confirming.stopPrice ?? confirming.limitPrice}
              </Typography>
              {confirming.isProtective ? (
                <Alert severity="error">
                  This is a <strong>protective</strong> order. Cancelling it leaves the position on
                  this contract without that stop standing at the venue.
                </Alert>
              ) : null}
            </>
          ) : null}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirming(null)}>Keep it</Button>
          <Button color="error" variant="contained" onClick={confirmCancel}>
            Cancel this order
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}

function ProtectionChip({ state }: { readonly state: Protection }) {
  switch (state) {
    case 'protected':
      return <Chip size="small" color="success" label="Protected" />;
    case 'unprotected':
      // What ProtectionMonitorHost pages P1 on (gh#370) — the operator should see what it sees.
      return <Chip size="small" color="error" label="UNPROTECTED" />;
    default:
      return <Chip size="small" color="warning" label="Protection unknown" />;
  }
}
