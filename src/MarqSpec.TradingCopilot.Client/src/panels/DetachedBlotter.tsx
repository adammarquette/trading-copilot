import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';

import { useOptionalAccounts } from '../accounts/AccountProvider';
import { Blotter } from '../blotter/Blotter';

/**
 * The live blotter as a standalone pop-out panel (gh#651). It sources the active account from the provider the
 * detached route carries — exactly as the workspace does — and is **withheld** until one resolves: an empty blotter
 * would assert something about venue truth that has not been read (gh#656). Keyed by account so a switch remounts it
 * rather than showing one account's positions under another's header (gh#969).
 *
 * Because the lean pop-out frame carries **no app-bar account switcher** (the shell's home for the account states
 * and their retry), this surfaces the non-`ready` states itself — otherwise an account-load **error** would be both
 * invisible and unrecoverable in a detached window. Every non-`ready` branch withholds the blotter, so none of them
 * reads as a false "flat".
 */
export function DetachedBlotter(): React.JSX.Element {
  const accounts = useOptionalAccounts();

  if (accounts?.status === 'ready') {
    const activeAccountId = accounts.activeAccount.id;
    return (
      <Box sx={{ p: 1.5 }}>
        <Blotter key={activeAccountId} accountId={activeAccountId} />
      </Box>
    );
  }

  if (accounts?.status === 'error') {
    return (
      <Box sx={{ p: 2 }}>
        <Alert
          severity="error"
          data-testid="detached-blotter-error"
          action={
            <Button color="inherit" size="small" onClick={accounts.reload}>
              Retry
            </Button>
          }
        >
          The account list is unavailable — {accounts.message}
        </Alert>
      </Box>
    );
  }

  return (
    <Box sx={{ p: 2 }}>
      <Typography variant="body2" color="text.secondary" data-testid="detached-blotter-no-account">
        {accounts?.status === 'empty'
          ? 'No accounts are configured yet.'
          : 'Waiting for an account…'}
      </Typography>
    </Box>
  );
}
