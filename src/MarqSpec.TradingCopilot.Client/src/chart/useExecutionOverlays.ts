import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { useAccounts } from '../accounts/AccountProvider';
import type { ReconciledPosition, RestingOrder } from '../api/execution';
import { getPositions, getWorkingOrders } from '../api/execution';
import { useRealtime } from '../realtime/RealtimeProvider';
import type { ExecutionOverlay, PositionMark, WorkingOrderMark } from './overlays';

export interface ExecutionOverlaysResult {
  readonly overlay: ExecutionOverlay;
  /** The socket is not `live`, so the overlay may lag a fill / cancel — surfaced, never hidden (R-19). */
  readonly stale: boolean;
}

/** Stable empty defaults so a "nothing" result never churns the chart's overlay effect (the gh#749 lesson). */
const NO_ORDER_MARKS: readonly WorkingOrderMark[] = [];
const NO_RAW_ORDERS: readonly RestingOrder[] = [];
const NO_RAW_POSITIONS: readonly ReconciledPosition[] = [];
const EMPTY_OVERLAY: ExecutionOverlay = { orders: NO_ORDER_MARKS, position: null };

/**
 * The operator's live working orders and net position on one instrument for the active account (gh#727 increment 3),
 * from the instrument-scoped venue-truth reads (gh#772). It loads on mount / account / instrument change, refreshes on
 * every owner-scoped order-state and fill push (gh#683) and across a reconnect / retention gap (`onResync`), and flags
 * `stale` whenever the socket is not `live` (R-19 — a degraded socket must look degraded).
 *
 * The realtime pushes are REFRESH SIGNALS, not marker data: an order-state change or a fill means the book / position
 * may have moved, so the instrument-scoped REST reads are re-run. That sidesteps `RealtimeFill` carrying no instrument
 * (it is account-wide and live-only) — the instrument filter stays server-side, where the venue contract is known. A
 * load is token-guarded, so a slow response for the account / instrument just left can never land (R-14).
 */
export function useExecutionOverlays(instrument: string): ExecutionOverlaysResult {
  const accounts = useAccounts();
  const { connectionState, onOrderState, onFill, onResync } = useRealtime();
  const accountId = accounts.status === 'ready' ? accounts.activeAccount.id : null;

  const [rawOrders, setRawOrders] = useState<readonly RestingOrder[]>(NO_RAW_ORDERS);
  const [rawPositions, setRawPositions] = useState<readonly ReconciledPosition[]>(NO_RAW_POSITIONS);
  const mounted = useRef(true);
  const loadToken = useRef(0);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const wanted = instrument.trim().toUpperCase();

  const load = useCallback(() => {
    if (accountId === null) {
      return;
    }
    const token = (loadToken.current += 1);
    void Promise.all([getWorkingOrders(accountId, wanted), getPositions(accountId, wanted)]).then(
      ([ordersResult, positionsResult]) => {
        // Drop a superseded load (an account / instrument switch bumped the token) or a resolve after unmount, so the
        // one just left never writes over the current one (R-14). A refused / failed read leaves the overlay empty
        // rather than drawing stale marks — the blotter (gh#656) owns surfacing that error, not the chart.
        if (!mounted.current || token !== loadToken.current) {
          return;
        }
        setRawOrders(ordersResult.ok ? ordersResult.data.orders : NO_RAW_ORDERS);
        setRawPositions(positionsResult.ok ? positionsResult.data.positions : NO_RAW_POSITIONS);
      },
    );
  }, [accountId, wanted]);

  // Load on mount and whenever the account / instrument changes; refresh on every owner-scoped order-state / fill push
  // and across a reconnect / retention gap. Each `on*` returns its own unsubscribe.
  useEffect(() => {
    load();
  }, [load]);
  useEffect(() => onOrderState(load), [onOrderState, load]);
  useEffect(() => onFill(load), [onFill, load]);
  useEffect(() => onResync(load), [onResync, load]);

  return useMemo<ExecutionOverlaysResult>(() => {
    const stale = connectionState !== 'live';
    if (accountId === null) {
      return { overlay: EMPTY_OVERLAY, stale };
    }
    const orders = toOrderMarks(rawOrders);
    const position = toPositionMark(rawPositions);
    if (orders.length === 0 && position === null) {
      return { overlay: EMPTY_OVERLAY, stale };
    }
    return { overlay: { orders: orders.length === 0 ? NO_ORDER_MARKS : orders, position }, stale };
  }, [rawOrders, rawPositions, accountId, connectionState]);
}

/** A resting order maps to a stop mark at its stop trigger and / or a limit mark at its limit price (either may be set). */
function toOrderMarks(orders: readonly RestingOrder[]): WorkingOrderMark[] {
  const marks: WorkingOrderMark[] = [];
  for (const order of orders) {
    if (order.stopPrice !== null) {
      marks.push({
        id: `${order.venueOrderKey}:stop`,
        price: order.stopPrice,
        kind: 'stop',
        size: order.size,
      });
    }
    if (order.limitPrice !== null) {
      marks.push({
        id: `${order.venueOrderKey}:limit`,
        price: order.limitPrice,
        kind: 'limit',
        size: order.size,
      });
    }
  }
  return marks;
}

/**
 * The operator's net position on the instrument: the first non-flat one the read returns (venue truth carries one net
 * position per contract, and the read is already instrument-scoped, so there is never more than one to pick). A flat /
 * absent instrument is `null` — no line.
 */
function toPositionMark(positions: readonly ReconciledPosition[]): PositionMark | null {
  const open = positions.find((position) => !position.isFlat);
  return open === undefined
    ? null
    : { averagePrice: open.averagePrice, netQuantity: open.netQuantity };
}
