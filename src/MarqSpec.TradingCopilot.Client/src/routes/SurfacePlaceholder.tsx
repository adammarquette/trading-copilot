import Box from '@mui/material/Box';

import { EmptyState } from '../components/EmptyState';
import type { Destination } from '../navigation/destinations';

/**
 * What a destination renders until its own card builds it.
 *
 * An empty state rather than a stub UI: a placeholder that mimics the real surface -- fake candles,
 * zeroed P&L -- is indistinguishable from the real surface having lost its data, and this app is one
 * where that distinction matters. This says plainly that nothing is here yet, and names the requirement
 * that will fill it.
 */
export interface SurfacePlaceholderProps {
  readonly destination: Destination;
}

export function SurfacePlaceholder({ destination }: SurfacePlaceholderProps) {
  const { Icon, label, summary, requirement } = destination;

  return (
    <Box data-testid="surface" data-surface={destination.id} sx={{ height: '100%' }}>
      <EmptyState
        icon={<Icon sx={{ fontSize: 40 }} />}
        title={label}
        description={summary}
        tag={requirement}
      />
    </Box>
  );
}
