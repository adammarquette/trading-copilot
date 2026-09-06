import LightbulbOutlinedIcon from '@mui/icons-material/LightbulbOutlined';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import { useCallback, useEffect, useRef, useState } from 'react';

import { listActionableSuggestions, type StagedTicket, type Suggestion } from '../api/suggestions';
import { BehindMarker } from '../components/BehindMarker';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import { useBehindIndicator } from '../components/useBehindIndicator';
import { useRealtime } from '../realtime/RealtimeProvider';
import { SuggestionCard } from './SuggestionCard';

/**
 * The **actionable** set for one account (R-4) — the decision surface.
 *
 * Actionable is the server's definition, not a client filter: the list endpoint defaults to `Active` **and**
 * un-dispositioned, so a passed or expired setup is already absent. Keeping the definition on the server matters
 * because it is the same predicate the take path re-checks against; two definitions of "still actionable" is two
 * chances to disagree.
 *
 * **Order is the server's too — newest first, and never by confidence.** Sorting a decision list by the model's
 * own self-assessment is the subtlest way to turn a display-only figure into a recommendation (R-4), so this
 * renders the page in the order it arrives.
 */

export interface SuggestionListProps {
  readonly accountId: string;
  /** Live market price, when the host surface has one. Threaded straight through to each card. */
  readonly referencePrice?: number | null;
  readonly onArmed?: (suggestion: Suggestion, ticket: StagedTicket) => void;
}

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly suggestions: readonly Suggestion[] };

export function SuggestionList({ accountId, referencePrice = null, onArmed }: SuggestionListProps) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  // A background refresh keeps the current list on a failed read rather than nuking a working panel — but the panel
  // must still LOOK degraded, so a stale actionable set does not read as confidently current (R-19, gh#874). The
  // shared affordance (gh#1109) generalises what this panel built first.
  const { behind: staleRefresh, markBehind, clearBehind } = useBehindIndicator();
  const mounted = useRef(true);
  // Ordering guards for the two reads that write this panel. `mounted` says the component is still on screen; it
  // says nothing about WHICH account a response belongs to.
  //
  // The sole host, `SuggestionsPanel`, keys this component by account (gh#713), so an account switch is already a
  // full remount there — `mounted.current` goes false on the old instance. This generation guard is therefore
  // defense-in-depth: it protects a caller that reuses the component WITHOUT keying it (and is what the
  // same-instance unit tests exercise), not a reachable bug in the current tree — unlike `useSuggestionZones`,
  // whose host is not remounted per account, where the equivalent `loadToken` IS the live guard.
  //
  // A foreground `load` is authoritative: it always writes, and it supersedes every read started before it, so a
  // response for the account just left can never land (R-14). A background `refresh` yields to anything newer — a
  // later load, a later refresh, or a pass.
  const loadGeneration = useRef(0);
  const refreshToken = useRef(0);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    const generation = (loadGeneration.current += 1);
    void listActionableSuggestions(accountId).then((result) => {
      // Drop a superseded load — an account switch started a newer one — or a resolve after unmount.
      if (!mounted.current || generation !== loadGeneration.current) {
        return;
      }
      if (result.ok) {
        setState({ kind: 'loaded', suggestions: result.data });
        clearBehind(); // an authoritative read landed — the panel is current again
      } else {
        setState({
          kind: 'error',
          message: result.kind === 'refused' ? result.reason : result.error,
        });
      }
    });
  }, [accountId, clearBehind]);

  // Mount (and every account switch) kicks off the load. The initial state is already `loading`, so no state is
  // set synchronously in the effect.
  useEffect(() => {
    load();
  }, [load]);

  const reload = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  // A realtimeSuggestion push (gh#760) is a compact signal to reconcile — too little to render a card — so a new /
  // superseded suggestion refetches the actionable list. Owner-scoped and live-only like order / fill, so a reconnect
  // (onResync) refetches too: the pushes missed during a drop are never replayed. A failed background refresh keeps the
  // current list rather than nuking a working panel to an error screen — the initial load and the retry own errors.
  const { onSuggestion, onResync } = useRealtime();
  const refresh = useCallback(() => {
    // A background refresh must never clobber a newer local change. A `handlePassed` optimistic drop — or a later
    // refresh — bumps the token, so an in-flight read that resolves afterwards with a pre-commit snapshot is
    // discarded rather than resurrecting a just-passed card (the R-4 decision surface must not flicker a card back).
    // It also yields to a newer load: an account switch reads through `load`, which never touched this token, so
    // the generation is what keeps acc-1's answer off acc-2's panel (R-14).
    const token = (refreshToken.current += 1);
    // Read, never bump: a refresh that superseded an in-flight load and then failed would write nothing at all —
    // it keeps a working panel rather than nuking it to an error screen — and the panel would hang on its spinner.
    const generation = loadGeneration.current;
    void listActionableSuggestions(accountId).then((result) => {
      // Ignore a superseded refresh (a newer load / refresh / pass has run since) or a resolve after unmount.
      if (
        !mounted.current ||
        generation !== loadGeneration.current ||
        token !== refreshToken.current
      ) {
        return;
      }
      if (result.ok) {
        setState({ kind: 'loaded', suggestions: result.data });
        clearBehind();
      } else {
        // Keep the current list, but flag it possibly out of date. A failed background read while the socket stays
        // live is the one degraded state the panel would otherwise hide — the operator has no other signal (R-19).
        markBehind();
      }
    });
  }, [accountId, markBehind, clearBehind]);
  useEffect(() => onSuggestion(refresh), [onSuggestion, refresh]);
  useEffect(() => onResync(refresh), [onResync, refresh]);

  /**
   * A pass drops the card out of the actionable list — the disposition is recorded and the setup is no longer
   * something to decide on. It is **removed here rather than re-fetched**: a refetch would race the operator's
   * next click and could briefly bring the card back, which reads as the pass having failed. The suggestion stays
   * addressable by id (`GET /suggestions/{id}` returns it in any state, with its disposition) — dropped from the
   * decision surface, kept in the journal.
   */
  const handlePassed = useCallback((id: string) => {
    // Invalidate any background refresh already in flight (its snapshot predates this pass's commit), so it cannot
    // resurrect the dropped card when it resolves — the very flicker this local drop exists to avoid.
    refreshToken.current += 1;
    setState((current) =>
      current.kind === 'loaded'
        ? {
            kind: 'loaded',
            suggestions: current.suggestions.filter((suggestion) => suggestion.id !== id),
          }
        : current,
    );
  }, []);

  // The R-19 degraded-refresh affordance, shared by the loaded and empty returns below — the gh#1109 shared marker,
  // keyed to this panel's established `suggestions-stale` selector. A failed background read while the socket
  // stays live is the one degraded state the panel would otherwise hide (see `refresh`); never shown on the
  // loading / error states (which own their own screens).
  const staleBanner = staleRefresh ? <BehindMarker testId="suggestions-stale" /> : null;

  if (state.kind === 'loading') {
    return <LoadingState label="Loading suggestions" />;
  }

  if (state.kind === 'error') {
    return (
      <EmptyState
        title="Suggestions could not be loaded"
        description={state.message}
        action={
          <Button variant="outlined" size="small" onClick={reload} data-testid="suggestions-retry">
            Try again
          </Button>
        }
        tag="R-4"
      />
    );
  }

  if (state.suggestions.length === 0) {
    // An empty panel whose last background refresh failed is the WORST case for R-19: a just-issued suggestion could
    // be hidden behind a confident "nothing proposed", so the degraded hint shows here too (gh#874 review).
    return (
      <>
        {staleBanner}
        <EmptyState
          icon={<LightbulbOutlinedIcon sx={{ fontSize: 40 }} />}
          title="No setup right now"
          // "Nothing proposed" is a normal, frequent state — the honest answer when conditions are not there. It is
          // told apart from a failed load above, because an operator who reads one as the other trades on nothing.
          description="The co-pilot has nothing to propose on this account. Passed and expired setups stay in the journal."
          tag="R-4"
        />
      </>
    );
  }

  return (
    <Stack data-testid="suggestion-list" sx={{ gap: 1.5, p: 2 }}>
      {staleBanner}
      {state.suggestions.map((suggestion) => (
        <SuggestionCard
          key={suggestion.id}
          suggestion={suggestion}
          referencePrice={referencePrice}
          onPassed={handlePassed}
          {...(onArmed === undefined ? {} : { onArmed })}
        />
      ))}
    </Stack>
  );
}
