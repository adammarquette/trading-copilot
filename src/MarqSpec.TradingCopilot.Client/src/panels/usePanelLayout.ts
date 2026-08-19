import { useCallback, useEffect, useRef, useState } from 'react';

import { detachPanel } from './detachPanel';
import { loadDetachedPanels, saveDetachedPanels } from './panelLayout';

export interface PanelLayout {
  /** Whether the panel is currently popped out. */
  readonly isDetached: (panelId: string) => boolean;
  /** Pop the panel out into its own window and remember it (persisted). */
  readonly detach: (panelId: string) => void;
  /** Close the window this session opened for the panel and re-dock it (persisted). */
  readonly reattach: (panelId: string) => void;
}

/**
 * The operator's pop-out layout (gh#651, ADR-0006): which panels are detached, **persisted across reloads**. The
 * set is restored from `localStorage` on mount, so a reload reflects the last layout rather than silently re-docking
 * everything. `detach` opens the pop-out ({@link detachPanel}) and records it; `reattach` closes the window it
 * opened this session and re-docks. Mount this **once** at the workspace and pass its actions down — a per-button
 * copy would each hold a separate, diverging set.
 *
 * Cross-window limits are deliberate and left to a later increment (the `BroadcastChannel` link, ADR-0006): a
 * browser cannot re-open a window without a user gesture, so a reload shows the detached panels as detached with a
 * reattach affordance rather than auto-reopening them; and reattaching a window this tab did not open (a pop-out
 * that outlived a main-window reload) re-docks the layout but cannot itself close that orphaned window.
 */
export function usePanelLayout(): PanelLayout {
  const [detached, setDetached] = useState<ReadonlySet<string>>(loadDetachedPanels);
  /** The pop-out windows THIS session opened, so reattach can close the one it created. */
  const windows = useRef(new Map<string, Window>());

  // Persist as a side-effect of the set changing (not inside the updater, which StrictMode double-invokes). The
  // mount write is a harmless re-save of what was just loaded.
  useEffect(() => {
    saveDetachedPanels(detached);
  }, [detached]);

  const detach = useCallback((panelId: string) => {
    const handle = detachPanel(panelId);
    if (handle !== null) {
      windows.current.set(panelId, handle);
    }
    setDetached((current) => (current.has(panelId) ? current : new Set(current).add(panelId)));
  }, []);

  const reattach = useCallback((panelId: string) => {
    windows.current.get(panelId)?.close();
    windows.current.delete(panelId);
    setDetached((current) => {
      if (!current.has(panelId)) {
        return current;
      }
      const next = new Set(current);
      next.delete(panelId);
      return next;
    });
  }, []);

  const isDetached = useCallback((panelId: string) => detached.has(panelId), [detached]);

  return { isDetached, detach, reattach };
}
