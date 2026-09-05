import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import ChevronLeftIcon from '@mui/icons-material/ChevronLeft';
import ChevronRightIcon from '@mui/icons-material/ChevronRight';
import Stack from '@mui/material/Stack';
import { useCallback, useEffect, useRef, useState } from 'react';

import { type DailyRealizedPnL, getDailyRealizedPnL } from '../api/journal';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import { DayDetail } from './DayDetail';
import { firstDayOf, type IsoDay, type IsoMonth, monthOf, monthWindow, shiftMonth } from './month';
import { MonthSummary } from './MonthSummary';
import { PnlCalendar } from './PnlCalendar';

/**
 * One account's journal month (gh#659, R-8 / R-9): the stat strip, the equity curve, the P&L-by-day calendar,
 * and the day the operator has drilled into.
 *
 * **`today` is passed in, never read from a clock here.** It is a *Central* trading day — the key the endpoint
 * groups by — resolved once by {@link ./JournalSurface}. Keeping the clock out of this component is what makes
 * the month arithmetic testable without mocking time, and what stops a browser-local "today" from opening the
 * journal on a day the server reports nothing for.
 *
 * **Paging months moves the drill-down with it.** Leaving the selected day behind in the month just left
 * would show a day the calendar above no longer draws. Stepping back to the current month re-selects today,
 * because that is what R-8 makes the default view.
 *
 * **The future is not reviewable.** `Next` stops at the current Central month: there is nothing to review
 * there, and an empty calendar in October reads as data that went missing rather than data that does not
 * exist yet.
 */
export interface JournalMonthProps {
  readonly accountId: string;
  /** Today, as a Central trading day (`YYYY-MM-DD`). */
  readonly today: IsoDay;
}

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly days: readonly DailyRealizedPnL[] | null };

export function JournalMonth({ accountId, today }: JournalMonthProps) {
  const currentMonth = monthOf(today);
  const [month, setMonth] = useState<IsoMonth>(currentMonth);
  const [selectedDate, setSelectedDate] = useState<IsoDay>(today);
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  // Deliberately free of a synchronous `setState`: this is what the mount effect runs, and a setState in an
  // effect body cascades a render. Putting the surface back to loading is a *gesture* -- `retry` and `step`
  // below, both event handlers.
  const load = useCallback(() => {
    const { from, to } = monthWindow(month);
    void getDailyRealizedPnL(accountId, from, to).then((result) => {
      if (!mounted.current) {
        return;
      }
      setState(
        result.ok
          ? { kind: 'loaded', days: result.data }
          : { kind: 'error', message: result.kind === 'refused' ? result.reason : result.error },
      );
    });
  }, [accountId, month]);

  useEffect(() => {
    load();
  }, [load]);

  /** The retry gesture: an event handler, so putting the surface back to loading here is not a cascade. */
  const retry = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  const step = useCallback(
    (delta: number) => {
      const next = shiftMonth(month, delta);
      // Back to loading before the new month's read lands, so the calendar is never labelled one month while
      // still drawing another's days.
      setState({ kind: 'loading' });
      setMonth(next);
      // The drill-down follows the calendar rather than being left on a day off the grid. Coming back to the
      // month in progress lands on today, which is R-8's stated default view.
      setSelectedDate(next === currentMonth ? today : firstDayOf(next));
    },
    [currentMonth, month, today],
  );

  return (
    <Stack spacing={2} data-testid="journal-month">
      <Stack direction="row" spacing={1} alignItems="center">
        <IconButton aria-label="Previous month" size="small" onClick={() => step(-1)}>
          <ChevronLeftIcon fontSize="small" />
        </IconButton>
        <IconButton
          aria-label="Next month"
          size="small"
          disabled={month >= currentMonth}
          onClick={() => step(1)}
        >
          <ChevronRightIcon fontSize="small" />
        </IconButton>
      </Stack>

      {state.kind === 'loading' ? (
        <LoadingState label="Loading the month" fullHeight={false} />
      ) : null}

      {state.kind === 'error' ? (
        <EmptyState
          title="Could not load the journal"
          description={state.message}
          action={
            <Button variant="outlined" size="small" onClick={retry}>
              Try again
            </Button>
          }
          tag="R-9"
        />
      ) : null}

      {state.kind === 'loaded' && state.days === null ? (
        <EmptyState
          title="This account has no journal"
          description="An account with no declared mode trades nowhere (R-14), so there is nothing to report for it. Declare the account practice or live to start journaling."
          tag="R-9 · R-14"
        />
      ) : null}

      {state.kind === 'loaded' && state.days !== null ? (
        <Box
          sx={{
            display: 'grid',
            gap: 2,
            gridTemplateColumns: { xs: '1fr', lg: 'minmax(0, 3fr) minmax(0, 2fr)' },
            alignItems: 'start',
          }}
        >
          <Stack spacing={2}>
            <MonthSummary days={state.days} />
            <PnlCalendar
              month={month}
              days={state.days}
              selectedDate={selectedDate}
              onSelectDate={setSelectedDate}
            />
          </Stack>
          {/*
            Keyed on the day: picking another day REMOUNTS the drill-down at its own `loading` state, rather
            than leaving the day just left on screen under the new heading while its read is in flight.
          */}
          <DayDetail key={selectedDate} accountId={accountId} date={selectedDate} />
        </Box>
      ) : null}
    </Stack>
  );
}
