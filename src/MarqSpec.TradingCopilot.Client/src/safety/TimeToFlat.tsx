import Box from '@mui/material/Box';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import {
  type FlattenSchedule,
  getFlattenSchedule,
  isFlattenEvent,
  remainingMs,
  soonestArmed,
} from '../api/flatten';
import { useOptionalRealtime } from '../realtime/RealtimeProvider';

/** How often the schedule is re-read: re-syncs `asOf` and rolls to the next session without a reload. */
const REFRESH_MS = 5 * 60 * 1000;

/** The visible tick. */
const TICK_MS = 1000;

/**
 * The R-13 time-to-flat countdown, one of the two controls the safety strip keeps in front of the operator at
 * every breakpoint (gh#657, ADR-0005).
 *
 * **Every number here is the server's.** The deadline is a wall-clock Central time, so a browser resolving it
 * against its own zone is an hour out around a daylight-saving change; and a workstation clock running fast
 * would invent safety margin that does not exist. So the component subtracts two server instants and then
 * advances that figure by a locally measured *duration*, which carries no skew.
 *
 * The three non-counting states are deliberately distinct, because conflating them is what would mislead:
 * **unavailable** (the read failed — say so, never show a stale or fabricated number), **not armed** (R-13's
 * deliberate override — auto-flatten will not act, and on a live account nothing else will), and **loading**.
 */
export function TimeToFlat() {
  const [schedule, setSchedule] = useState<FlattenSchedule | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [elapsedMs, setElapsedMs] = useState(0);
  const fetchedAt = useRef(0);
  const realtime = useOptionalRealtime();
  const mounted = useRef(true);
  /** Monotonic read generation — only the newest in-flight read may write the schedule. */
  const latestRequest = useRef(0);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    // Ordering is by request GENERATION, not arrival: an event-driven re-read races the periodic refresh and the
    // mount read, and an older schedule resolving late must not overwrite a newer one (a stale `asOf` would drift
    // the countdown). `mounted` guards post-unmount writes and says nothing about ordering.
    const generation = ++latestRequest.current;

    void getFlattenSchedule().then((result) => {
      if (!mounted.current || generation !== latestRequest.current) {
        return;
      }
      if (result.ok) {
        // The local reference for the elapsed duration is taken from the same clock that will later measure
        // against it, so the two cancel however wrong that clock is in absolute terms.
        fetchedAt.current = Date.now();
        setElapsedMs(0);
        setSchedule(result.data);
        setUnavailable(false);
        return;
      }
      setUnavailable(true);
    });
  }, []);

  // The initial state is already "loading", so nothing is set synchronously here.
  useEffect(() => {
    load();
    const refresh = setInterval(load, REFRESH_MS);
    return () => clearInterval(refresh);
  }, [load]);

  // Re-read the moment auto-flatten acts, rather than waiting up to REFRESH_MS: after a deadline fires the
  // soonest-armed rolls to the next session, and a second window (a gh#651 pop-out) or a reconnected tab would
  // otherwise sit at 00:00 — or on a now-passed deadline — until the periodic refresh caught up. onResync covers the
  // retention-gap / reconnect re-fetch. The arming itself is deployed config, so this reacts to a deadline being
  // acted on, not to a runtime arm/disarm. The read is the truth; the event is only the prompt.
  useEffect(() => {
    if (realtime === null) {
      return;
    }

    const stopEvents = realtime.onEvent((event) => {
      if (isFlattenEvent(event.type)) {
        load();
      }
    });
    const stopResync = realtime.onResync(load);

    return () => {
      stopEvents();
      stopResync();
    };
  }, [realtime, load]);

  useEffect(() => {
    const tick = setInterval(() => setElapsedMs(Date.now() - fetchedAt.current), TICK_MS);
    return () => clearInterval(tick);
  }, []);

  if (unavailable) {
    return (
      <Strip label="unavailable" description="The auto-flatten schedule could not be read." warn />
    );
  }

  if (schedule === null) {
    return <Strip label="…" description="Reading the auto-flatten schedule." />;
  }

  const market = soonestArmed(schedule);

  if (market === null) {
    return (
      <Strip
        label="not armed"
        description="Auto-flatten is disabled for every governed market — positions will not be closed automatically (R-13)."
        warn
      />
    );
  }

  return (
    <Strip
      label={`${market.instrument} ${formatRemaining(remainingMs(market, schedule.asOf, elapsedMs))}`}
      description={`Auto-flatten closes ${market.instrument} at ${market.deadline} CT.`}
    />
  );
}

/** `H:MM:SS` past an hour, `MM:SS` inside one — the shorter form is easier to read at a glance, which matters most near the deadline. */
function formatRemaining(milliseconds: number): string {
  const totalSeconds = Math.floor(milliseconds / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const mmss = `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;

  return hours > 0 ? `${hours}:${mmss}` : mmss;
}

interface StripProps {
  readonly label: string;
  readonly description: string;
  readonly warn?: boolean;
}

function Strip({ label, description, warn = false }: StripProps) {
  return (
    <Tooltip title={description}>
      <Box
        data-testid="time-to-flat"
        aria-label={`Time to auto-flatten: ${label}`}
        aria-description={description}
        sx={{ display: 'flex', alignItems: 'center', whiteSpace: 'nowrap' }}
      >
        <Typography
          variant="body2"
          sx={{
            fontVariantNumeric: 'tabular-nums',
            fontWeight: 600,
            fontSize: 12,
            color: warn ? 'warning.main' : 'text.primary',
          }}
        >
          {label}
        </Typography>
      </Box>
    </Tooltip>
  );
}
