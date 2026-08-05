import CandlestickChartIcon from '@mui/icons-material/CandlestickChart';
import ForumIcon from '@mui/icons-material/Forum';
import type { SvgIconComponent } from '@mui/icons-material';
import MenuBookIcon from '@mui/icons-material/MenuBook';
import NewspaperIcon from '@mui/icons-material/Newspaper';
import RuleIcon from '@mui/icons-material/Rule';
import SettingsIcon from '@mui/icons-material/Settings';

/**
 * Primary destinations are the ones the navigation offers directly at every window size class.
 * Secondary ones sit below the rail's spacer on medium/expanded (as the wireframe draws Settings) and
 * move into the app bar on compact, because Material caps a bottom navigation bar at five destinations
 * and pushing a sixth in makes every target smaller than a thumb.
 */
export type DestinationTier = 'primary' | 'secondary';

export interface Destination {
  /** Stable key; also the test handle. Never derived from the label, which is copy and will change. */
  readonly id: string;
  /** Route path. `/` is the workspace -- the chart is the central surface (R-10, ADR-0004). */
  readonly path: string;
  readonly label: string;
  /** What the surface will hold once its own card lands. Shown in the placeholder's empty state. */
  readonly summary: string;
  /** The PRD requirement this surface serves, so a placeholder still traces to something. */
  readonly requirement: string;
  readonly Icon: SvgIconComponent;
  readonly tier: DestinationTier;
}

/**
 * The single navigation table. Routes, the rail, the bottom bar and the app-bar overflow all read it,
 * so a destination cannot exist in the navigation without a route behind it (or the reverse) -- the
 * classic way a shell grows a dead tab.
 */
export const destinations: readonly Destination[] = [
  {
    id: 'workspace',
    path: '/',
    label: 'Workspace',
    summary: 'The chart-central trading workspace: suggestions, order ticket, positions and fills.',
    requirement: 'R-10',
    Icon: CandlestickChartIcon,
    tier: 'primary',
  },
  {
    id: 'chat',
    path: '/chat',
    label: 'Chat',
    summary: 'The co-pilot conversation -- ask about a setup, a rule, or a day just traded.',
    requirement: 'R-6',
    Icon: ForumIcon,
    tier: 'primary',
  },
  {
    id: 'journal',
    path: '/journal',
    label: 'Journal',
    summary: 'Realized P&L by day, the equity curve, and per-trade feedback.',
    requirement: 'R-8',
    Icon: MenuBookIcon,
    tier: 'primary',
  },
  {
    id: 'rulebook',
    path: '/rulebook',
    label: 'Rulebook',
    summary: 'Plain-language rules and the deterministic triggers they compile to.',
    requirement: 'R-7',
    Icon: RuleIcon,
    tier: 'primary',
  },
  {
    id: 'news',
    path: '/news',
    label: 'News',
    summary: 'The relevance-ranked feed and its per-instrument topic mapping.',
    requirement: 'R-2',
    Icon: NewspaperIcon,
    tier: 'primary',
  },
  {
    id: 'settings',
    path: '/settings',
    label: 'Settings',
    summary: 'Risk tolerance, auto-flatten times, entry defaults, accounts and layout.',
    requirement: 'R-5 · R-14',
    Icon: SettingsIcon,
    tier: 'secondary',
  },
];

export const primaryDestinations: readonly Destination[] = destinations.filter(
  (destination) => destination.tier === 'primary',
);

export const secondaryDestinations: readonly Destination[] = destinations.filter(
  (destination) => destination.tier === 'secondary',
);

/**
 * Whether a destination is the one currently shown.
 *
 * The workspace lives at `/`, which is a prefix of every other path, so prefix matching alone would
 * light up two rail items at once. Exact for the root, prefix (on a segment boundary) for the rest, so
 * a future `/journal/2026-08-05` still highlights Journal.
 */
export function isDestinationActive(pathname: string, destination: Destination): boolean {
  if (destination.path === '/') return pathname === '/';
  return pathname === destination.path || pathname.startsWith(`${destination.path}/`);
}
