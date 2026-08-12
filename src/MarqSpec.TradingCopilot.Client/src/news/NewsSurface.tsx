import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';

import type { Destination } from '../navigation/destinations';
import { RelevanceMaps } from './RelevanceMaps';
import { RelevanceTopics } from './RelevanceTopics';

/**
 * The News surface (gh#25 U3, R-2). Its first half is the **relevance configuration** — the ticker↔instrument maps
 * and the topics (gh#658) that decide which news attaches to which instrument, and how loudly. This config is
 * **deployment-global**, not account-scoped, so — unlike the settings surface — nothing here resolves the active
 * account. The relevance-ranked feed itself and the star / mute feedback on its items are their own increments.
 */
export interface NewsSurfaceProps {
  readonly destination: Destination;
}

export function NewsSurface({ destination }: NewsSurfaceProps) {
  return (
    <Box
      data-testid="surface"
      data-surface={destination.id}
      sx={{ height: '100%', overflowY: 'auto' }}
    >
      <Box sx={{ px: 2, pt: 2 }}>
        <Typography variant="h6" component="h2" sx={{ fontWeight: 600 }}>
          News relevance
        </Typography>
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          Which news attaches to which instrument (R-2). This configuration is shared across the
          deployment.
        </Typography>
      </Box>

      <Box sx={{ p: 2 }}>
        <RelevanceMaps />
        <RelevanceTopics />
      </Box>
    </Box>
  );
}
