import Box from '@mui/material/Box';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useTheme } from '@mui/material/styles';

import type { DailyRealizedPnL } from '../api/journal';
import { formatSignedUsd, toneOf } from './format';
import { equityCurve, monthStats } from './month';

/**
 * The month's stat strip and equity curve (gh#659, R-9) — the two glances the wireframe puts above the
 * calendar: *how did the month go*, and *what shape did it take getting there*.
 *
 * Everything here is derived from the same day rows the calendar draws, so the strip and the grid can never
 * disagree. Nothing is fetched twice and nothing is summed server-side a second time.
 *
 * **Sharing the rows is necessary but not sufficient**, and the difference cost a review round: this component
 * takes `days` unfiltered while `PnlCalendar` filters them to the month it was handed, so a *superseded* read
 * landing in state would repaint the strip from a month the grid had already discarded. What actually holds the
 * invariant is `JournalMonth`'s generation guard, which lets only the newest read write.
 *
 * **Absence stays absence.** A month with no traded days shows an em dash for the average and the extremes,
 * and says the curve is empty rather than drawing a flat line at zero — `$0.00` claims the operator traded to
 * breakeven, which is a different statement from "nothing was traded".
 */
export interface MonthSummaryProps {
  readonly days: readonly DailyRealizedPnL[];
}

function Stat({
  id,
  label,
  value,
  tone,
}: {
  readonly id: string;
  readonly label: string;
  readonly value: string;
  readonly tone?: 'positive' | 'negative';
}) {
  const theme = useTheme();
  const color =
    tone === 'positive'
      ? theme.palette.trading.long
      : tone === 'negative'
        ? theme.palette.trading.short
        : undefined;

  return (
    <Paper variant="outlined" sx={{ px: 1.5, py: 1, flex: '1 1 120px', minWidth: 110 }}>
      <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>
        {label}
      </Typography>
      <Typography
        data-testid={`journal-stat-${id}`}
        variant="body2"
        sx={{ fontWeight: 700, fontVariantNumeric: 'tabular-nums', color }}
      >
        {value}
      </Typography>
    </Paper>
  );
}

/** The width/height the curve is drawn in; it scales to its box, so these are only the aspect ratio. */
const VIEW_WIDTH = 300;
const VIEW_HEIGHT = 90;

export function MonthSummary({ days }: MonthSummaryProps) {
  const theme = useTheme();
  const stats = monthStats(days);
  const points = equityCurve(days);

  const cumulatives = points.map((point) => point.cumulative);
  // The curve is bounded by its own extremes AND by zero, so a month that never went negative still shows
  // the baseline it stayed above rather than being rescaled into looking like a drawdown.
  const high = Math.max(0, ...cumulatives);
  const low = Math.min(0, ...cumulatives);
  const span = high - low || 1;

  const coordinates = points.map((point, index) => {
    const x = points.length === 1 ? VIEW_WIDTH / 2 : (index / (points.length - 1)) * VIEW_WIDTH;
    const y = VIEW_HEIGHT - ((point.cumulative - low) / span) * VIEW_HEIGHT;
    return { ...point, x, y };
  });

  const ending = points.at(-1)?.cumulative ?? null;
  const curveColor = (ending ?? 0) < 0 ? theme.palette.trading.short : theme.palette.trading.long;

  return (
    <Stack spacing={1.5}>
      <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1 }}>
        <Stat
          id="net"
          label="Net (month)"
          value={days.length === 0 ? '—' : formatSignedUsd(stats.net)}
          tone={toneOf(stats.net)}
        />
        <Stat
          id="days"
          label="Green / red days"
          value={`${String(stats.greenDays)} / ${String(stats.redDays)}`}
        />
        {/* The best day of an all-red month is still a loss; the tone follows the figure (gh#659 review). */}
        <Stat
          id="best"
          label="Best day"
          value={stats.best === null ? '—' : formatSignedUsd(stats.best.realizedPnL)}
          tone={stats.best === null ? undefined : toneOf(stats.best.realizedPnL)}
        />
        <Stat
          id="worst"
          label="Worst day"
          value={stats.worst === null ? '—' : formatSignedUsd(stats.worst.realizedPnL)}
          tone={stats.worst === null ? undefined : toneOf(stats.worst.realizedPnL)}
        />
        <Stat id="average" label="Avg / day" value={formatSignedUsd(stats.averagePerDay)} />
      </Stack>

      <Paper variant="outlined" sx={{ p: 1.5 }}>
        <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 1 }}>
          Equity curve — cumulative realized P&amp;L
        </Typography>

        {coordinates.length === 0 ? (
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            Nothing realized this month yet.
          </Typography>
        ) : (
          <Box
            component="svg"
            data-testid="journal-equity-curve"
            role="img"
            aria-label={`Cumulative realized P&L — the month ends at ${formatSignedUsd(ending)}`}
            viewBox={`0 0 ${String(VIEW_WIDTH)} ${String(VIEW_HEIGHT)}`}
            preserveAspectRatio="none"
            sx={{ width: '100%', height: 90, display: 'block', overflow: 'visible' }}
          >
            <line
              x1={0}
              x2={VIEW_WIDTH}
              y1={VIEW_HEIGHT - ((0 - low) / span) * VIEW_HEIGHT}
              y2={VIEW_HEIGHT - ((0 - low) / span) * VIEW_HEIGHT}
              stroke={theme.palette.divider}
              strokeWidth={1}
            />
            <polyline
              points={coordinates.map((point) => `${String(point.x)},${String(point.y)}`).join(' ')}
              fill="none"
              stroke={curveColor}
              strokeWidth={2}
              vectorEffect="non-scaling-stroke"
            />
            {coordinates.map((point) => (
              <circle key={point.date} cx={point.x} cy={point.y} r={2.5} fill={curveColor} />
            ))}
          </Box>
        )}
      </Paper>
    </Stack>
  );
}
