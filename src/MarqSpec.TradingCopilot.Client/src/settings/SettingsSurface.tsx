import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';

import { useAccounts } from '../accounts/AccountProvider';
import { FirmOnboarding } from '../accounts/FirmOnboarding';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import type { Destination } from '../navigation/destinations';
import { AiCostAttribution } from './AiCostAttribution';
import { AiSpendSettings } from './AiSpendSettings';
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
 *
 * When there are **no accounts yet** this is also where a fresh deployment starts: the empty state is the
 * {@link FirmOnboarding} walk (gh#653), because there is nothing to declare risk against until a firm and its
 * accounts exist. Finishing it reloads the roster, so the surface re-resolves into the account-scoped view.
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
          {accounts.status === 'empty'
            ? 'Set up a firm and the accounts under it to start.'
            : "The risk inputs this account declares, and today's headroom against them."}
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
        <Box sx={{ p: 2 }}>
          <FirmOnboarding onComplete={accounts.reload} />
        </Box>
      ) : null}

      {accounts.status === 'ready' ? (
        <Stack sx={{ p: 2 }} spacing={4}>
          <RiskSettings key={accounts.activeAccount.id} accountId={accounts.activeAccount.id} />
          <Box>
            <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600, mb: 1 }}>
              AI usage &amp; spend
            </Typography>
            <AiSpendSettings />
          </Box>
          <Box>
            <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600, mb: 1 }}>
              AI cost attribution
            </Typography>
            <AiCostAttribution />
          </Box>
        </Stack>
      ) : null}
    </Box>
  );
}
