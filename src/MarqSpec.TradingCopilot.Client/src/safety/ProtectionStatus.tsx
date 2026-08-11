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
 * **It fails toward the safe reading.** A failed read never resolves to "protected" — an unknown state leaves the
 * last known one standing rather than silently downgrading a live warning.
 *
 * R-19 rides every rendering: degraded is **never** "unprotected". The native safety stop remains the physical
 * floor; it is the operator's *tighter* synthetic stop that is orphaned.
 */
export function ProtectionStatus() {
  const [state, setState] = useState<ProtectionState | null>(null);
  const realtime = useOptionalRealtime();
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    void getProtectionState().then((result) => {
      // A failure leaves the last known state standing. Reverting to "protected" on a transient 503 would take a
      // live warning off the strip precisely when the backend is already unhealthy.
      if (mounted.current && result.ok) {
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
