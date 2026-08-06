import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import { useTheme } from '@mui/material/styles';

import { useNow } from '../time/clock';
import { formatRemaining, type Validity, type ValidityUrgency, validityAt } from './validity';

/**
 * The live validity window (R-4) — a running clock, never a timestamp.
 *
 * Colour comes from `palette.trading`, the semantic scale, and not from the brand accent: "this is about to stop
 * being takeable" is a trading status, and status must not compete with chrome (ADR-0005). The ramp is
 * deliberately coarse — normal, warn, critical — so every state on screen is a token someone named.
 */

export interface ValidityCountdownProps {
  /** The suggestion's `expiresAt`, ISO-8601 from the API. */
  readonly expiresAt: string;
}

/** The window's state right now — exported so a card can gate on the same reading the operator is looking at. */
export function useValidity(expiresAt: string): Validity {
  const now = useNow();
  const expiresAtMs = new Date(expiresAt).getTime();

  // A window we cannot parse is treated as already closed. Failing toward "not takeable" is the only safe
  // direction: the alternative is an unbounded, never-expiring card offering to arm an order.
  return validityAt(Number.isNaN(expiresAtMs) ? 0 : expiresAtMs, now);
}

export function ValidityCountdown({ expiresAt }: ValidityCountdownProps) {
  const theme = useTheme();
  const { remainingMs, urgency, expired } = useValidity(expiresAt);

  const colour: Record<ValidityUrgency, string> = {
    normal: theme.palette.text.secondary,
    warn: theme.palette.trading.warn,
    critical: theme.palette.trading.critical,
    expired: theme.palette.text.disabled,
  };

  return (
    <Box
      // `timer` is the role for a running countdown. It is NOT a live region: announcing every second would make
      // the card unusable with a screen reader. The card announces the one transition that matters — expiry —
      // through its own polite status line.
      role="timer"
      aria-label={expired ? 'Validity window closed' : `Valid for ${formatRemaining(remainingMs)}`}
      data-testid="validity-countdown"
      data-urgency={urgency}
      data-expired={expired ? 'true' : 'false'}
      sx={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 0.5,
        px: 0.75,
        py: 0.25,
        borderRadius: 5,
        border: '1px solid',
        borderColor: colour[urgency],
        color: colour[urgency],
      }}
    >
      <Typography variant="caption" sx={{ fontSize: 10.5, letterSpacing: '.04em' }}>
        {expired ? 'expired' : 'valid'}
      </Typography>
      {expired ? null : (
        <Typography variant="numeric" sx={{ fontSize: 12, fontWeight: 600 }}>
          {formatRemaining(remainingMs)}
        </Typography>
      )}
    </Box>
  );
}
