import type { ComponentType } from 'react';

import { SuggestionsPanel } from '../suggestions/SuggestionsPanel';
import { DetachedBlotter } from './DetachedBlotter';

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
 * The panels that can stand alone in a pop-out today (gh#651 increment 1): the suggestion feed and the live
 * blotter, both self-contained given the authenticated realtime + account providers. The chart (which needs the
 * cross-window linked-instrument channel) and chat (not yet built) join the registry as they land.
 */
export const DETACHABLE_PANELS: readonly DetachablePanel[] = [
  { id: 'suggestions', title: 'Suggestions', Panel: SuggestionsPanel },
  { id: 'blotter', title: 'Positions & orders', Panel: DetachedBlotter },
];

/** The detachable panel for a route token, or `undefined` for an unknown id (a mistyped or stale pop-out URL). */
export function panelById(id: string | undefined): DetachablePanel | undefined {
  return DETACHABLE_PANELS.find((panel) => panel.id === id);
}
