import LightbulbOutlinedIcon from '@mui/icons-material/LightbulbOutlined';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import { useCallback, useEffect, useRef, useState } from 'react';

import { listActionableSuggestions, type StagedTicket, type Suggestion } from '../api/suggestions';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
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
  const mounted = useRef(true);
  // Guards the background refresh (onSuggestion / onResync) against a stale write — see `refresh` / `handlePassed`.
  const refreshToken = useRef(0);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    void listActionableSuggestions(accountId).then((result) => {
      if (!mounted.current) {
        return;
      }
      setState(
        result.ok
          ? { kind: 'loaded', suggestions: result.data }
          : {
              kind: 'error',
              message: result.kind === 'refused' ? result.reason : result.error,
            },
      );
    });
  }, [accountId]);

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
    const token = (refreshToken.current += 1);
    void listActionableSuggestions(accountId).then((result) => {
      if (mounted.current && token === refreshToken.current && result.ok) {
        setState({ kind: 'loaded', suggestions: result.data });
      }
    });
  }, [accountId]);
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
    return (
      <EmptyState
        icon={<LightbulbOutlinedIcon sx={{ fontSize: 40 }} />}
        title="No setup right now"
        // "Nothing proposed" is a normal, frequent state — the honest answer when conditions are not there. It is
        // told apart from a failed load above, because an operator who reads one as the other trades on nothing.
        description="The co-pilot has nothing to propose on this account. Passed and expired setups stay in the journal."
        tag="R-4"
      />
    );
  }

  return (
    <Stack data-testid="suggestion-list" sx={{ gap: 1.5, p: 2 }}>
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
