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

  it('reads nothing detached — never throws — on a corrupt, primitive, or non-array value', () => {
    // Fail toward the docked default: a garbled localStorage value must not strand the workspace.
    localStorage.setItem('tc.detached-panels', 'not json');
    expect(loadDetachedPanels().size).toBe(0);

    localStorage.setItem('tc.detached-panels', '{"blotter":true}');
    expect(loadDetachedPanels().size).toBe(0);

    localStorage.setItem('tc.detached-panels', '42'); // a bare primitive is valid JSON but not an array
    expect(loadDetachedPanels().size).toBe(0);
  });

  it('drops unknown / stale ids and non-string elements on LOAD — the guard against an OLDER build', () => {
    // Seed storage DIRECTLY, bypassing save's own filter, so the load-side known-id + typeof guard is what runs —
    // the one that protects against an id (or garbage) written by a build that no longer knows the panel.
    localStorage.setItem(
      'tc.detached-panels',
      JSON.stringify(['blotter', 'ghost-panel', 42, null, 'suggestions']),
    );

    expect([...loadDetachedPanels()].sort()).toEqual(['blotter', 'suggestions']);
  });
});
