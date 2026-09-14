import Box from '@mui/material/Box';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';

import type { RealtimeConnectionState } from '../realtime/connection';
import { useOptionalRealtime } from '../realtime/RealtimeProvider';

const LABELS: Readonly<Record<RealtimeConnectionState, string>> = {
  connecting: 'Connecting',
  live: 'Live',
  reconnecting: 'Reconnecting',
  down: 'Offline',
};

const DESCRIPTIONS: Readonly<Record<RealtimeConnectionState, string>> = {
  connecting: 'Opening the realtime connection',
  live: 'Realtime connection is live',
  reconnecting: 'Realtime connection dropped — reconnecting; this view may be stale',
  down: 'No realtime connection — this view is not live',
};

const DOT_COLORS: Readonly<Record<RealtimeConnectionState, string>> = {
  connecting: 'text.disabled',
  live: 'trading.long',
  reconnecting: 'warning.main',
  down: 'trading.critical',
};

/**
 * The app bar's connection dot -- the wireframe's "● Live" HUD chip, now driven by the realtime socket (gh#649).
 *
 * The dot reads the SignalR connection state, not a `/health` poll: a degraded socket must LOOK degraded (R-19,
 * ADR-0013 -- declared-unknown over stale-but-confident). `reconnecting` is amber and says the view may be stale;
 * `down` is the trading-critical red. Green is reserved for a genuinely live stream, never an optimistic default.
 * Outside a `RealtimeProvider` (which should never happen in the shell) it falls back to `down`, not a false green.
 */
export function ConnectionStatus() {
  const realtime = useOptionalRealtime();
  const state: RealtimeConnectionState = realtime?.connectionState ?? 'down';

  return (
    <Tooltip title={DESCRIPTIONS[state]}>
      <Box
        // role="status" is both the honest semantics (a polite live region announcing the connection state) and
        // what the test queries by -- no test-only attribute needed.
        role="status"
        data-status={state}
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: 0.75,
          px: 1,
          py: 0.5,
          borderRadius: 1,
          bgcolor: 'background.surface1',
          border: '1px solid',
          borderColor: 'divider',
          flex: 'none',
        }}
      >
        <Box
          aria-hidden
          sx={{
            width: 8,
            height: 8,
            borderRadius: '50%',
            bgcolor: DOT_COLORS[state],
            flex: 'none',
          }}
        />
        <Typography variant="caption" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>
          {LABELS[state]}
        </Typography>
      </Box>
    </Tooltip>
  );
}
