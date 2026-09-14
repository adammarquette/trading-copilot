import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';

import type { Destination } from '../navigation/destinations';
import { TriggersSurface } from './TriggersSurface';

interface RulebookSurfaceProps {
  readonly destination: Destination;
}

/**
 * The Rulebook destination (R-7). Its navigation summary promises two things — "plain-language rules and the
 * deterministic triggers they compile to" — and **only the triggers half is built** (gh#991). The plain-language
 * rules are gh#660, blocked on gh#19 [A5].
 *
 * The header says so rather than leaving the absence to be inferred: a surface that silently delivers half of what
 * its own navigation entry promises reads as a bug, and an operator hunting for the rules half deserves to know it
 * is unbuilt rather than mis-navigated.
 */
export function RulebookSurface({ destination }: RulebookSurfaceProps) {
  return (
    <Box
      data-testid="surface"
      data-surface={destination.id}
      sx={{ height: '100%', overflowY: 'auto' }}
    >
      <Box sx={{ px: 2, pt: 2, pb: 1 }}>
        <Typography variant="h6" component="h2" sx={{ fontWeight: 600 }}>
          Triggers
        </Typography>
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          Standing conditions that alert you when an indicator crosses your threshold (R-7). A
          trigger does nothing until you confirm it — being switched on is not enough.
          Plain-language rules are not built yet.
        </Typography>
      </Box>
      <Box sx={{ px: 2, pb: 2 }}>
        <TriggersSurface />
      </Box>
    </Box>
  );
}
