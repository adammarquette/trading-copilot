import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';

import type { Destination } from '../navigation/destinations';
import { NewsFeed } from './NewsFeed';
import { RelevanceMaps } from './RelevanceMaps';
import { RelevanceTopics } from './RelevanceTopics';

/**
 * The News surface (gh#25 U3, R-2). The **relevance configuration** — the ticker↔instrument maps and the topics
 * (gh#658) that decide which news attaches to which instrument, and how loudly — is **deployment-global**, not
 * account-scoped, so unlike the settings surface nothing here resolves the active account. Below it sits the
 * **personalized news feed** (gh#742), ranked by the operator's own star / mute salience profile; that feedback is
 * per-operator, but it is a soft reorder weight only and never touches a risk limit or a trade (R-6).
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
        <NewsFeed />
      </Box>
    </Box>
  );
}
