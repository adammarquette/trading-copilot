import { afterEach, describe, expect, it } from 'vitest';

import { loadDetachedPanels, saveDetachedPanels } from './panelLayout';

afterEach(() => {
  localStorage.clear();
});

describe('panelLayout', () => {
  it('round-trips the detached-panel set so a reload restores the last layout (gh#651)', () => {
    saveDetachedPanels(new Set(['blotter', 'suggestions']));

    expect([...loadDetachedPanels()].sort()).toEqual(['blotter', 'suggestions']);
  });

  it('reads nothing detached when there is no stored layout', () => {
    expect(loadDetachedPanels().size).toBe(0);
  });

  it('reads nothing detached — never throws — on a corrupt or non-array value', () => {
    // Fail toward the docked default: a garbled localStorage value must not strand the workspace.
    localStorage.setItem('tc.detached-panels', 'not json');
    expect(loadDetachedPanels().size).toBe(0);

    localStorage.setItem('tc.detached-panels', '{"blotter":true}');
    expect(loadDetachedPanels().size).toBe(0);
  });

  it('drops an unknown / stale panel id rather than resurrecting a panel that no longer exists', () => {
    // A renamed or removed panel id from an older build must not linger in the layout.
    saveDetachedPanels(new Set(['blotter', 'ghost-panel']));

    const loaded = loadDetachedPanels();
    expect(loaded.has('blotter')).toBe(true);
    expect(loaded.has('ghost-panel')).toBe(false);
  });
});
