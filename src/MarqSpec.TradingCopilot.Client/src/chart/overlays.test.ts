import { describe, expect, it } from 'vitest';

import type { PriceLevel } from '../api/marketData';
import {
  levelToPriceLines,
  levelsToPriceLines,
  suggestionToPriceLines,
  suggestionsToPriceLines,
} from './overlays';

const PALETTE = { support: '#26a69a', resistance: '#ef5350' } as const;

function level(overrides: Partial<PriceLevel>): PriceLevel {
  return {
    timeframeMinutes: 5,
    top: 5305,
    bottom: 5304,
    kind: 'Support',
    significance: 0.5,
    formedAtBucket: '2026-01-01T00:00:00Z',
    touchCount: 1,
    ...overrides,
  };
}

describe('levelToPriceLines', () => {
  it('maps a band to its top and bottom edges, coloured by side, the top edge carrying the label', () => {
    const lines = levelToPriceLines(
      level({ kind: 'Resistance', top: 5310, bottom: 5308, timeframeMinutes: 60 }),
      PALETTE,
    );

    expect(lines).toHaveLength(2);
    expect(lines[0]).toMatchObject({ price: 5310, color: PALETTE.resistance, title: 'R 1h' });
    // The bottom edge shares the colour but stays unlabelled, so a band reads as one annotated zone, not two labels.
    expect(lines[1]).toMatchObject({ price: 5308, color: PALETTE.resistance, title: '' });
  });

  it('colours a support with the support colour and an S label', () => {
    const [top] = levelToPriceLines(level({ kind: 'Support', timeframeMinutes: 15 }), PALETTE);
    expect(top).toMatchObject({ color: PALETTE.support, title: 'S 15m' });
  });

  it('collapses a degenerate band (top === bottom) to a single line', () => {
    const lines = levelToPriceLines(level({ top: 5300, bottom: 5300 }), PALETTE);
    expect(lines).toHaveLength(1);
    expect(lines[0]).toMatchObject({ price: 5300 });
  });

  it('labels an unknown/other kind without throwing, defaulting to the resistance colour', () => {
    // The server serves Support/Resistance today; a future kind (role-reversal, gh#597) must still render.
    const [top] = levelToPriceLines(level({ kind: 'Breakout' }), PALETTE);
    expect(top.color).toBe(PALETTE.resistance);
  });
});

describe('levelsToPriceLines', () => {
  it('flattens every level to its price lines', () => {
    const lines = levelsToPriceLines(
      [level({ top: 10, bottom: 9 }), level({ top: 20, bottom: 20 })],
      PALETTE,
    );
    // 2 (a real band) + 1 (a degenerate band) = 3 lines.
    expect(lines.map((line) => line.price)).toEqual([10, 9, 20]);
  });

  it('is empty for no levels', () => {
    expect(levelsToPriceLines([], PALETTE)).toEqual([]);
  });
});

const SUGGESTION_PALETTE = { entry: '#4a90d9', stop: '#ef5350', target: '#26a69a' } as const;

describe('suggestionToPriceLines', () => {
  it('maps a suggestion zone to its entry, stop and target lines — each labelled and coloured by role', () => {
    const lines = suggestionToPriceLines(
      { id: 's1', entry: 5300, stop: 5290, target: 5320 },
      SUGGESTION_PALETTE,
    );

    // Role, not direction, drives the colour: the stop is always the risk side, the target always the reward side,
    // whether the setup is long or short — so a glance reads the geometry the same way every time.
    expect(lines).toEqual([
      { price: 5300, color: SUGGESTION_PALETTE.entry, title: 'Entry', style: 'solid' },
      { price: 5290, color: SUGGESTION_PALETTE.stop, title: 'Stop', style: 'dashed' },
      { price: 5320, color: SUGGESTION_PALETTE.target, title: 'Target', style: 'dashed' },
    ]);
  });
});

describe('suggestionsToPriceLines', () => {
  it('flattens every zone to its three lines', () => {
    const lines = suggestionsToPriceLines(
      [
        { id: 'a', entry: 10, stop: 9, target: 12 },
        { id: 'b', entry: 20, stop: 21, target: 18 },
      ],
      SUGGESTION_PALETTE,
    );
    expect(lines.map((line) => line.price)).toEqual([10, 9, 12, 20, 21, 18]);
  });

  it('leaves a lone zone unsuffixed — no ordinal noise when there is nothing to disambiguate', () => {
    const lines = suggestionsToPriceLines(
      [{ id: 'a', entry: 10, stop: 9, target: 12 }],
      SUGGESTION_PALETTE,
    );
    expect(lines.map((line) => line.title)).toEqual(['Entry', 'Stop', 'Target']);
  });

  it('disambiguates coexisting zones with a shared per-zone ordinal, so a stop pairs with its own entry', () => {
    // Supersession is keyed on (trigger, instrument, side), not the symbol (data dictionary §6), so two Active
    // suggestions can draw on one instrument at once. Six identically titled lines leave the operator unable to tell
    // which stop protects which entry — the ordinal ties each zone's three lines together (gh#727 review).
    const lines = suggestionsToPriceLines(
      [
        { id: 'a', entry: 10, stop: 9, target: 12 },
        { id: 'b', entry: 20, stop: 21, target: 18 },
      ],
      SUGGESTION_PALETTE,
    );
    expect(lines.map((line) => line.title)).toEqual([
      'Entry #1',
      'Stop #1',
      'Target #1',
      'Entry #2',
      'Stop #2',
      'Target #2',
    ]);
  });

  it('is empty for no zones', () => {
    expect(suggestionsToPriceLines([], SUGGESTION_PALETTE)).toEqual([]);
  });
});
