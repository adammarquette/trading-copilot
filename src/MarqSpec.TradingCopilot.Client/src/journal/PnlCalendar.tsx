import Box from '@mui/material/Box';
import ButtonBase from '@mui/material/ButtonBase';
import Paper from '@mui/material/Paper';
import Typography from '@mui/material/Typography';
import { alpha, useTheme } from '@mui/material/styles';

import type { DailyRealizedPnL } from '../api/journal';
import { formatSignedUsd, formatTradeCount } from './format';
import {
  type CalendarCell,
  type IsoDay,
  type IsoMonth,
  monthGrid,
  monthLabel,
  shadeIntensity,
  shortDayLabel,
} from './month';

/**
 * The P&L-by-day calendar (gh#659, R-9) — a month of realized days, green/red and magnitude-shaded, each one
 * a way into that day's trades.
 *
 * **Colour is the glance, never the answer.** Every cell carries an accessible name that states the figure and
 * the trade count in words, so a screen-reader user and a colour-blind operator read the same month a sighted
 * one does. Shading is scaled against the month's own largest day, which is what makes a $60 day and a $6,000
 * day distinguishable at all — a fixed scale flattens most months to one shade.
 *
 * **An untraded day is not a flat day.** The read returns no row for a day the operator stood aside, and this
 * draws it plainly untraded rather than as `$0.00`, which would read as "I broke even". It stays clickable:
 * drilling into a quiet day and being told nothing closed is an answer.
 */
export interface PnlCalendarProps {
  readonly month: IsoMonth;
  readonly days: readonly DailyRealizedPnL[];
  readonly selectedDate: IsoDay;
  readonly onSelectDate: (date: IsoDay) => void;
}

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;

/** What a screen reader reads on a cell — the whole answer, not a colour cue. */
function cellLabel(cell: CalendarCell): string {
  if (cell.realizedPnL === null) {
    return `${shortDayLabel(cell.date)} — no trades`;
  }
  return `${shortDayLabel(cell.date)} — ${formatSignedUsd(cell.realizedPnL)}, ${formatTradeCount(cell.tradeCount)}`;
}

export function PnlCalendar({ month, days, selectedDate, onSelectDate }: PnlCalendarProps) {
  const theme = useTheme();
  const weeks = monthGrid(month, days);
  const maxAbsolute = Math.max(0, ...weeks.flat().map((cell) => Math.abs(cell?.realizedPnL ?? 0)));

  return (
    <Paper variant="outlined" data-testid="pnl-calendar" sx={{ p: 1.5 }}>
      <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 1 }}>
        {monthLabel(month)}
      </Typography>

      <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)', gap: 0.5 }}>
        {WEEKDAYS.map((weekday) => (
          <Typography
            key={weekday}
            variant="caption"
            sx={{ color: 'text.secondary', textAlign: 'center' }}
          >
            {weekday}
          </Typography>
        ))}

        {weeks.flat().map((cell, index) => {
          if (cell === null) {
            // A leading/trailing blank. Keyed by position because it *is* a position and nothing else.
            return <Box key={`blank-${String(index)}`} />;
          }

          const traded = cell.realizedPnL !== null;
          const positive = (cell.realizedPnL ?? 0) > 0;
          const intensity = shadeIntensity(cell.realizedPnL ?? 0, maxAbsolute);
          const hue = positive ? theme.palette.trading.long : theme.palette.trading.short;
          const selected = cell.date === selectedDate;

          return (
            <ButtonBase
              key={cell.date}
              aria-label={cellLabel(cell)}
              aria-pressed={selected}
              onClick={() => onSelectDate(cell.date)}
              sx={{
                display: 'flex',
                flexDirection: 'column',
                alignItems: 'flex-start',
                gap: 0.25,
                p: 0.75,
                minHeight: 54,
                borderRadius: 1,
                border: '1px solid',
                borderColor: selected ? 'primary.main' : 'divider',
                // 0.08 keeps the quietest traded day visibly tinted rather than indistinguishable from an
                // untraded one; the ceiling stays low enough for the text on top to stay legible.
                bgcolor:
                  traded && cell.realizedPnL !== 0
                    ? alpha(hue, 0.08 + intensity * 0.32)
                    : 'transparent',
              }}
            >
              <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1 }}>
                {cell.dayOfMonth}
              </Typography>
              <Typography
                variant="caption"
                aria-hidden
                sx={{
                  fontVariantNumeric: 'tabular-nums',
                  fontWeight: 600,
                  lineHeight: 1.2,
                  color: traded ? 'text.primary' : 'text.disabled',
                }}
              >
                {traded ? formatSignedUsd(cell.realizedPnL) : '·'}
              </Typography>
            </ButtonBase>
          );
        })}
      </Box>
    </Paper>
  );
}
