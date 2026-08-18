import AppBar from '@mui/material/AppBar';
import Box from '@mui/material/Box';
import Toolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';

import { ErrorBoundary } from '../components/ErrorBoundary';
import { ConnectionStatus } from '../health/ConnectionStatus';
import { SafetyRegion } from '../layout/SafetyRegion';
import { KillSwitchControl } from '../safety/KillSwitchControl';
import { ProtectionStatus } from '../safety/ProtectionStatus';
import { TimeToFlat } from '../safety/TimeToFlat';

export interface DetachedPanelFrameProps {
  /** The window bar's title — the panel's name. */
  readonly title: string;
  /** The panel itself. */
  readonly children: ReactNode;
}

/**
 * The chrome around a detached panel (gh#651, ADR-0006). Deliberately **leaner** than the full {@link AppShell} —
 * no navigation rail, because a single-panel pop-out is not a place you navigate from — but it carries the one
 * thing ADR-0005/0006 require in **every** window: the safety strip. The kill switch and the time-to-flat
 * countdown are here for the same reason they are in the main shell, so an operator watching a detached panel on a
 * second monitor is never without the two controls that stop trading (R-13). Same non-scrolling, viewport-height
 * column as the shell, so the strip is off the scrolling axis and cannot be scrolled away. This window's own
 * `ConnectionStatus` sits beside it — each window is its own SignalR client, and reads its own link's health.
 */
export function DetachedPanelFrame({
  title,
  children,
}: DetachedPanelFrameProps): React.JSX.Element {
  return (
    <Box
      data-testid="detached-panel-frame"
      sx={{
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
            {title}
          </Typography>

          <Box sx={{ flex: 1, minWidth: 8 }} />

          {/* This window's own link health, then the safety strip — replicated to every window (ADR-0006). */}
          <ConnectionStatus />
          <SafetyRegion
            protection={<ProtectionStatus />}
            timeToFlat={<TimeToFlat />}
            killSwitch={<KillSwitchControl />}
          />
        </Toolbar>
      </AppBar>

      <Box component="main" sx={{ flex: 1, minWidth: 0, overflow: 'auto' }}>
        {/* A panel that throws must not take the safety strip in the bar down with it (as in the main shell). */}
        <ErrorBoundary>{children}</ErrorBoundary>
      </Box>
    </Box>
  );
}
