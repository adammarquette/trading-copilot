import { useSyncExternalStore } from 'react';

/**
 * The app's one ticking clock.
 *
 * Anything that counts *down* — a suggestion's validity window (R-4), and the time-to-flat countdown that will
 * live in the safety region (R-13) — reads the wall clock here rather than starting its own interval. Two reasons,
 * and the second is the one that matters:
 *
 *  1. Six cards on screen would otherwise be six intervals, each waking the tab on its own phase.
 *  2. **They would disagree.** Two independent intervals drift apart, so two cards expiring at the same instant
 *     would show different seconds remaining, and one would visibly die before the other. A single source makes
 *     every countdown on the screen consistent by construction.
 *
 * The interval is created on the first subscriber and torn down after the last, so an idle app runs no timer.
 */

type Listener = () => void;

/**
 * 500ms, not 1000ms. The display has one-second resolution, so a one-second tick can land up to a full second
 * after the boundary it is meant to show — leaving a card reading `0:01` for nearly two seconds and, worse,
 * reading "still valid" after it is not. Sampling at twice the display rate bounds that lag at half a second.
 */
export const TICK_MS = 500;

const listeners = new Set<Listener>();
let handle: ReturnType<typeof setInterval> | null = null;

// The snapshot MUST be a cached value rather than a fresh `Date.now()`: useSyncExternalStore compares snapshots
// by identity on every render, and a getter that changes on each call is an infinite render loop.
let currentNow = Date.now();

function subscribe(listener: Listener): () => void {
  listeners.add(listener);
  if (handle === null) {
    // Re-read on start: the cached value is arbitrarily stale if the app has been idle with no countdown mounted.
    currentNow = Date.now();
    handle = setInterval(() => {
      currentNow = Date.now();
      for (const notify of listeners) {
        notify();
      }
    }, TICK_MS);
  }

  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && handle !== null) {
      clearInterval(handle);
      handle = null;
    }
  };
}

function snapshot(): number {
  return currentNow;
}

/** The current wall-clock time in epoch milliseconds, re-rendering the caller every {@link TICK_MS}. */
export function useNow(): number {
  // The third argument is the server snapshot. There is no SSR here, but the parameter is required and passing
  // the same getter keeps hydration honest if this bundle is ever pre-rendered.
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}
