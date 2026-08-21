import { describe, expect, it } from 'vitest';

import { DETACHABLE_PANELS, panelById } from './panelRegistry';

describe('panelRegistry', () => {
  it('offers the standalone panels — the suggestions feed, the live blotter, and the chart (gh#1015)', () => {
    expect(DETACHABLE_PANELS.map((panel) => panel.id)).toEqual(['suggestions', 'blotter', 'chart']);
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

  it('offers the chart as a detachable panel now the linked-instrument channel has landed (gh#1015)', () => {
    const chart = panelById('chart');
    expect(chart?.id).toBe('chart');
    expect(chart?.title).toBe('Chart');
    expect(typeof chart?.Panel).toBe('function');
  });

  it('returns undefined for an unknown or missing id — a mistyped or stale pop-out URL', () => {
    expect(panelById('does-not-exist')).toBeUndefined();
    expect(panelById(undefined)).toBeUndefined();
  });
});
