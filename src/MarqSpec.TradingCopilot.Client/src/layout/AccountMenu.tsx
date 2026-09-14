import AccountCircleIcon from '@mui/icons-material/AccountCircleOutlined';
import LogoutIcon from '@mui/icons-material/LogoutOutlined';
import Divider from '@mui/material/Divider';
import IconButton from '@mui/material/IconButton';
import ListItemIcon from '@mui/material/ListItemIcon';
import ListItemText from '@mui/material/ListItemText';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Tooltip from '@mui/material/Tooltip';
import { useState } from 'react';

import { useOptionalAuth } from '../auth/AuthProvider';

/**
 * The app-bar account control (gh#652): who is signed in, and the one way to sign out.
 *
 * This is where account state belongs — and, by the same rule, where a credential field never does. The sign-in
 * form keeps its own surface precisely so the operator's identity and their password are never on screen at once.
 *
 * Rendered by {@link AppShell}, which only ever mounts behind {@link RequireAuth}, so in the running app the
 * session is always authenticated. It reads the session *optionally* anyway: the shell is mounted without a
 * provider in some layout tests, and an account menu with nobody to name simply has nothing to show.
 */
export function AccountMenu() {
  const auth = useOptionalAuth();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);

  if (!auth || auth.session.status !== 'authenticated') {
    return null;
  }

  const { user } = auth.session;
  const open = anchor !== null;

  function close() {
    setAnchor(null);
  }

  function signOut() {
    close();
    auth?.signOut();
  }

  return (
    <>
      <Tooltip title="Account">
        <IconButton
          onClick={(event) => setAnchor(event.currentTarget)}
          size="small"
          aria-label="Account"
          aria-haspopup="menu"
          aria-expanded={open}
          data-testid="account-menu-trigger"
          sx={{ color: 'text.secondary' }}
        >
          <AccountCircleIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Menu
        anchorEl={anchor}
        open={open}
        onClose={close}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
        slotProps={{ list: { dense: true, sx: { minWidth: 220 } } }}
      >
        <MenuItem disabled sx={{ opacity: '1 !important' }}>
          <ListItemText
            primary={user.displayName}
            secondary={user.email}
            slotProps={{
              primary: { noWrap: true, fontWeight: 600 },
              secondary: { noWrap: true },
            }}
          />
        </MenuItem>
        <Divider />
        <MenuItem onClick={signOut} data-testid="sign-out">
          <ListItemIcon>
            <LogoutIcon fontSize="small" />
          </ListItemIcon>
          <ListItemText primary="Sign out" />
        </MenuItem>
      </Menu>
    </>
  );
}
