import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';

/**
 * The reserved app-bar slot for the practice / live mode chip (R-14).
 *
 * ADR-0005 puts this chip in the app bar at every breakpoint, and R-14 is why: the operator must never
 * have to work out which account an order is about to hit. The chip itself -- practice vs. live vs. the
 * undeclared stage that is refused everywhere -- lands with the account work; the shell only guarantees
 * it a place that is always on screen and always in the same spot.
 *
 * Rendered in the warn container while empty, deliberately. An unknown mode is not a neutral state.
 */
export interface ModeChipSlotProps {
  readonly children?: ReactNode;
}

export function ModeChipSlot({ children }: ModeChipSlotProps) {
  const filled = children !== undefined && children !== null;

  return (
    <Box
      data-testid="mode-chip-slot"
      data-filled={filled ? 'true' : 'false'}
      sx={{
        display: 'flex',
        alignItems: 'center',
        minWidth: 64,
        minHeight: 22,
        px: 1,
        borderRadius: 0.75,
        border: filled ? 'none' : '1px dashed',
        borderColor: 'divider',
      }}
    >
      {filled ? (
        children
      ) : (
        <Typography
          variant="caption"
          sx={{
            color: 'text.disabled',
            fontSize: 9.5,
            fontWeight: 700,
            letterSpacing: '.08em',
            whiteSpace: 'nowrap',
          }}
        >
          MODE
        </Typography>
      )}
    </Box>
  );
}
