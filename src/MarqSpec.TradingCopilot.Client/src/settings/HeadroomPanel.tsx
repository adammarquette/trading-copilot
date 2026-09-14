import Chip from '@mui/material/Chip';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';

import type { DailyHeadroom } from '../api/risk';
import { formatUsd } from './format';

/**
 * Today's remaining room before the operator's personal governor and, where the firm imposes one, its hard daily
 * limit (gh#587). This is a **read** of what the gate is already tracking — a glance at how much of today's budget
 * is left — not a control: nothing here changes a limit, and the numbers come from the server's own projection.
 *
 * The firm-limit row appears only when a firm limit exists; an account whose only ceiling is the personal governor
 * should not show a blank "firm limit" line implying one it does not have.
 */
export interface HeadroomPanelProps {
  readonly headroom: DailyHeadroom;
}

function Stat({
  label,
  value,
  spent,
  reached,
}: {
  readonly label: string;
  readonly value: string;
  readonly spent?: boolean;
  readonly reached?: boolean;
}) {
  return (
    <Stack direction="row" justifyContent="space-between" alignItems="center" spacing={2}>
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {label}
      </Typography>
      <Stack direction="row" spacing={1} alignItems="center">
        <Typography variant="body2" sx={{ fontVariantNumeric: 'tabular-nums', fontWeight: 600 }}>
          {value}
        </Typography>
        {spent ? <Chip size="small" color="warning" label="spent" /> : null}
        {reached ? <Chip size="small" color="success" label="reached" /> : null}
      </Stack>
    </Stack>
  );
}

export function HeadroomPanel({ headroom }: HeadroomPanelProps) {
  return (
    <Paper variant="outlined" data-testid="headroom" sx={{ p: 2 }}>
      <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 1 }}>
        Today&apos;s headroom
      </Typography>
      <Stack spacing={1}>
        <Stat
          label="Under your daily governor"
          value={formatUsd(headroom.underGovernor)}
          spent={headroom.governorSpent}
        />
        {headroom.underDailyLossLimit !== null ? (
          <Stat
            label="Under the firm daily limit"
            value={formatUsd(headroom.underDailyLossLimit)}
            spent={headroom.dailyLossLimitSpent}
          />
        ) : null}
        <Stat label="Realized loss today" value={formatUsd(headroom.dayLoss)} />
        <Stat label="Realized profit today" value={formatUsd(headroom.dayRealizedProfit)} />
        {headroom.dailyProfitTarget !== null ? (
          <Stat
            label="Daily profit target"
            value={formatUsd(headroom.dailyProfitTarget)}
            reached={headroom.dailyTargetReached}
          />
        ) : null}
      </Stack>
    </Paper>
  );
}
