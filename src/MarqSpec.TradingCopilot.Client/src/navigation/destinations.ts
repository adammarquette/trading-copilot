import CandlestickChartIcon from '@mui/icons-material/CandlestickChart';
import ForumIcon from '@mui/icons-material/Forum';
import type { SvgIconComponent } from '@mui/icons-material';
import MenuBookIcon from '@mui/icons-material/MenuBook';
import NewspaperIcon from '@mui/icons-material/Newspaper';
import RuleIcon from '@mui/icons-material/Rule';
import SettingsIcon from '@mui/icons-material/Settings';
import type { ComponentType } from 'react';

import { ChatSurface } from '../chat/ChatSurface';
import { JournalSurface } from '../journal/JournalSurface';
import { NewsSurface } from '../news/NewsSurface';
import { RulebookSurface } from '../triggers/RulebookSurface';
import { SettingsSurface } from '../settings/SettingsSurface';
import { WorkspaceSurface } from '../workspace/WorkspaceSurface';

/**
 * Primary destinations are the ones the navigation offers directly at every window size class.
 * Secondary ones sit below the rail's spacer on medium/expanded (as the wireframe draws Settings) and
 * move into the app bar on compact, because Material caps a bottom navigation bar at five destinations
 * and pushing a sixth in makes every target smaller than a thumb.
 */
export type DestinationTier = 'primary' | 'secondary';

/**
 * What a destination renders. Every surface takes its own destination so it can carry the shell's
 * `data-surface` contract without a second copy of the id.
 */
export type SurfaceComponent = ComponentType<{ readonly destination: Destination }>;

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
  /**
   * The built surface, once its card lands. Left off, the route renders the placeholder — so the one table still
   * guarantees a destination has a route and a route has a destination, and "built" versus "not built yet" is a
   * single field rather than a second list to keep in step.
   */
  readonly Surface?: SurfaceComponent;
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
    // The chart-central workspace (gh#725): the candlestick chart (gh#725) beside the suggestion panel (gh#654).
    // The order ticket, live blotter and chart overlays are still their own cards (gh#655/#656/#727).
    Surface: WorkspaceSurface,
  },
  {
    id: 'chat',
    path: '/chat',
    label: 'Chat',
    summary: 'The co-pilot conversation -- ask about a setup, a rule, or a day just traded.',
    requirement: 'R-6',
    Icon: ForumIcon,
    tier: 'primary',
    // The conversation list, thread and composer (gh#1063, increment 8 of gh#18): consumes the merged
    // persistence / CRUD / grounded-turn / retrieval / rerank stack (gh#898/#901/#906/#925/#930/#975/#995), which
    // was fully built but unreachable until this surface existed. Tool-call cards, citations and drafted-ticket
    // actions from the wireframe's [A4] design stay unbuilt -- the API returns plain turn text, nothing structured
    // for them yet.
    Surface: ChatSurface,
  },
  {
    id: 'journal',
    path: '/journal',
    label: 'Journal',
    summary: 'Realized P&L by day, the equity curve, and per-trade feedback.',
    requirement: 'R-8',
    Icon: MenuBookIcon,
    tier: 'primary',
    // P&L by day, drillable into a day (gh#659): the stat strip, equity curve and magnitude-shaded calendar
    // over `GET /accounts/{id}/journal/daily` (gh#1062), the day's trades over its `/{date}` sibling, and per
    // trade the taken-vs-suggested delta (gh#549) plus operator feedback (gh#1064). Practice/live and
    // instrument FILTERS, the shadow-P&L overlay, a strategy label, an R-multiple and a day-level
    // awaiting-review count all want reads that do not exist yet -- named in the PR, not faked here.
    Surface: JournalSurface,
  },
  {
    id: 'rulebook',
    path: '/rulebook',
    label: 'Rulebook',
    summary: 'Plain-language rules and the deterministic triggers they compile to.',
    requirement: 'R-7',
    Icon: RuleIcon,
    tier: 'primary',
    Surface: RulebookSurface,
  },
  {
    id: 'news',
    path: '/news',
    label: 'News',
    summary: 'The relevance-ranked feed and its per-instrument topic mapping.',
    requirement: 'R-2',
    Icon: NewspaperIcon,
    tier: 'primary',
    // The relevance-config half is built (gh#742): the ticker↔instrument maps, with topics and the feed itself
    // still their own increments, so the surface holds what exists and does not mock up what does not.
    Surface: NewsSurface,
  },
  {
    id: 'settings',
    path: '/settings',
    label: 'Settings',
    summary: 'Risk tolerance, auto-flatten times, entry defaults, accounts and layout.',
    requirement: 'R-5 · R-14',
    Icon: SettingsIcon,
    tier: 'secondary',
    // The R-5 risk profile + headroom half is built (gh#658); AI spend and news relevance are their own increments,
    // so the surface holds what exists and does not mock up what does not.
    Surface: SettingsSurface,
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
