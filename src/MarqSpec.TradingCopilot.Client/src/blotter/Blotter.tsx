import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import {
  type BlotterPosition,
  type BlotterRestingOrder,
  type VenueView,
  exitPosition,
  getPositions,
  getRestingOrders,
} from '../api/blotter';
import { cancelOrder, repriceOrder } from '../api/orders';
import { useOptionalRealtime } from '../realtime/RealtimeProvider';

/**
 * Reads an operator-typed price strictly. A reprice reaches a risk calculation (R-16), so a mistyped price must
 * not be read leniently — `Number.parseFloat('52ab')` is `52`, and that is the one input here that must not slip.
 */
function parsePrice(text: string): number | null {
  const trimmed = text.trim();
  if (!/^\d+(?:\.\d+)?$/.test(trimmed)) {
    return null;
  }
  const value = Number(trimmed);
  return value > 0 ? value : null;
}

function refusalText(result: {
  readonly kind: string;
  readonly reason?: string;
  readonly error?: string;
}): string {
  return result.kind === 'refused'
    ? (result.reason ?? 'Refused.')
    : (result.error ?? 'The request failed.');
}

/**
 * The `safety → working → entry` band a hidden working stop may move strictly inside (gh#865, ADR-0007). **Side is
 * not on the venue-truth record**, so direction is inferred from the band's geometry: for a long the entry sits
 * above the safety floor, for a short below it — either way the working stop must lie strictly between the two.
 * Returns `null` when the order carries no stop plan (the four platform-held fields are absent as a whole), so a
 * non-`Hidden` order has no band and no move. This mirrors the server's authoritative check; it is UX, not the
 * guard — the re-stage re-validates the same band, the `Hidden` gate and any widen fail-closed.
 */
function stopBand(
  order: BlotterRestingOrder,
): { readonly low: number; readonly high: number } | null {
  if (order.safetyStopPrice === undefined || order.entryPrice === undefined) {
    return null;
  }
  return {
    low: Math.min(order.safetyStopPrice, order.entryPrice),
    high: Math.max(order.safetyStopPrice, order.entryPrice),
  };
}

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
  const [exiting, setExiting] = useState<BlotterPosition | null>(null);
  const [exitFailure, setExitFailure] = useState<string | null>(null);
  // The reprice sheet: the order being repriced, the operator's new entry, and the current market price the R-16
  // fat-finger band re-measures against (there is no server-side quote read, so the operator supplies it — the same
  // posture the suggestion card takes). A refusal from the re-gate stays on the sheet rather than closing it.
  const [repricing, setRepricing] = useState<BlotterRestingOrder | null>(null);
  const [newEntryDraft, setNewEntryDraft] = useState('');
  const [marketDraft, setMarketDraft] = useState('');
  const [repriceRefusal, setRepriceRefusal] = useState<string | null>(null);
  // The move-stop sheet: the order whose HIDDEN working stop is being moved, the operator's new stop, and a refusal
  // that stays on the sheet. Unlike a reprice this is a LOCAL re-stage — no market reference — so there is one input.
  const [movingStop, setMovingStop] = useState<BlotterRestingOrder | null>(null);
  const [newWorkingDraft, setNewWorkingDraft] = useState('');
  const [moveStopRefusal, setMoveStopRefusal] = useState<string | null>(null);
  const realtime = useOptionalRealtime();
  const mounted = useRef(true);
  const latest = useRef(0);
  /** Synchronous re-entrancy guard -- a `pending` state would not exclude two clicks in one tick. */
  const cancelling = useRef(false);
  const closing = useRef(false);
  const repricingInFlight = useRef(false);
  const movingStopInFlight = useRef(false);

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
    // A leg with no journaled order id is never confirmable (its Cancel is disabled), so this is a belt-and-braces
    // guard: cancelling by the app Order.Id the endpoint routes on, never the venue key it cannot.
    if (confirming === null || confirming.orderId === null || cancelling.current) {
      return; // never a second cancel of the same order
    }
    cancelling.current = true;
    const orderId = confirming.orderId;

    void cancelOrder(orderId)
      .then(() => {
        setConfirming(null);
        // Re-read rather than assume it worked: venue truth decides what is standing, not this click.
        load();
      })
      .finally(() => {
        cancelling.current = false;
      });
  }, [confirming, load]);

  const openReprice = useCallback((order: BlotterRestingOrder) => {
    setRepricing(order);
    setRepriceRefusal(null);
    // Seed the new entry with the order's current resting price so the field is a nudge, not a blank; the market
    // price stays empty — it must be a fresh operator observation, never defaulted (that would disable the R-16
    // drift re-check, the exact mistake the suggestion card's reference input avoids).
    setNewEntryDraft(String(order.limitPrice ?? order.stopPrice ?? ''));
    setMarketDraft('');
  }, []);

  const closeReprice = useCallback(() => {
    setRepricing(null);
    setRepriceRefusal(null);
  }, []);

  const openMoveStop = useCallback((order: BlotterRestingOrder) => {
    setMovingStop(order);
    setMoveStopRefusal(null);
    // Seed the field with the current hidden stop so it reads as a nudge, not a blank.
    setNewWorkingDraft(String(order.workingStopPrice ?? ''));
  }, []);

  const closeMoveStop = useCallback(() => {
    setMovingStop(null);
    setMoveStopRefusal(null);
  }, []);

  const newEntry = parsePrice(newEntryDraft);
  const market = parsePrice(marketDraft);
  // An entry move re-gates and REQUIRES a reference price server-side (R-16), so the action stays disabled until
  // both are real positive numbers -- fail closed rather than let the server refuse for a missing/mistyped field.
  const canReprice = newEntry !== null && market !== null;

  const newWorking = parsePrice(newWorkingDraft);
  const moveBand = movingStop !== null ? stopBand(movingStop) : null;
  // The move is allowed only STRICTLY inside the safety→entry band. This is UX: the server re-validates the same
  // band (and any widen, and the Hidden gate) fail-closed, so an out-of-band or lost-race value never reaches the venue.
  const workingInBand =
    newWorking !== null &&
    moveBand !== null &&
    newWorking > moveBand.low &&
    newWorking < moveBand.high;
  const workingOutOfBand = newWorking !== null && moveBand !== null && !workingInBand;

  const confirmReprice = useCallback(() => {
    if (repricing === null || repricing.orderId === null || repricingInFlight.current) {
      return; // never a second reprice of the same order
    }
    if (newEntry === null || market === null) {
      return; // fail closed on an unparseable price even though the control is disabled for it
    }
    repricingInFlight.current = true;
    setRepriceRefusal(null);
    const orderId = repricing.orderId;

    void repriceOrder(orderId, { entryPrice: newEntry, referencePrice: market })
      .then((result) => {
        if (!result.ok) {
          // The re-gate's answer (a breach, a fat-finger band) stays on the sheet -- the order did not move, and
          // the operator needs the reason to decide, exactly as the order ticket keeps a refusal on screen.
          setRepriceRefusal(refusalText(result));
          return;
        }
        setRepricing(null);
        // Re-read rather than assume the new price took: venue truth decides what is resting, not this click.
        load();
      })
      .finally(() => {
        repricingInFlight.current = false;
      });
  }, [repricing, newEntry, market, load]);

  const confirmMoveStop = useCallback(() => {
    if (movingStop === null || movingStop.orderId === null || movingStopInFlight.current) {
      return; // never a second move of the same stop
    }
    if (newWorking === null || !workingInBand) {
      return; // fail closed on an out-of-band or unparseable price even though the control is disabled for it
    }
    movingStopInFlight.current = true;
    setMoveStopRefusal(null);
    const orderId = movingStop.orderId;

    // A hidden re-stage is a LOCAL write — no reference price — so only the working stop is sent. Naming a field is a
    // request to move it, so entry/market are deliberately absent.
    void repriceOrder(orderId, { workingStopPrice: newWorking })
      .then((result) => {
        if (!result.ok) {
          // The server's answer -- a band breach, or a lost race where the stop promoted to a native venue order --
          // stays on the sheet: the stop did not move, and the operator needs the reason to decide.
          setMoveStopRefusal(refusalText(result));
          return;
        }
        setMovingStop(null);
        // Re-read rather than assume the stop moved: platform/venue truth decides what is staged, not this click.
        load();
      })
      .finally(() => {
        movingStopInFlight.current = false;
      });
  }, [movingStop, newWorking, workingInBand, load]);

  const confirmExit = useCallback(() => {
    if (exiting === null || closing.current) {
      return; // closing at market is not idempotent -- never a second attempt from a second click
    }
    closing.current = true;
    const contract = exiting.contract;

    void exitPosition(accountId, contract)
      .then((result) => {
        if (!result.ok) {
          // StillOpen and Unreachable both arrive here. Neither is "done": the position may still be live, and
          // saying otherwise would stop the operator watching it.
          setExitFailure(
            `Exit of ${contract} did NOT complete (${result.kind === 'refused' ? result.reason : result.error}). ` +
              'The position may still be open — check before walking away.',
          );
          return;
        }
        setExitFailure(null);
        setExiting(null);
      })
      .finally(() => {
        closing.current = false;
        // Re-read either way: venue truth decides what is open, not the outcome of this click.
        load();
      });
  }, [accountId, exiting, load]);

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
                <Button
                  size="small"
                  color="error"
                  onClick={() => {
                    setExitFailure(null); // a previous failure must not read as this position's result
                    setExiting(position);
                  }}
                >
                  Exit
                </Button>
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
              {order.orderId === null ? (
                // A venue-spawned leg carries no journaled Order row (ADR-0007), so /orders/{id} cannot reach it —
                // rather than a Cancel that would 404, say why it is not actionable here. Managing a bracket's leg
                // is done by exiting or adjusting its position (a separate, venue-key path — gh#656 follow-up).
                <Typography
                  variant="caption"
                  sx={{ color: 'text.disabled' }}
                  data-testid="leg-not-actionable"
                >
                  bracket leg — manage via its position
                </Typography>
              ) : (
                <>
                  <Button size="small" onClick={() => openReprice(order)}>
                    Reprice
                  </Button>
                  {/* Only a HIDDEN working stop is locally movable (gh#865, ADR-0007): Native rests at the venue,
                      Orphaned re-arms on reconnect, and an order with no stop plan has none. The read carries the
                      staging, so the gate is exact rather than a guess. */}
                  {order.stopStaging === 'Hidden' ? (
                    <Button size="small" onClick={() => openMoveStop(order)}>
                      Move stop
                    </Button>
                  ) : null}
                  <Button size="small" color="error" onClick={() => setConfirming(order)}>
                    Cancel
                  </Button>
                </>
              )}
            </Box>
          ))
        )}
      </Box>
      <Dialog open={exiting !== null} onClose={() => setExiting(null)}>
        <DialogTitle>Exit this position?</DialogTitle>
        <DialogContent>
          {exiting !== null ? (
            <>
              {/* Names the position, its size and what will actually happen to it. Closing at market is the
                  most destructive thing this surface does, so "which one" and "how" are both stated. */}
              <Typography variant="body2" gutterBottom>
                <strong>{exiting.contract}</strong> · {exiting.netQuantity} @ {exiting.averagePrice}
              </Typography>
              <Typography variant="body2" gutterBottom>
                This closes the position <strong>at market</strong>, now.
              </Typography>
              {protectionOf(exiting) === 'unknown' ? (
                <Alert severity="warning">
                  Protection state is unknown for this contract, so what is standing at the venue
                  cannot be confirmed from here.
                </Alert>
              ) : null}
              {/* INSIDE the dialog, not beside it. The dialog stays open on a failed exit, and MUI's
                  backdrop would hide a sibling Alert entirely -- so the one message telling the operator
                  the position may still be live would be invisible until they dismissed the dialog. Same
                  placement as repriceRefusal, for the same reason (#851 review). */}
              {exitFailure !== null ? (
                <Alert severity="error" sx={{ mt: 1 }}>
                  {exitFailure}
                </Alert>
              ) : null}
            </>
          ) : null}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setExiting(null)}>Keep it</Button>
          <Button color="error" variant="contained" onClick={confirmExit}>
            Exit this position
          </Button>
        </DialogActions>
      </Dialog>

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

      <Dialog open={repricing !== null} onClose={closeReprice}>
        <DialogTitle>Reprice this order?</DialogTitle>
        <DialogContent>
          {repricing !== null ? (
            <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1.5, pt: 0.5 }}>
              {/* Names the SPECIFIC order and its current resting price, like the cancel dialog — the same blotter
                  carries several at once, and repricing the wrong one is the same class of mistake. */}
              <Typography variant="body2">
                <strong>{repricing.contract}</strong> · {repricing.size} contract
                {repricing.size === 1 ? '' : 's'} · now at{' '}
                {repricing.limitPrice ?? repricing.stopPrice}
              </Typography>
              <TextField
                size="small"
                label="New entry price"
                value={newEntryDraft}
                onChange={(event) => setNewEntryDraft(event.target.value)}
                slotProps={{
                  htmlInput: { inputMode: 'decimal', 'data-testid': 'new-entry-price' },
                }}
              />
              <TextField
                size="small"
                label="Market price now"
                required
                value={marketDraft}
                onChange={(event) => setMarketDraft(event.target.value)}
                slotProps={{
                  htmlInput: { inputMode: 'decimal', 'data-testid': 'reprice-reference' },
                }}
                helperText="The move re-gates against this (R-16) — enter the price you can see; it is not fetched."
              />
              {/* The promise the modify sheet makes: the safety stop never moves here, so the R-5 floor holds, and
                  a breach at the new terms is blocked or resized by the gate below the model — never a warning. */}
              <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                The safety stop is unchanged. The move is re-checked by the risk gate before the
                venue sees it — a breach is blocked or resized, not just flagged.
              </Typography>
              {repriceRefusal !== null ? (
                <Alert severity="error" data-testid="reprice-refusal">
                  {repriceRefusal}
                </Alert>
              ) : null}
            </Box>
          ) : null}
        </DialogContent>
        <DialogActions>
          <Button onClick={closeReprice}>Keep it</Button>
          <Button variant="contained" onClick={confirmReprice} disabled={!canReprice}>
            Reprice this order
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog open={movingStop !== null} onClose={closeMoveStop}>
        <DialogTitle>Move the hidden stop?</DialogTitle>
        <DialogContent>
          {movingStop !== null ? (
            <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1.5, pt: 0.5 }}>
              {/* Names the SPECIFIC order and its current hidden stop, like the reprice/cancel dialogs — the same
                  blotter carries several at once, and moving the wrong one's stop is the same class of mistake. */}
              <Typography variant="body2">
                <strong>{movingStop.contract}</strong> · {movingStop.size} contract
                {movingStop.size === 1 ? '' : 's'} · stop now at {movingStop.workingStopPrice}
              </Typography>
              <TextField
                size="small"
                label="New working stop"
                value={newWorkingDraft}
                onChange={(event) => setNewWorkingDraft(event.target.value)}
                slotProps={{
                  htmlInput: { inputMode: 'decimal', 'data-testid': 'new-working-stop' },
                }}
                helperText={
                  moveBand !== null
                    ? `Between ${moveBand.low} and ${moveBand.high} — inside the safety-to-entry band.`
                    : undefined
                }
              />
              {/* The promise the sheet makes: the SAFETY stop never moves here, so the R-5 floor holds. The move must
                  stay strictly inside the band, and the server re-checks it (and any widen) before the venue sees it. */}
              <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                The safety stop is unchanged. The move must stay strictly between the safety stop
                and the entry; the server re-checks the band before the venue sees it — a breach is
                blocked, not just flagged.
              </Typography>
              {workingOutOfBand ? (
                <Alert severity="warning" data-testid="move-stop-band">
                  That price is outside the safety-to-entry band — onto or past a bound would remove
                  or invert the stop, so it is refused before the venue sees it.
                </Alert>
              ) : null}
              {moveStopRefusal !== null ? (
                <Alert severity="error" data-testid="move-stop-refusal">
                  {moveStopRefusal}
                </Alert>
              ) : null}
            </Box>
          ) : null}
        </DialogContent>
        <DialogActions>
          <Button onClick={closeMoveStop}>Keep it</Button>
          <Button variant="contained" onClick={confirmMoveStop} disabled={!workingInBand}>
            Move this stop
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
