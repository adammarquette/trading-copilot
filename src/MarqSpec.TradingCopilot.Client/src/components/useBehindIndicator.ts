import { useCallback, useState } from 'react';

export interface BehindIndicator {
  /**
   * True from the moment a background refresh fails until the next one succeeds. Never set by a FOREGROUND
   * load's failure — that owns its own error / loading screen (see the module note on {@link BehindMarker}).
   */
  readonly behind: boolean;
  /** Call from a background refresh's failure branch. */
  readonly markBehind: () => void;
  /** Call from a background (or foreground) refresh's success branch — the surface is current again. */
  readonly clearBehind: () => void;
}

/**
 * The shared "this read is behind, the socket is fine" affordance (gh#1109), generalising what gh#874 built
 * one-off for the suggestion panel (`SuggestionList`'s `staleRefresh` state) so a second surface does not have to
 * reinvent it, and a third does not answer the same question a third way (gh#1103's `ConversationThread`, which
 * shipped keeping the thread on a failed reconnect re-read but saying nothing about it — the honest half of R-19 /
 * ADR-0013's "never present stale data as live" that this closes).
 *
 * **What this hook owns, and what it deliberately does not.** It is only the boolean and its two setters — not the
 * fetch, not the retry, not the rendering. A surface that keeps its last good render on a failed BACKGROUND
 * refresh (never nuking a working view to an error screen, per ADR-0021's gh#760 rule) still needs to LOOK
 * degraded, or a stale read carries no operator-visible signal at all. The caller decides the loading/error
 * boundary itself, simply by calling `markBehind` / `clearBehind` only from its background-refresh path and never
 * from the path that owns the loading / error screens — {@link BehindMarker} is never rendered there either way.
 */
export function useBehindIndicator(): BehindIndicator {
  const [behind, setBehind] = useState(false);
  const markBehind = useCallback(() => setBehind(true), []);
  const clearBehind = useCallback(() => setBehind(false), []);
  return { behind, markBehind, clearBehind };
}
