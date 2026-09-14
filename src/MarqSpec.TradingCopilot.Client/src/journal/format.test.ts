import { describe, expect, it } from 'vitest';

import { formatSignedUsd, toneOf } from './format';

describe('formatSignedUsd', () => {
  it('signs a profit so a scan of the column reads without colour', () => {
    expect(formatSignedUsd(1250)).toBe('+$1,250.00');
  });

  it('signs a loss', () => {
    expect(formatSignedUsd(-400)).toBe('-$400.00');
  });

  it('leaves a flat result unsigned — it is neither', () => {
    expect(formatSignedUsd(0)).toBe('$0.00');
  });

  it('normalizes a string-encoded decimal off the wire', () => {
    // A `decimal` can serialize either way; trusting the runtime type is how a P&L renders as `NaN`.
    expect(formatSignedUsd('150.50')).toBe('+$150.50');
  });

  it('renders an absent value as an em dash, never as zero', () => {
    expect(formatSignedUsd(null)).toBe('—');
  });
});

describe('toneOf', () => {
  // The defect this replaced: a tile whose tone came from its LABEL, so `Best day` in an all-red month
  // rendered a loss in the long colour and a scratch trade was painted as a win (gh#659 review).
  it('takes the tone from the figure, not from the tile it sits in', () => {
    expect(toneOf(1250)).toBe('positive');
    expect(toneOf(-400)).toBe('negative');
    expect(toneOf('1250.50')).toBe('positive');
    expect(toneOf('-0.25')).toBe('negative');
  });

  it('gives a flat or absent figure no tone rather than borrowing one', () => {
    expect(toneOf(0)).toBeUndefined();
    expect(toneOf('0.00')).toBeUndefined();
    expect(toneOf(null)).toBeUndefined();
  });
});
