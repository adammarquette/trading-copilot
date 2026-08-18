import { describe, expect, it } from 'vitest';

import { DETACHABLE_PANELS, panelById } from './panelRegistry';

describe('panelRegistry', () => {
  it('offers the standalone panels increment 1 ships — the suggestions feed and the live blotter', () => {
    expect(DETACHABLE_PANELS.map((panel) => panel.id)).toEqual(['suggestions', 'blotter']);
    // Each is a real, titled, renderable panel — the frame draws the title and mounts the component.
    expect(
      DETACHABLE_PANELS.every(
        (panel) => panel.title.length > 0 && typeof panel.Panel === 'function',
      ),
    ).toBe(true);
  });

  it('resolves a panel by its route id', () => {
    expect(panelById('blotter')?.title).toBe('Positions & orders');
  });

  it('returns undefined for an unknown or missing id — a mistyped or stale pop-out URL', () => {
    expect(panelById('does-not-exist')).toBeUndefined();
    expect(panelById(undefined)).toBeUndefined();
  });
});
