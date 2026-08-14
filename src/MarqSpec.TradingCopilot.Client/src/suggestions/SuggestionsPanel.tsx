import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { useCallback, useState } from 'react';

import { useAccounts } from '../accounts/AccountProvider';
import { OrderSide, type StagedTicket, type Suggestion } from '../api/suggestions';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import { StagedTicketReview } from '../orders/StagedTicketReview';
import { SuggestionList } from './SuggestionList';

/**
 * The workspace's suggestion panel — the actionable list, scoped to the **active account** (R-14). Extracted from
 * the old surface (gh#654) so the workspace (gh#725) can place it *beside* the chart: it is a panel, not a route
 * surface, so it carries no `data-surface` of its own — {@link WorkspaceSurface} owns the shell's surface contract.
 *
 * Scoping is not decoration: a suggestion belongs to one account, and `POST /take` arms against that account's gate.
 * So the account context is resolved *before* anything renders — there is no in-between where the operator could act
 * on a card without knowing which account an order would hit.
 *
 * **Where it comes together (gh#655).** Approving a card *arms* an editable, unsent ticket and hands it here; the
 * panel then presents that staged ticket for **review and sending** — a separate, gated step (R-11b). Sending does
 * not happen on the card. Once the ticket has been sent or cancelled, the list is re-fetched, because a sent
 * suggestion is no longer actionable and the card's local "armed" state would otherwise linger as a stale claim.
 */
export function SuggestionsPanel(): React.JSX.Element {
  const accounts = useAccounts();
  const [armed, setArmed] = useState<{
    readonly suggestion: Suggestion;
    readonly ticket: StagedTicket;
  } | null>(null);
  // Bumped when a review finishes, to force a fresh list — the server, not the card's local state, is the truth
  // about what is still actionable after a send.
  const [reviewNonce, setReviewNonce] = useState(0);

  const handleDone = useCallback(() => {
    setArmed(null);
    setReviewNonce((nonce) => nonce + 1);
  }, []);

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

      {armed !== null ? (
        <Box sx={{ borderBottom: '1px solid', borderColor: 'divider' }}>
          <StagedTicketReview
            ticket={armed.ticket}
            requested={armed.suggestion.size}
            summary={`${armed.suggestion.side === OrderSide.Buy ? 'LONG' : 'SHORT'} ${armed.suggestion.instrument}`}
            onDone={handleDone}
          />
        </Box>
      ) : null}

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
        // stale callback lands on a component that no longer exists. The nonce extends the same idea to a completed
        // review: a fresh list after a send reflects the server's actionable set, not the card's stale local state.
        <SuggestionList
          key={`${accounts.activeAccount.id}:${String(reviewNonce)}`}
          accountId={accounts.activeAccount.id}
          onArmed={(suggestion, ticket) => {
            setArmed({ suggestion, ticket });
          }}
        />
      ) : null}
    </Box>
  );
}
