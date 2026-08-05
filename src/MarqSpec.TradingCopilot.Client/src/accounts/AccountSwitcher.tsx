import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown';
import Box from '@mui/material/Box';
import ButtonBase from '@mui/material/ButtonBase';
import Divider from '@mui/material/Divider';
import ListItemText from '@mui/material/ListItemText';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Typography from '@mui/material/Typography';
import { useState } from 'react';

import { TradingMode } from '../api/accounts';
import { ModeChipSlot } from '../layout/ModeChipSlot';
import { useOptionalAccounts } from './AccountProvider';
import { ModeChip } from './ModeChip';

/**
 * The app-bar account control (gh#653, R-14): which account every surface is scoped to, and its resolved mode,
 * always on screen in the reserved mode-chip slot. Switching re-scopes the app because every surface reads the
 * active account from the same context this drives.
 *
 * It reads the context *optionally*: the shell is mounted without an {@link AccountProvider} in some layout
 * tests, and before the roster resolves there is no account to name — both render the **reserved empty slot**
 * rather than a guess, because an unknown mode is not a neutral state to paper over.
 */
export function AccountSwitcher() {
  const context = useOptionalAccounts();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);

  if (!context || context.status !== 'ready') {
    return <ModeChipSlot />;
  }

  const { accounts, activeAccount, setActiveAccount } = context;
  const undeclared = activeAccount.mode === TradingMode.Undeclared;
  // Active accounts only (the wireframe's title), but never hide the one currently selected.
  const switchable = accounts.filter(
    (account) => account.isVisible || account.id === activeAccount.id,
  );

  function choose(id: string) {
    setActiveAccount(id);
    setAnchor(null);
  }

  return (
    <>
      <ModeChipSlot>
        <ButtonBase
          onClick={(event) => setAnchor(event.currentTarget)}
          aria-haspopup="menu"
          aria-expanded={anchor !== null}
          data-testid="account-switcher"
          sx={{ display: 'flex', alignItems: 'center', gap: 0.75, borderRadius: 0.75, px: 0.25 }}
        >
          <Typography variant="caption" noWrap sx={{ fontWeight: 700, maxWidth: 120 }}>
            {activeAccount.name}
          </Typography>
          <ModeChip mode={activeAccount.mode} />
          <KeyboardArrowDownIcon sx={{ fontSize: 16, color: 'text.secondary' }} />
        </ButtonBase>
      </ModeChipSlot>

      <Menu
        anchorEl={anchor}
        open={anchor !== null}
        onClose={() => setAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
        slotProps={{ list: { dense: true, sx: { minWidth: 260 } } }}
      >
        <Typography variant="overline" sx={{ px: 2, color: 'text.secondary' }}>
          Trading account
        </Typography>
        {switchable.map((account) => (
          <MenuItem
            key={account.id}
            selected={account.id === activeAccount.id}
            onClick={() => choose(account.id)}
            data-testid="account-option"
          >
            <ListItemText primary={account.name} slotProps={{ primary: { noWrap: true } }} />
            <Box sx={{ ml: 2 }}>
              <ModeChip mode={account.mode} />
            </Box>
          </MenuItem>
        ))}
        {undeclared ? (
          <Box>
            <Divider />
            <Box sx={{ px: 2, py: 1, maxWidth: 300 }} data-testid="undeclared-block">
              <Typography variant="body2" sx={{ fontWeight: 700, color: 'error.main' }}>
                Undeclared — tradeable nowhere
              </Typography>
              <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                This account's firm has not declared what its stage means, so no order will be
                accepted for it. Declare this stage's convention for the firm to trade it.
              </Typography>
            </Box>
          </Box>
        ) : null}
      </Menu>
    </>
  );
}
