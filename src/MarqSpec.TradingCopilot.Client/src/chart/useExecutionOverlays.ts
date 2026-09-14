import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { useAccounts } from '../accounts/AccountProvider';
import type { ReconciledPosition, RestingOrder } from '../api/execution';
import { getPositions, getWorkingOrders } from '../api/execution';
import { useRealtime } from '../realtime/RealtimeProvider';
import type { ExecutionOverlay, PositionMark, WorkingOrderMark } from './overlays';

export interface ExecutionOverlaysResult {
  readonly overlay: ExecutionOverlay;
  /** The socket is not `live`, so the DRAWN overlay may lag a fill / cancel — surfaced, never hidden (R-19). */
  readonly stale: boolean;
  /**
   * The venue-truth read did not return a confirmed reading — an `Unknown` basis (venue unreachable, a 200 with
   * empty data) or a refused / failed read — so an EMPTY overlay here is <b>declared-unknown, not a confirmed flat
   * book</b>. Kept distinct from {@link stale} (which is the socket): a live socket can still front an unreachable
   * venue-truth read, and R-13 / R-19 / ADR-0013 hold that "we could not ask" and "nothing is there" are opposite
   * answers — the chart must not let the first read as the second.
   */
  readonly unavailable: boolean;
}

/** Stable empty defaults so a "nothing" result never churns the chart's overlay effect (the gh#749 lesson). */
const NO_ORDER_MARKS: readonly WorkingOrderMark[] = [];
const NO_RAW_ORDERS: readonly RestingOrder[] = [];
const NO_RAW_POSITIONS: readonly ReconciledPosition[] = [];
const EMPTY_OVERLAY: ExecutionOverlay = { orders: NO_ORDER_MARKS, position: null };

/** The mark basis an unreachable venue returns (declared-unknown truth); a reachable one returns Live / Settlement. */
const UNKNOWN_BASIS = 'Unknown';

/**
 * How long to coalesce a burst of realtime refresh signals before re-reading (ms). A partial-filled order emits a
 * flurry of order-state + fill pushes in a second or two; without coalescing each one fans out to a fresh pair of
 * venue-truth (broker) reads. A short trailing debounce collapses the burst to one re-read — imperceptible for a
 * display overlay, and it bounds the broker read rate. The mount / account / instrument load stays immediate.
 */
const REFRESH_COALESCE_MS = 120;

/**
 * The operator's live working orders and net position on one instrument for the active account (gh#727 increment 3),
 * from the instrument-scoped venue-truth reads (gh#772). It loads on mount / account / instrument change, refreshes on
 * every owner-scoped order-state and fill push (gh#683) and across a reconnect / retention gap (`onResync`), flags
 * `stale` whenever the socket is not `live`, and flags `unavailable` when the venue-truth read is not a confirmed
 * reading (R-19 — a degraded read must look degraded, and an empty overlay must never read as a confirmed flat book).
 *
 * The realtime pushes are REFRESH SIGNALS, not marker data: an order-state change or a fill means the book / position
 * may have moved, so the instrument-scoped REST reads are re-run. That sidesteps `RealtimeFill` carrying no instrument
 * (it is account-wide and live-only) — the instrument filter stays server-side, where the venue contract is known. A
 * load is token-guarded, so a slow response for the account / instrument just left can never land (R-14); a burst of
 * pushes is coalesced into one re-read (`REFRESH_COALESCE_MS`) so a multi-fill sequence does not hammer the venue.
 */
export function useExecutionOverlays(instrument: string): ExecutionOverlaysResult {
  const accounts = useAccounts();
  const { connectionState, onOrderState, onFill, onResync } = useRealtime();
  const accountId = accounts.status === 'ready' ? accounts.activeAccount.id : null;

  const [rawOrders, setRawOrders] = useState<readonly RestingOrder[]>(NO_RAW_ORDERS);
  const [rawPositions, setRawPositions] = useState<readonly ReconciledPosition[]>(NO_RAW_POSITIONS);
  const [unavailable, setUnavailable] = useState(false);
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
        // one just left never writes over the current one (R-14).
        if (!mounted.current || token !== loadToken.current) {
          return;
        }
        setRawOrders(ordersResult.ok ? ordersResult.data.orders : NO_RAW_ORDERS);
        setRawPositions(positionsResult.ok ? positionsResult.data.positions : NO_RAW_POSITIONS);
        // A read is venue truth only if it succeeded AND carries a confirmed (non-Unknown) basis. An Unknown basis
        // (venue unreachable — a 200 with empty data) or a refused / failed read means the empty overlay is
        // declared-unknown, NOT a confirmed flat book, so the chart labels it rather than letting it read as flat
        // (R-13 / R-19). The blotter (gh#656) still owns the detailed error text; this is only the honesty flag.
        const ordersKnown = ordersResult.ok && ordersResult.data.markBasis !== UNKNOWN_BASIS;
        const positionsKnown =
          positionsResult.ok && positionsResult.data.markBasis !== UNKNOWN_BASIS;
        setUnavailable(!ordersKnown || !positionsKnown);
      },
    );
  }, [accountId, wanted]);

  // The debounced refresh fires the LATEST load through a ref, so the coalescing callback stays stable (subscribes
  // once) while always re-reading the current account / instrument — never a stale closure.
  const loadRef = useRef(load);
  useEffect(() => {
    loadRef.current = load;
  }, [load]);

  const refreshTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const scheduleRefresh = useCallback(() => {
    if (refreshTimer.current !== null) {
      clearTimeout(refreshTimer.current);
    }
    refreshTimer.current = setTimeout(() => {
      refreshTimer.current = null;
      loadRef.current();
    }, REFRESH_COALESCE_MS);
  }, []);
  useEffect(
    () => () => {
      if (refreshTimer.current !== null) {
        clearTimeout(refreshTimer.current);
      }
    },
    [],
  );

  // Load immediately on mount and whenever the account / instrument changes; refresh (coalesced) on every owner-scoped
  // order-state / fill push and across a reconnect / retention gap. Each `on*` returns its own unsubscribe.
  useEffect(() => {
    load();
  }, [load]);
  useEffect(() => onOrderState(scheduleRefresh), [onOrderState, scheduleRefresh]);
  useEffect(() => onFill(scheduleRefresh), [onFill, scheduleRefresh]);
  useEffect(() => onResync(scheduleRefresh), [onResync, scheduleRefresh]);

  return useMemo<ExecutionOverlaysResult>(() => {
    const stale = connectionState !== 'live';
    if (accountId === null) {
      return { overlay: EMPTY_OVERLAY, stale, unavailable: false };
    }
    const orders = toOrderMarks(rawOrders);
    const position = toPositionMark(rawPositions);
    const overlay =
      orders.length === 0 && position === null
        ? EMPTY_OVERLAY
        : { orders: orders.length === 0 ? NO_ORDER_MARKS : orders, position };
    return { overlay, stale, unavailable };
  }, [rawOrders, rawPositions, accountId, connectionState, unavailable]);
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
