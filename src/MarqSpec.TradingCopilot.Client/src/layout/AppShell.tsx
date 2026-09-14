import AppBar from '@mui/material/AppBar';
import Box from '@mui/material/Box';
import Toolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';
import { Outlet, useLocation } from 'react-router';

import { ErrorBoundary } from '../components/ErrorBoundary';
import { ConnectionStatus } from '../health/ConnectionStatus';
import { AccountSwitcher } from '../accounts/AccountSwitcher';
import { AdaptiveNavigation, SecondaryDestinationLinks } from '../navigation/AdaptiveNavigation';
import { KillSwitchControl } from '../safety/KillSwitchControl';
import { ProtectionStatus } from '../safety/ProtectionStatus';
import { TimeToFlat } from '../safety/TimeToFlat';
import { AccountMenu } from './AccountMenu';
import { SafetyRegion } from './SafetyRegion';
import { ThemeModeToggle } from './ThemeModeToggle';
import { useWindowSizeClass } from './useWindowSizeClass';

/** The scrolling content region. Named so a test can prove what is -- and is not -- inside it. */
export const CONTENT_REGION_TEST_ID = 'app-content';

/**
 * The application shell: app bar, adaptive navigation, the persistent safety region, and the routed
 * surface.
 *
 * The layout is a viewport-height flex column that does **not** scroll. Only the content region does.
 * That single decision is what makes the safety region unscrollable -- it is off the scrolling axis
 * rather than pinned to it, so there is no scroll position, no rubber-banding and no mobile URL-bar
 * behaviour that can slide the kill switch out of view.
 */
export function AppShell() {
  const sizeClass = useWindowSizeClass();
  const location = useLocation();
  const compact = sizeClass === 'compact';

  return (
    <Box
      data-testid="app-shell"
      data-size-class={sizeClass}
      sx={{
        // dvh, not vh: on a phone the dynamic viewport unit tracks the browser chrome collapsing, where
        // 100vh would leave the shell taller than the screen and push the bottom bar under it.
        height: '100dvh',
        display: 'flex',
        flexDirection: 'column',
        overflow: 'hidden',
        bgcolor: 'background.default',
      }}
    >
      <AppBar
        position="static"
        elevation={0}
        sx={{
          bgcolor: 'background.surface3',
          color: 'text.primary',
          borderBottom: '1px solid',
          borderColor: 'divider',
          flex: 'none',
        }}
      >
        <Toolbar variant="dense" disableGutters sx={{ gap: 1.25, px: 1.5, minHeight: 52 }}>
          <Typography
            variant="subtitle1"
            component="h1"
            noWrap
            sx={{ fontWeight: 700, fontSize: 14.5 }}
          >
            Trading Co-Pilot
          </Typography>

          {/* R-14. Beside the product name because "which account is this" is the first question any
              control in this bar begs. */}
          <AccountSwitcher />

          <Box sx={{ flex: 1, minWidth: 8 }} />

          {/* Everything from here right is status and safety. The safety region is last-but-one so it
              lands in the same place at every size class -- muscle memory is a safety property. */}
          {!compact ? <ConnectionStatus /> : null}
          <SafetyRegion
            dense={compact}
            protection={<ProtectionStatus />}
            timeToFlat={<TimeToFlat />}
            killSwitch={<KillSwitchControl />}
          />
          {compact ? <SecondaryDestinationLinks /> : null}
          <ThemeModeToggle />
          <AccountMenu />
        </Toolbar>
      </AppBar>

      <Box sx={{ flex: 1, display: 'flex', minHeight: 0 }}>
        {!compact ? <AdaptiveNavigation sizeClass={sizeClass} /> : null}

        <Box
          component="main"
          data-testid={CONTENT_REGION_TEST_ID}
          sx={{ flex: 1, minWidth: 0, overflow: 'auto' }}
        >
          {/* Scoped to the routed surface on purpose: a surface that throws must not be able to take
              the app bar -- and the safety controls in it -- down with it. `location.key` clears a
              caught error when the operator navigates away. */}
          <ErrorBoundary resetKey={location.key}>
            <Outlet />
          </ErrorBoundary>
        </Box>
      </Box>

      {compact ? <AdaptiveNavigation sizeClass={sizeClass} /> : null}
    </Box>
  );
}
