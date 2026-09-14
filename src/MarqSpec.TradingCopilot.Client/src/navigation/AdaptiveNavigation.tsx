import BottomNavigation from '@mui/material/BottomNavigation';
import BottomNavigationAction from '@mui/material/BottomNavigationAction';
import Box from '@mui/material/Box';
import IconButton from '@mui/material/IconButton';
import Paper from '@mui/material/Paper';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import { Link, useLocation } from 'react-router';

import type { WindowSizeClass } from '../theme/tokens';
import {
  isDestinationActive,
  primaryDestinations,
  secondaryDestinations,
  type Destination,
} from './destinations';

/**
 * The navigation form for each Material 3 window size class, per ADR-0005: bottom navigation bar on
 * compact, icon navigation rail on medium, labelled navigation rail on expanded.
 *
 * Exposed as a data attribute rather than inferred from the DOM shape so a test can assert the *form*
 * directly -- "which navigation is this" is the requirement, and a test that instead counted list items
 * or measured a width would pass while the wrong component rendered.
 */
export const NAVIGATION_FORM: Readonly<Record<WindowSizeClass, string>> = {
  compact: 'bottom-bar',
  medium: 'rail-icon',
  expanded: 'rail-labelled',
};

/** One accessible name for the navigation landmark, so tests and screen readers agree across forms. */
const NAVIGATION_LABEL = 'Primary';

interface RailItemProps {
  readonly destination: Destination;
  readonly active: boolean;
  readonly labelled: boolean;
}

function RailItem({ destination, active, labelled }: RailItemProps) {
  const { Icon, label, path } = destination;

  const item = (
    <Box
      component={Link}
      to={path}
      aria-label={label}
      aria-current={active ? 'page' : undefined}
      data-destination={destination.id}
      sx={{
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        gap: 0.5,
        px: 0.5,
        py: 1.25,
        borderRadius: 1.5,
        textDecoration: 'none',
        color: active ? 'primary.main' : 'text.disabled',
        '&:hover': { color: active ? 'primary.main' : 'text.secondary' },
      }}
    >
      <Box
        sx={{
          width: 34,
          height: 26,
          borderRadius: '14px',
          display: 'grid',
          placeItems: 'center',
          // `primary.light` carries the wireframe's `--primary-ctr` (see createAppTheme) -- the tonal
          // container behind the selected item, not a lightened accent.
          bgcolor: active ? 'primary.light' : 'transparent',
        }}
      >
        <Icon fontSize="small" />
      </Box>
      {labelled ? (
        <Typography variant="caption" sx={{ fontSize: 10.5, textAlign: 'center', lineHeight: 1.2 }}>
          {label}
        </Typography>
      ) : null}
    </Box>
  );

  // On the icon rail the label exists only as an accessible name, so a pointer user needs the tooltip
  // to read it. On the labelled rail the text is already on screen and a tooltip would just repeat it.
  return labelled ? (
    item
  ) : (
    <Tooltip title={label} placement="right">
      {item}
    </Tooltip>
  );
}

interface NavigationRailProps {
  readonly labelled: boolean;
  readonly pathname: string;
}

function NavigationRail({ labelled, pathname }: NavigationRailProps) {
  return (
    <Box
      component="nav"
      aria-label={NAVIGATION_LABEL}
      data-nav-form={labelled ? NAVIGATION_FORM.expanded : NAVIGATION_FORM.medium}
      sx={{
        width: labelled ? 96 : 60,
        flex: 'none',
        display: 'flex',
        flexDirection: 'column',
        gap: '2px',
        px: 0.75,
        py: 1.25,
        bgcolor: 'background.surface2',
        borderRight: '1px solid',
        borderColor: 'divider',
        overflowY: 'auto',
      }}
    >
      {primaryDestinations.map((destination) => (
        <RailItem
          key={destination.id}
          destination={destination}
          active={isDestinationActive(pathname, destination)}
          labelled={labelled}
        />
      ))}

      {/* The wireframe's rail spacer: secondary destinations sit at the foot of the rail, away from the
          five the operator reaches for all day. */}
      <Box sx={{ flex: 1, minHeight: 8 }} />

      {secondaryDestinations.map((destination) => (
        <RailItem
          key={destination.id}
          destination={destination}
          active={isDestinationActive(pathname, destination)}
          labelled={labelled}
        />
      ))}
    </Box>
  );
}

function BottomNavigationBar({ pathname }: { readonly pathname: string }) {
  const active = primaryDestinations.find((destination) =>
    isDestinationActive(pathname, destination),
  );

  return (
    <Paper
      component="nav"
      aria-label={NAVIGATION_LABEL}
      data-nav-form={NAVIGATION_FORM.compact}
      square
      elevation={3}
      sx={{
        flex: 'none',
        bgcolor: 'background.surface3',
        borderTop: '1px solid',
        borderColor: 'divider',
      }}
    >
      <BottomNavigation
        showLabels
        // `false` rather than undefined when nothing matches (a deep link outside the table): MUI treats
        // an unmatched value as "no selection", which is honest, where undefined makes it uncontrolled
        // and lets the bar highlight whatever was tapped last.
        value={active?.path ?? false}
        sx={{ bgcolor: 'transparent' }}
      >
        {primaryDestinations.map((destination) => (
          <BottomNavigationAction
            key={destination.id}
            component={Link}
            to={destination.path}
            value={destination.path}
            label={destination.label}
            aria-label={destination.label}
            aria-current={isDestinationActive(pathname, destination) ? 'page' : undefined}
            data-destination={destination.id}
            icon={<destination.Icon fontSize="small" />}
          />
        ))}
      </BottomNavigation>
    </Paper>
  );
}

/**
 * The compact-only home for the secondary destinations.
 *
 * They cannot go in the bottom bar (Material's five-destination cap) and there is no rail to hang them
 * off, so they become app-bar icon links -- which is also what the wireframe's phone frame draws. Every
 * destination stays reachable at every window size class; only the form changes.
 */
export function SecondaryDestinationLinks() {
  const { pathname } = useLocation();

  return (
    <Box
      component="nav"
      aria-label="Secondary"
      data-nav-form="app-bar-overflow"
      sx={{ display: 'flex', alignItems: 'center' }}
    >
      {secondaryDestinations.map((destination) => {
        const active = isDestinationActive(pathname, destination);

        return (
          <Tooltip key={destination.id} title={destination.label}>
            <IconButton
              component={Link}
              to={destination.path}
              size="small"
              aria-label={destination.label}
              aria-current={active ? 'page' : undefined}
              data-destination={destination.id}
              sx={{ color: active ? 'primary.main' : 'text.secondary' }}
            >
              <destination.Icon fontSize="small" />
            </IconButton>
          </Tooltip>
        );
      })}
    </Box>
  );
}

export interface AdaptiveNavigationProps {
  readonly sizeClass: WindowSizeClass;
}

/**
 * Renders whichever navigation form the current window size class calls for. The shell decides where it
 * sits (beside the content for a rail, below it for the bar); this decides what it is.
 */
export function AdaptiveNavigation({ sizeClass }: AdaptiveNavigationProps) {
  const { pathname } = useLocation();

  if (sizeClass === 'compact') return <BottomNavigationBar pathname={pathname} />;

  return <NavigationRail labelled={sizeClass === 'expanded'} pathname={pathname} />;
}
