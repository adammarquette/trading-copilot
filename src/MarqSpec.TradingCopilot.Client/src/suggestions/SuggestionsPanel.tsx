import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';

import { useAccounts } from '../accounts/AccountProvider';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import { SuggestionList } from './SuggestionList';

/**
 * The workspace's suggestion panel — the actionable list, scoped to the **active account** (R-14). Extracted from
 * the old surface (gh#654) so the workspace (gh#725) can place it *beside* the chart: it is a panel, not a route
 * surface, so it carries no `data-surface` of its own — {@link WorkspaceSurface} owns the shell's surface contract.
 *
 * Scoping is not decoration: a suggestion belongs to one account, and `POST /take` arms against that account's gate.
 * So the account context is resolved *before* anything renders — there is no in-between where the operator could act
 * on a card without knowing which account an order would hit.
 */
export function SuggestionsPanel(): React.JSX.Element {
  const accounts = useAccounts();

  return (
    <Box sx={{ height: '100%', overflowY: 'auto' }}>
      <Box sx={{ px: 2, pt: 2 }}>
        <Typography variant="h6" component="h2" sx={{ fontWeight: 600 }}>
          Suggestions
        </Typography>
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          The co-pilot proposes; you dispose. Approving arms an editable ticket — it does not send.
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
          description="Connect a trading account before the co-pilot can propose anything against it."
          tag="R-14"
        />
      ) : null}

      {accounts.status === 'ready' ? (
        // `key` is load-bearing, not a list-render habit (gh#713). Without it the list keeps its instance across an
        // account switch, so a SLOW response for the account just left can resolve LAST and replace the current
        // account's cards -- with every action still enabled. The take arms against the suggestion's own account
        // server-side, so an operator who believes they moved to a practice account could arm on the live one they
        // left, with the mode chip as the only tell. Keying on the account discards that instance instead, so the
        // stale callback lands on a component that no longer exists.
        <SuggestionList key={accounts.activeAccount.id} accountId={accounts.activeAccount.id} />
      ) : null}
    </Box>
  );
}
