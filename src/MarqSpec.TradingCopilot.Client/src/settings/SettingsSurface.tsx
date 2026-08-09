import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';

import { useAccounts } from '../accounts/AccountProvider';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import type { Destination } from '../navigation/destinations';
import { RiskSettings } from './RiskSettings';

/**
 * The settings surface (gh#25 U3), scoped to the **active account** (R-14). Its first section is the R-5 risk
 * profile and today's headroom; the AI-spend and news-relevance sections the wireframe also draws are their own
 * increments (spend has no read endpoint yet; relevance belongs to the news surface).
 *
 * Account scoping is not decoration: a risk profile is declared per account, and the gate that enforces it runs
 * against that account. So the account context is resolved before the risk section mounts, and the section is keyed
 * on the account id — switching accounts remounts it, so a slow load for the account just left can never resolve
 * into the new one's view.
 */
export interface SettingsSurfaceProps {
  readonly destination: Destination;
}

export function SettingsSurface({ destination }: SettingsSurfaceProps) {
  const accounts = useAccounts();

  return (
    <Box
      data-testid="surface"
      data-surface={destination.id}
      sx={{ height: '100%', overflowY: 'auto' }}
    >
      <Box sx={{ px: 2, pt: 2 }}>
        <Typography variant="h6" component="h2" sx={{ fontWeight: 600 }}>
          Settings
        </Typography>
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          The risk inputs this account declares, and today&apos;s headroom against them.
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
          title="No accounts yet"
          description="Connect a trading account before declaring the risk it trades under."
          tag="R-14"
        />
      ) : null}

      {accounts.status === 'ready' ? (
        <Box sx={{ p: 2 }}>
          <RiskSettings key={accounts.activeAccount.id} accountId={accounts.activeAccount.id} />
        </Box>
      ) : null}
    </Box>
  );
}
