import Box from '@mui/material/Box';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';

import type { VenueSetup } from '../api/venues';

export interface VenueCredentialGuidanceProps {
  /** The venue's setup contract (identity + labelled credential schema) to render as guidance. */
  readonly venue: VenueSetup;
}

/**
 * Renders a venue's setup contract (gh#64, ADR-0023) as read-only onboarding guidance: each credential field's
 * config key mapped to its **true label**, whether it is a secret, and its help text. It makes ProjectX's
 * "`ApiKey` is really the username" trap legible where the operator wires the login, and it auto-adapts to whatever
 * a venue declares (2 fields for ProjectX, 7 for Tradovate). It **never collects a value** — credentials are set
 * server-side (ADR-0015); this is a reference, not an input.
 */
export function VenueCredentialGuidance({ venue }: VenueCredentialGuidanceProps) {
  return (
    <Box data-testid="venue-credential-guidance" sx={{ mb: 2 }}>
      <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block', mb: 1 }}>
        {venue.displayName} — the fields this login needs, set server-side. The config key on the
        left is not always named for what it actually holds:
      </Typography>
      <Stack spacing={1}>
        {venue.credentials.map((field) => (
          <Box key={field.key} data-testid={`venue-credential-${field.key}`}>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap' }}>
              <Typography
                variant="body2"
                component="code"
                sx={{ fontFamily: 'monospace', color: 'text.secondary' }}
              >
                {field.key}
              </Typography>
              <Typography
                variant="body2"
                component="span"
                sx={{ color: 'text.secondary' }}
                aria-hidden
              >
                &rarr;
              </Typography>
              <Typography variant="body2" component="span" sx={{ fontWeight: 600 }}>
                {field.label}
              </Typography>
              {field.secret ? <Chip label="secret" size="small" variant="outlined" /> : null}
            </Box>
            {field.helpText !== null ? (
              <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>
                {field.helpText}
              </Typography>
            ) : null}
          </Box>
        ))}
      </Stack>
    </Box>
  );
}
