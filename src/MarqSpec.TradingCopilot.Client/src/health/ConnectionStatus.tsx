import Box from '@mui/material/Box';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';

import { useBffHealth, type HealthStatus } from './useBffHealth';

const LABELS: Readonly<Record<HealthStatus, string>> = {
  checking: 'Checking',
  reachable: 'Live',
  unreachable: 'Offline',
};

const DESCRIPTIONS: Readonly<Record<HealthStatus, string>> = {
  checking: 'Checking whether the server is reachable',
  reachable: 'The server answered /health',
  unreachable: 'The server did not answer /health',
};

/**
 * The app bar's connection dot -- the wireframe's "● Live" HUD chip.
 *
 * The dot is drawn from the trading semantics, not the accent: a dead connection is a state the
 * operator must read instantly, and ADR-0005 reserves exactly this scale for that. `checking` is a
 * third colour rather than an optimistic green, because a probe that has not answered is not a
 * connection that works.
 */
export function ConnectionStatus() {
  const health = useBffHealth();

  const dotColor =
    health === 'reachable'
      ? 'trading.long'
      : health === 'unreachable'
        ? 'trading.critical'
        : 'text.disabled';

  return (
    <Tooltip title={DESCRIPTIONS[health]}>
      <Box
        // role="status" is both the honest semantics (a polite live region announcing a result that
        // arrives after paint) and what the test queries by -- no test-only attribute needed.
        role="status"
        data-status={health}
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
          sx={{ width: 8, height: 8, borderRadius: '50%', bgcolor: dotColor, flex: 'none' }}
        />
        <Typography variant="caption" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>
          {LABELS[health]}
        </Typography>
      </Box>
    </Tooltip>
  );
}
