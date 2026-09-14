import Chip from '@mui/material/Chip';

import { TradingMode } from '../api/accounts';

/**
 * The account's resolved {@link TradingMode} as a chip (R-14, gh#653). This renders what the server resolved; it
 * never computes a mode from anything client-side — deriving mode from a venue's `simulated` flag once classified
 * every funded account as practice, and the gate failed toward real money.
 *
 * The three read differently on purpose. **Live** — capital at risk — is the loud one, because confusing it with
 * a demo is the expensive mistake. **Undeclared** is an error state, not a neutral one: the firm has not said what
 * the stage means, so the account is tradeable nowhere until the operator declares it.
 */
const PRESENTATION: Record<
  TradingMode,
  {
    readonly label: string;
    readonly color: 'info' | 'warning' | 'error';
    readonly variant: 'filled' | 'outlined';
  }
> = {
  [TradingMode.Practice]: { label: 'Practice', color: 'info', variant: 'outlined' },
  [TradingMode.Live]: { label: 'Live', color: 'warning', variant: 'filled' },
  [TradingMode.Undeclared]: { label: 'Undeclared', color: 'error', variant: 'filled' },
};

export interface ModeChipProps {
  readonly mode: TradingMode;
}

export function ModeChip({ mode }: ModeChipProps) {
  const { label, color, variant } = PRESENTATION[mode];
  return (
    <Chip
      label={label}
      color={color}
      variant={variant}
      size="small"
      data-testid="mode-chip"
      data-mode={label}
      sx={{ fontWeight: 700, letterSpacing: '.02em', height: 20, fontSize: 11 }}
    />
  );
}
