import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import {
  IndicatorComparison,
  type Trigger,
  TriggerConfirmation,
  confirmTrigger,
  deleteTrigger,
  listTriggers,
  willFire,
} from '../api/triggers';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';

/**
 * The standing-triggers surface (gh#991, split from gh#660; R-7 / R-4, ADR-0008).
 *
 * **What this surface exists to make visible is that `enabled` does not mean live.** A trigger defaults to
 * unconfirmed and is inert regardless of `enabled` (gh#470's confirm-before-live gate, proven in gh#486). Rendered
 * as two equal badges, `Enabled` beside `Unconfirmed` invites the reading "on, with a note" — and an operator
 * waiting on an alert that can never fire is the silent-monitor failure ADR-0019 exists to close. So each row leads
 * with the one question that matters, *will this fire?*, and answers **"Will not fire"** in words rather than
 * leaving the operator to combine two flags correctly.
 *
 * Authoring is deliberately not here (inc 2): the create form carries ten fields and would bury this.
 */
type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly triggers: readonly Trigger[] };

/** The condition in words. Display only — nothing is gated on this string. */
function conditionOf(trigger: Trigger): string {
  const direction =
    trigger.comparison === IndicatorComparison.Above
      ? 'above'
      : trigger.comparison === IndicatorComparison.Below
        ? 'below'
        : 'never satisfies';
  return `${trigger.symbol} ${trigger.indicator}(${trigger.period}) ${trigger.resolutionMinutes}m ${direction} ${trigger.threshold}`;
}

/**
 * Why a trigger will not fire, or null when it will. Phrased as the operator's question rather than the model's
 * fields, and it names the *blocking* reason: an unconfirmed trigger stays inert even when switched on, so
 * "unconfirmed" outranks "switched off" when both are true.
 */
function inertReason(trigger: Trigger): string | null {
  if (trigger.confirmation !== TriggerConfirmation.Confirmed) {
    return trigger.enabled
      ? 'Switched on, but never confirmed — it will not fire until you confirm it.'
      : 'Not confirmed, and switched off.';
  }
  return trigger.enabled ? null : 'Confirmed, but switched off.';
}

export function TriggersSurface() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  // A SET, not a single slot. `disabled` is keyed per row, so one shared slot lets a second action on another
  // row clear the first row's busy state while its request is still in flight — the operator can re-press, and
  // the second request 404s and reports "could not be deleted" for a delete that in fact succeeded (gh#993
  // review, round 4). Increment 2 puts four handlers on this, so a single slot gets worse, not better.
  const [busyIds, setBusyIds] = useState<ReadonlySet<string>>(() => new Set());
  // Per-row, keyed like `busyIds`. A single shared slot let a second row's refusal silently REPLACE the first's,
  // with neither message tied to its row (gh#1002) — and two rows can be in flight at once (that is exactly why
  // `busyIds` was made per-row, gh#993 round 4), so an error must belong to the row whose action produced it.
  const [actionErrors, setActionErrors] = useState<ReadonlyMap<string, string>>(() => new Map());

  const markBusy = useCallback((id: string, busy: boolean) => {
    setBusyIds((current) => {
      const next = new Set(current);
      if (busy) {
        next.add(id);
      } else {
        next.delete(id);
      }
      return next;
    });
  }, []);

  const markError = useCallback((id: string, message: string | null) => {
    setActionErrors((current) => {
      const next = new Map(current);
      if (message === null) {
        next.delete(id);
      } else {
        next.set(id, message);
      }
      return next;
    });
  }, []);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  // No synchronous setState here: this is passed straight to an effect, and the initial state is already
  // `loading`. The retry path sets it explicitly, since by then the surface is showing an error.
  const load = useCallback(() => {
    void listTriggers()
      .then((result) => {
        if (!mounted.current) {
          return;
        }
        if (!result.ok) {
          setState({
            kind: 'error',
            message: result.kind === 'refused' ? result.reason : result.error,
          });
          return;
        }
        setState({ kind: 'loaded', triggers: result.data });
      })
      .catch(() => {
        // The read seam maps a malformed or empty 2xx to `failed` (gh#963), so this arm is for anything else on
        // the path that could throw. A surface must not be strandable by a throw it did not anticipate (gh#973).
        if (mounted.current) {
          setState({ kind: 'error', message: 'The triggers could not be loaded.' });
        }
      });
  }, []);

  useEffect(load, [load]);

  /** Retry from the error state: show the spinner again, then re-read. */
  const retry = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  /** Confirms one trigger and replaces that row with what the server returned — never with an assumed state. */
  const onConfirm = useCallback(
    (id: string) => {
      markError(id, null);
      markBusy(id, true);
      void confirmTrigger(id)
        .then((result) => {
          if (!mounted.current) {
            return;
          }
          markBusy(id, false);
          if (!result.ok) {
            // The row is left exactly as it was: a failed confirm must never look like a successful one.
            markError(id, result.kind === 'refused' ? result.reason : result.error);
            return;
          }
          const updated = result.data;
          setState((current) =>
            current.kind === 'loaded'
              ? {
                  kind: 'loaded',
                  triggers: current.triggers.map((t) => (t.id === updated.id ? updated : t)),
                }
              : current,
          );
        })
        .catch(() => {
          if (mounted.current) {
            markBusy(id, false);
            markError(id, 'The trigger could not be confirmed.');
          }
        });
    },
    [markBusy, markError],
  );

  const onDelete = useCallback(
    (id: string) => {
      markError(id, null);
      markBusy(id, true);
      void deleteTrigger(id)
        .then((result) => {
          if (!mounted.current) {
            return;
          }
          markBusy(id, false);
          if (!result.ok) {
            // The row STAYS. Removing it on a failed delete would tell the operator a trigger is gone while it is
            // still standing at the server and still able to fire (gh#993 review).
            markError(id, result.kind === 'refused' ? result.reason : result.error);
            return;
          }
          setState((current) =>
            current.kind === 'loaded'
              ? { kind: 'loaded', triggers: current.triggers.filter((t) => t.id !== id) }
              : current,
          );
        })
        .catch(() => {
          if (mounted.current) {
            markBusy(id, false);
            markError(id, 'The trigger could not be deleted.');
          }
        });
    },
    [markBusy, markError],
  );

  if (state.kind === 'loading') {
    return <LoadingState label="Loading triggers" />;
  }

  if (state.kind === 'error') {
    return (
      <Stack spacing={2} data-testid="triggers-error">
        <Alert severity="error" role="alert">
          {state.message}
        </Alert>
        <Box>
          <Button variant="outlined" onClick={retry}>
            Try again
          </Button>
        </Box>
      </Stack>
    );
  }

  if (state.triggers.length === 0) {
    return (
      <EmptyState
        title="No triggers yet"
        description="A trigger watches one indicator on one instrument and alerts you when it crosses your threshold. A new trigger does not fire until you confirm it."
        tag="R-7"
      />
    );
  }

  return (
    <Stack spacing={2} data-testid="triggers-surface">
      {state.triggers.map((trigger) => {
        const inert = inertReason(trigger);
        return (
          <Box
            key={trigger.id}
            data-testid="trigger-row"
            sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 2 }}
          >
            <Stack spacing={1}>
              <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
                <Typography variant="subtitle1">{conditionOf(trigger)}</Typography>
                <Chip
                  size="small"
                  color={willFire(trigger) ? 'success' : 'default'}
                  label={willFire(trigger) ? 'Live' : 'Will not fire'}
                />
              </Stack>
              {inert ? (
                <Typography variant="body2" color="text.secondary">
                  {inert}
                </Typography>
              ) : null}
              <Stack direction="row" spacing={1}>
                {trigger.confirmation === TriggerConfirmation.Confirmed ? null : (
                  <Button
                    size="small"
                    variant="contained"
                    aria-label={`Confirm ${conditionOf(trigger)}`}
                    disabled={busyIds.has(trigger.id)}
                    onClick={() => onConfirm(trigger.id)}
                  >
                    Confirm
                  </Button>
                )}
                <Button
                  size="small"
                  color="error"
                  aria-label={`Delete ${conditionOf(trigger)}`}
                  disabled={busyIds.has(trigger.id)}
                  onClick={() => onDelete(trigger.id)}
                >
                  Delete
                </Button>
              </Stack>
              {/* The error belongs to THIS row (gh#1002): a second row's refusal can no longer replace it, and it
                  sits beside the action it came from rather than in one banner that named no row. */}
              {actionErrors.has(trigger.id) ? (
                <Alert severity="error" role="alert" data-testid={`trigger-error-${trigger.id}`}>
                  {actionErrors.get(trigger.id)}
                </Alert>
              ) : null}
            </Stack>
          </Box>
        );
      })}
    </Stack>
  );
}
