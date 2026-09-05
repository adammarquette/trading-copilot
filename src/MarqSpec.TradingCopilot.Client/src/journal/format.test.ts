import { describe, expect, it } from 'vitest';

import { formatSignedUsd } from './format';

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
