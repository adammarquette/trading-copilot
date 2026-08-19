import { panelById } from './panelRegistry';

/** Where the set of detached panel ids persists, so a reload restores the operator's last pop-out layout (gh#651). */
const STORAGE_KEY = 'tc.detached-panels';

/**
 * The detached-panel layout, persisted to `localStorage` (gh#651, ADR-0006). Only **known** panel ids are ever
 * stored or returned — a stale or renamed id from an older build is dropped rather than resurrecting a panel that no
 * longer exists. A corrupt or missing value reads as "nothing detached" (fail toward the docked default), never a
 * throw: a garbled layout must not strand the workspace.
 */
export function loadDetachedPanels(): ReadonlySet<string> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw === null) {
      return new Set();
    }
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return new Set();
    }
    return new Set(
      parsed.filter((id): id is string => typeof id === 'string' && panelById(id) !== undefined),
    );
  } catch {
    return new Set();
  }
}

/** Persists the detached-panel set, storing only known panel ids (a stale id is dropped on the way out too). */
export function saveDetachedPanels(ids: ReadonlySet<string>): void {
  const known = [...ids].filter((id) => panelById(id) !== undefined);
  localStorage.setItem(STORAGE_KEY, JSON.stringify(known));
}
