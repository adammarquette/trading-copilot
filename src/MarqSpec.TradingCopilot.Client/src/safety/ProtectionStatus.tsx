import Box from '@mui/material/Box';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import { type ProtectionState, getProtectionState, isProtectionEvent } from '../api/protection';
import { useOptionalRealtime } from '../realtime/RealtimeProvider';

/**
 * The degraded-protection indicator, the safety strip's third slot (gh#222, R-11 / R-19, ADR-0021).
 *
 * **Persistent, not a toast.** A venue drop degrades protection for as long as it takes to reconnect and
 * re-validate; a notification that can be missed or dismissed is exactly what R-11's "alerted immediately" is not.
 *
 * **The read is the truth; the broadcast is the prompt.** Every `protection.*` event triggers a re-read rather
 * than being folded into local state. Three things fall out of that, all of which would otherwise need careful
 * reasoning here:
 *
 * - A **replayed** event and a **live** one behave identically — the surface never has to decide whether a
 *   historical event should raise a live banner, because it renders the read, not the event.
 * - `protection.restored` fires when *some* stops re-armed. Clearing on the event would drop the alert while
 *   others are still orphaned; the re-read decides, so a server still reporting orphans keeps it up.
 * - A missed event cannot desynchronise a count, because no count is accumulated client-side.
 *
 * **It fails toward the safe reading**, in both of the ways it can be pushed away from it. A failed read never
 * resolves to "protected" — an unknown state leaves the last known one standing rather than silently downgrading a
 * live warning. And reads are ordered by **generation**, not arrival: overlapping reads are routine here (the
 * mount read races the first broadcast), and an older "protected" answer landing late would otherwise overwrite a
 * newer "degraded" one — the same dangerous direction, reached by a client-side race instead of a missed replay.
 *
 * R-19 rides every rendering: degraded is **never** "unprotected". The native safety stop remains the physical
 * floor; it is the operator's *tighter* synthetic stop that is orphaned.
 */
export function ProtectionStatus() {
  const [state, setState] = useState<ProtectionState | null>(null);
  const realtime = useOptionalRealtime();
  const mounted = useRef(true);
  /** Monotonic read generation — only the newest in-flight read may write state. */
  const latestRequest = useRef(0);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    // Ordering is by request GENERATION, not by arrival. Reads overlap readily here -- the mount read races the
    // first broadcast, and a resync can fire while one is in flight -- and responses can land out of order under
    // HTTP/2 multiplexing, a slow first connection, or a retry. Without this, an older "protected" answer
    // resolving late would overwrite a newer "degraded" one and put the strip back into exactly the dangerous
    // direction this component exists to prevent, reached by a client-side race instead of a missed replay.
    // `mounted` guards post-unmount writes and says nothing about ordering (#777 review).
    const generation = ++latestRequest.current;

    void getProtectionState().then((result) => {
      if (!mounted.current || generation !== latestRequest.current) {
        return;
      }
      // A failure leaves the last known state standing. Reverting to "protected" on a transient 503 would take a
      // live warning off the strip precisely when the backend is already unhealthy.
      if (result.ok) {
        setState(result.data);
      }
    });
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    if (realtime === null) {
      return;
    }

    const stopEvents = realtime.onEvent((event) => {
      if (isProtectionEvent(event.type)) {
        load();
      }
    });
    // The gap path: the hub tells a client whose cursor fell off retention to re-fetch state over REST. This is
    // that re-fetch — without it, an operator who was away longer than the retention window never learns.
    const stopResync = realtime.onResync(load);

    return () => {
      stopEvents();
      stopResync();
    };
  }, [realtime, load]);

  if (state === null || !state.degraded) {
    // Nothing alarming while protection is whole. The slot keeps its footprint (SafetyRegion reserves it), so
    // the strip does not shift the moment a drop happens — the controls beside it stay where muscle memory left
    // them.
    return <Box data-testid="protection-status" data-degraded="false" sx={{ minWidth: 8 }} />;
  }

  const description =
    `${state.orphanedStops} hidden working stop${state.orphanedStops === 1 ? '' : 's'} orphaned by a venue ` +
    'disconnect — your tighter stop cannot promote until it reconnects and re-validates. Your native safety ' +
    'stop is still with the position and remains the floor.';

  return (
    <Tooltip title={description}>
      <Box
        data-testid="protection-status"
        data-degraded="true"
        role="status"
        aria-label={`Protection degraded: ${state.orphanedStops} stop(s) orphaned`}
        aria-description={description}
        sx={{ display: 'flex', alignItems: 'center', whiteSpace: 'nowrap' }}
      >
        <Typography
          variant="body2"
          sx={{ fontWeight: 700, fontSize: 11, letterSpacing: '.04em', color: 'warning.main' }}
        >
          {`DEGRADED ${state.orphanedStops}`}
        </Typography>
      </Box>
    </Tooltip>
  );
}
