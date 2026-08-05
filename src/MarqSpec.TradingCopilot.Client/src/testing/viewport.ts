import { vi } from 'vitest';

import { windowSizeClassMinWidth, type WindowSizeClass } from '../theme/tokens';

/**
 * jsdom ships no `matchMedia`, so every adaptive component would otherwise render at its "no media
 * queries matched" fallback and the breakpoint behaviour would go untested -- exactly the thing the
 * adaptive-navigation requirement is about.
 *
 * This installs a `matchMedia` that answers min-width / max-width queries against a pretended viewport
 * width, which is all `useWindowSizeClass` asks of it.
 */

const MIN_WIDTH = /\(\s*min-width:\s*(\d+(?:\.\d+)?)px\s*\)/;
const MAX_WIDTH = /\(\s*max-width:\s*(\d+(?:\.\d+)?)px\s*\)/;

function queryMatches(query: string, width: number): boolean {
  const min = MIN_WIDTH.exec(query);
  if (min !== null && width < Number(min[1])) return false;

  const max = MAX_WIDTH.exec(query);
  if (max !== null && width > Number(max[1])) return false;

  // A query mentioning neither bound is not something this shell asks; answering false keeps an
  // unexpected query from quietly reading as "matched".
  return min !== null || max !== null;
}

function mediaQueryList(query: string, width: number): MediaQueryList {
  return {
    matches: queryMatches(query, width),
    media: query,
    onchange: null,
    // The width never changes within a test -- each case renders at one size -- so the listeners are
    // accepted and never fired rather than being wired to a fake event source that could not emit.
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  };
}

/** Pretends the viewport is `width` px wide for every subsequent `matchMedia` call. */
export function setViewportWidth(width: number): void {
  vi.stubGlobal('matchMedia', (query: string) => mediaQueryList(query, width));
}

/**
 * A representative width inside each Material 3 window size class -- 20px past the boundary, so a test
 * that passes is testing the class and not the edge case.
 */
export const WINDOW_SIZE_CLASS_WIDTHS: Readonly<Record<WindowSizeClass, number>> = {
  compact: windowSizeClassMinWidth.medium - 200,
  medium: windowSizeClassMinWidth.medium + 20,
  expanded: windowSizeClassMinWidth.expanded + 20,
};

/** Every window size class, for tests that must prove behaviour holds at all three. */
export const ALL_WINDOW_SIZE_CLASSES: readonly WindowSizeClass[] = [
  'compact',
  'medium',
  'expanded',
];

/** Pretends the viewport sits inside the given Material 3 window size class. */
export function setWindowSizeClass(sizeClass: WindowSizeClass): void {
  setViewportWidth(WINDOW_SIZE_CLASS_WIDTHS[sizeClass]);
}
