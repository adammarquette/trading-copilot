import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';

import { useOptionalAccounts } from '../accounts/AccountProvider';
import { Blotter } from '../blotter/Blotter';

/**
 * The live blotter as a standalone pop-out panel (gh#651). It sources the active account from the provider the
 * detached route carries — exactly as the workspace does — and is **withheld** until one resolves: an empty blotter
 * would assert something about venue truth that has not been read (gh#656). Keyed by account so a switch remounts it
 * rather than showing one account's positions under another's header (gh#969).
 */
export function DetachedBlotter(): React.JSX.Element {
  const accounts = useOptionalAccounts();
  const activeAccountId = accounts?.status === 'ready' ? accounts.activeAccount.id : null;

  if (activeAccountId === null) {
    return (
      <Box sx={{ p: 2 }}>
        <Typography
          variant="body2"
          color="text.secondary"
          data-testid="detached-blotter-no-account"
        >
          Waiting for an account…
        </Typography>
      </Box>
    );
  }

  return (
    <Box sx={{ p: 1.5 }}>
      <Blotter key={activeAccountId} accountId={activeAccountId} />
    </Box>
  );
}
