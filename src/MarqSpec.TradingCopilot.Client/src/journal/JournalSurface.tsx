import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { useState } from 'react';

import { useAccounts } from '../accounts/AccountProvider';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import type { Destination } from '../navigation/destinations';
import { JournalMonth } from './JournalMonth';
import { centralDay } from './month';

/**
 * The journal surface (gh#659, R-8 / R-9) — realized P&L by day, drillable into a day's trades, their
 * taken-vs-suggested delta and the operator's feedback.
 *
 * **Scoped to the active account, and keyed on it.** A journal is per account, and the endpoints resolve that
 * account's **own current** mode server-side (R-14), so a practice result can never blend into a live report.
 * Keying the month view on the account id remounts it on a switch, which is what stops a slow read for the
 * account just left from resolving into the new one's view.
 *
 * **Today is the Central trading day, resolved once here.** The read groups by Chicago's calendar day; an
 * operator east of it would otherwise open the journal on tomorrow — a day the server has nothing for. It is
 * held in state rather than recomputed each render so the default day cannot shift under a re-render, and it
 * is a *display* default only: nothing here is a clock the rest of the system depends on.
 *
 * The `data-surface` contract is carried in **every** state, loading included — the shell keys navigation off
 * it, and a surface that only wears it once loaded reads as a missing surface while the roster resolves.
 */
export interface JournalSurfaceProps {
  readonly destination: Destination;
}

export function JournalSurface({ destination }: JournalSurfaceProps) {
  const accounts = useAccounts();
  const [today] = useState(() => centralDay(new Date()));

  return (
    <Box
      data-testid="surface"
      data-surface={destination.id}
      sx={{ height: '100%', overflowY: 'auto' }}
    >
      <Box sx={{ px: 2, pt: 2 }}>
        <Typography variant="h6" component="h2" sx={{ fontWeight: 600 }}>
          Journal
        </Typography>
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          Realized P&amp;L by Central trading day. Pick a day to review its trades, what was
          suggested against what was taken, and your own notes.
        </Typography>
      </Box>

      {accounts.status === 'loading' ? <LoadingState label="Loading the account" /> : null}

      {accounts.status === 'error' ? (
        <EmptyState
          title="No account context"
          description={accounts.message}
          action={
            <Button variant="outlined" size="small" onClick={accounts.reload}>
              Try again
            </Button>
          }
          tag="R-14"
        />
      ) : null}

      {accounts.status === 'empty' ? (
        <EmptyState
          title="No account to journal"
          description="A journal belongs to an account. Set up a firm and its accounts in Settings, and the days you trade will appear here."
          tag="R-8"
        />
      ) : null}

      {accounts.status === 'ready' ? (
        <Box sx={{ p: 2 }}>
          <JournalMonth
            key={accounts.activeAccount.id}
            accountId={accounts.activeAccount.id}
            today={today}
          />
        </Box>
      ) : null}
    </Box>
  );
}
