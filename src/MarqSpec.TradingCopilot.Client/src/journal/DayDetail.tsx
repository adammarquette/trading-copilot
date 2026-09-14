import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import { type DayDetail as DayDetailData, getDayDetail } from '../api/journal';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import { formatSignedUsd, formatTradeCount } from './format';
import { JournalTradeCard } from './JournalTradeCard';
import { dayLabel, type IsoDay } from './month';

/**
 * One Central trading day's drill-down (gh#659, R-8) — the day's closed trades and their realized net.
 *
 * Three outcomes are kept apart, because collapsing any two of them misinforms the operator:
 *
 * - **A quiet day** is a 200 with no trades. It says so plainly. That is an answer, not an empty screen.
 * - **No journal** is the endpoint's 404 — an absent, foreign or `Undeclared` account (R-14 / R-20). An
 *   undeclared account trades nowhere, so it has nothing to report; that is a state to name, not an error.
 * - **A failed read** offers a retry, because retrying it is meaningful.
 *
 * The day is a prop, so picking another day on the calendar re-reads rather than reconciling in place.
 */
export interface DayDetailProps {
  readonly accountId: string;
  readonly date: IsoDay;
}

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly detail: DayDetailData | null };

export function DayDetail({ accountId, date }: DayDetailProps) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  // Deliberately free of a synchronous `setState`: this is what the mount effect runs, and a setState in an
  // effect body cascades a render. Showing the spinner again is the *caller's* gesture -- see `retry` below,
  // and the `key` JournalMonth mounts this under, which gives a newly picked day a fresh `loading` rather
  // than the previous day's rows under a new heading.
  const load = useCallback(() => {
    void getDayDetail(accountId, date).then((result) => {
      if (!mounted.current) {
        return;
      }
      setState(
        result.ok
          ? { kind: 'loaded', detail: result.data }
          : { kind: 'error', message: result.kind === 'refused' ? result.reason : result.error },
      );
    });
  }, [accountId, date]);

  useEffect(() => {
    load();
  }, [load]);

  /** The retry gesture: an event handler, so putting the surface back to loading here is not a cascade. */
  const retry = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  if (state.kind === 'loading') {
    return <LoadingState label="Loading the day" fullHeight={false} />;
  }

  if (state.kind === 'error') {
    return (
      <EmptyState
        title="Could not load the day"
        description={state.message}
        action={
          <Button variant="outlined" size="small" onClick={retry}>
            Try again
          </Button>
        }
        tag="R-8"
      />
    );
  }

  if (state.detail === null) {
    return (
      <EmptyState
        title="This account has no journal"
        description="An account with no declared mode trades nowhere (R-14), so there is nothing to report for it. Declare the account practice or live to start journaling."
        tag="R-8 · R-14"
      />
    );
  }

  const { detail } = state;

  return (
    <Stack spacing={1.5} data-testid="day-detail">
      <Box>
        <Typography variant="subtitle2" sx={{ fontWeight: 700 }}>
          {dayLabel(detail.date)}
        </Typography>
        <Typography
          data-testid="day-summary"
          variant="caption"
          sx={{ color: 'text.secondary', fontVariantNumeric: 'tabular-nums' }}
        >
          {`${formatTradeCount(detail.trades.length)} · ${formatSignedUsd(detail.realizedPnL)}`}
        </Typography>
      </Box>

      {detail.trades.length === 0 ? (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          No trades closed on this day.
        </Typography>
      ) : (
        detail.trades.map((trade) => <JournalTradeCard key={trade.id} trade={trade} />)
      )}
    </Stack>
  );
}
