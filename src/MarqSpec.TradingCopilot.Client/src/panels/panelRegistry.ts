import type { ComponentType } from 'react';

import { SuggestionsPanel } from '../suggestions/SuggestionsPanel';
import { DetachedBlotter } from './DetachedBlotter';
import { DetachedChart } from './DetachedChart';

/**
 * A workspace panel that can be detached into its own window (gh#651, ADR-0006). `id` is the route token
 * ({@link detachPanel} / {@link DetachedPanelPage}); `title` names the window's bar; `Panel` renders standalone,
 * sourcing whatever it needs (an account, the realtime connection) from the providers the detached route carries.
 */
export interface DetachablePanel {
  readonly id: string;
  readonly title: string;
  readonly Panel: ComponentType;
}

/**
 * The panels that can stand alone in a pop-out: the suggestion feed and the live blotter (gh#651 increment 1) and now
 * the **chart** (gh#1015) — all self-contained given the authenticated realtime + account providers the detached
 * route carries. The chart joined once the cross-window **linked-instrument channel** landed
 * ({@link ../chart/ChartWorkspace} + {@link DetachedChart}): a detached chart follows the instrument the docked
 * window selects, neither authoritative. Only **chat** (gh#18, not yet built) remains to join as it lands.
 */
export const DETACHABLE_PANELS: readonly DetachablePanel[] = [
  { id: 'suggestions', title: 'Suggestions', Panel: SuggestionsPanel },
  { id: 'blotter', title: 'Positions & orders', Panel: DetachedBlotter },
  { id: 'chart', title: 'Chart', Panel: DetachedChart },
];

/** The detachable panel for a route token, or `undefined` for an unknown id (a mistyped or stale pop-out URL). */
export function panelById(id: string | undefined): DetachablePanel | undefined {
  return DETACHABLE_PANELS.find((panel) => panel.id === id);
}
