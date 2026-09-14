import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { useAccounts } from '../accounts/AccountProvider';
import type { Suggestion } from '../api/suggestions';
import { SuggestionState, listActionableSuggestions } from '../api/suggestions';
import { useRealtime } from '../realtime/RealtimeProvider';
import type { SuggestionZone } from './overlays';

export interface SuggestionZonesResult {
  readonly zones: readonly SuggestionZone[];
  /** The socket is not `live`, so the zones may lag a fresh issue / supersede — surfaced, never hidden (R-19). */
  readonly stale: boolean;
}

/** Stable empty defaults so a "nothing" result never churns the chart's overlay effect (the gh#749 lesson). */
const NO_ZONES: readonly SuggestionZone[] = [];
const NO_SUGGESTIONS: readonly Suggestion[] = [];

/**
 * The active suggestion zones for one instrument on the active account (gh#727 increment 2). It loads the R-4
 * actionable list (owner-scoped by the account), derives the charted instrument's entry / stop / target, keeps it
 * fresh across a reconnect / retention gap (`onResync`), and flags `stale` whenever the socket is not `live` (R-19 —
 * a degraded socket must look degraded).
 *
 * The account-wide list is loaded once and the instrument filter is a pure derivation, so switching instrument needs
 * no refetch. A load is token-guarded, so a slow response for the account just left can never land (R-14). It refreshes
 * on every `realtimeSuggestion` push (gh#760 — a new / superseded suggestion) and on reconnect / retention gap
 * (`onResync`, since owner-scoped pushes are live-only and never replayed), and flags `stale` whenever the socket is
 * not `live` in between (R-19 — a degraded socket must look degraded).
 */
export function useSuggestionZones(instrument: string): SuggestionZonesResult {
  const accounts = useAccounts();
  const { connectionState, onResync, onSuggestion } = useRealtime();
  const accountId = accounts.status === 'ready' ? accounts.activeAccount.id : null;

  const [suggestions, setSuggestions] = useState<readonly Suggestion[]>(NO_SUGGESTIONS);
  const mounted = useRef(true);
  const loadToken = useRef(0);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    if (accountId === null) {
      return;
    }
    const token = (loadToken.current += 1);
    void listActionableSuggestions(accountId).then((result) => {
      // Drop a superseded load (an account switch bumped the token) or a resolve after unmount, so the account just
      // left never writes over the current one (R-14). A failed / refused load leaves no suggestions — the suggestion
      // PANEL owns that error (R-4), so duplicating it on the chart would be noise.
      if (!mounted.current || token !== loadToken.current) {
        return;
      }
      setSuggestions(result.ok ? result.data : NO_SUGGESTIONS);
    });
  }, [accountId]);

  // Load on mount and whenever the account changes; refresh across a reconnect / retention gap (`onResync` returns
  // its own unsubscribe).
  useEffect(() => {
    load();
  }, [load]);
  useEffect(() => onResync(load), [onResync, load]);
  // Per-push refresh (gh#760): a new / superseded suggestion re-derives the zones instantly, no longer only on reconnect.
  useEffect(() => onSuggestion(load), [onSuggestion, load]);

  return useMemo<SuggestionZonesResult>(() => {
    const stale = connectionState !== 'live';
    if (accountId === null) {
      return { zones: NO_ZONES, stale };
    }
    const wanted = instrument.trim().toUpperCase();
    const zones = suggestions
      .filter(
        (suggestion) =>
          suggestion.state === SuggestionState.Active &&
          suggestion.instrument.toUpperCase() === wanted,
      )
      .map((suggestion) => ({
        id: suggestion.id,
        entry: suggestion.entryPrice,
        stop: suggestion.stopPrice,
        target: suggestion.targetPrice,
      }));
    return { zones: zones.length === 0 ? NO_ZONES : zones, stale };
  }, [suggestions, instrument, accountId, connectionState]);
}
