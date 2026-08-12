import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { useAccounts } from '../accounts/AccountProvider';
import type { Fill } from '../api/fills';
import { getFills } from '../api/fills';
import { useRealtime } from '../realtime/RealtimeProvider';
import type { FillMark } from './overlays';

export interface FillMarkersResult {
  readonly fills: readonly FillMark[];
  /** The socket is not `live`, so a fresh fill may not have refreshed the markers yet — surfaced, not hidden (R-19). */
  readonly stale: boolean;
  /** The read was refused / failed (e.g. an over-wide window), so absence is unknown, not "no fills" (R-11 / R-19). */
  readonly unavailable: boolean;
}

/** Stable empty defaults so a "nothing" result never churns the chart's marker effect (the gh#749 lesson). */
const NO_FILL_MARKS: readonly FillMark[] = [];
const NO_RAW_FILLS: readonly Fill[] = [];

/**
 * How far back the fill overlay looks (minutes) — a fixed recent window (a trading week), NOT the chart's full
 * lookback. The chart's default window runs to months at coarse resolutions, which would both bury the axis in old
 * markers and blow the server's fills cap (gh#792); recent fills are what a day-trading review needs, and off-window
 * markers clip harmlessly. A window this size stays comfortably under the read cap for a realistic operator.
 */
const FILL_LOOKBACK_MINUTES = 7 * 24 * 60;

/** How long to coalesce a burst of fill pushes before re-reading (ms) — the useExecutionOverlays discipline. */
const REFRESH_COALESCE_MS = 120;

/**
 * The operator's recent fills on one instrument for the active account (gh#727), for the chart's fill-marker overlay,
 * from the journal read (gh#792). It loads on mount / account / instrument change over a fixed recent window,
 * refreshes on every owner-scoped fill push (gh#683) and across a reconnect / retention gap (`onResync`), flags
 * `stale` whenever the socket is not `live`, and flags `unavailable` when the read is refused / failed (so an empty
 * overlay is never misread as "no trades" — R-19).
 *
 * The fill push is a REFRESH SIGNAL, not marker data: it carries no instrument (it is account-wide), so a push just
 * re-reads the instrument-scoped journal, where the fill's order — and thus its instrument — is known. A load is
 * token-guarded (R-14) and a burst of pushes is coalesced into one re-read so a multi-fill sequence does not hammer
 * the read.
 */
export function useFillMarkers(instrument: string): FillMarkersResult {
  const accounts = useAccounts();
  const { connectionState, onFill, onResync } = useRealtime();
  const accountId = accounts.status === 'ready' ? accounts.activeAccount.id : null;

  const [rawFills, setRawFills] = useState<readonly Fill[]>(NO_RAW_FILLS);
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
    const nowMs = Date.now();
    const from = new Date(nowMs - FILL_LOOKBACK_MINUTES * 60_000).toISOString();
    const to = new Date(nowMs).toISOString();
    void getFills(accountId, wanted, from, to).then((result) => {
      // Drop a superseded load (an account / instrument switch bumped the token) or a resolve after unmount (R-14).
      if (!mounted.current || token !== loadToken.current) {
        return;
      }
      setRawFills(result.ok ? result.data.fills : NO_RAW_FILLS);
      // A refused / failed read did not obtain the fill history, so an empty overlay is unknown, not "no trades".
      setUnavailable(!result.ok);
    });
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
  // fill push and across a reconnect / retention gap. Each `on*` returns its own unsubscribe.
  useEffect(() => {
    load();
  }, [load]);
  useEffect(() => onFill(scheduleRefresh), [onFill, scheduleRefresh]);
  useEffect(() => onResync(scheduleRefresh), [onResync, scheduleRefresh]);

  const fills = useMemo(() => {
    if (accountId === null) {
      return NO_FILL_MARKS;
    }
    const marks = rawFills.map(toFillMark);
    return marks.length === 0 ? NO_FILL_MARKS : marks;
  }, [rawFills, accountId]);

  const stale = connectionState !== 'live';
  return useMemo<FillMarkersResult>(
    () => ({ fills, stale, unavailable: accountId === null ? false : unavailable }),
    [fills, stale, unavailable, accountId],
  );
}

/** A journaled fill maps to a marker at its execution instant (ISO → UNIX seconds), keyed by its venue fill handle. */
function toFillMark(fill: Fill): FillMark {
  return {
    id: fill.venueFillKey ?? `${fill.side}-${fill.executedAt}-${fill.price}`,
    time: Math.floor(Date.parse(fill.executedAt) / 1000),
    side: fill.side,
    size: fill.size,
  };
}
