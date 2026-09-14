import { describe, expect, it } from 'vitest';

import { CRITICAL_THRESHOLD_MS, formatRemaining, validityAt, WARN_THRESHOLD_MS } from './validity';

const NOW = 1_700_000_000_000;

describe('validityAt', () => {
  it('is normal well inside the window', () => {
    expect(validityAt(NOW + 10 * 60_000, NOW)).toEqual({
      remainingMs: 600_000,
      urgency: 'normal',
      expired: false,
    });
  });

  it('escalates to warn on the two-minute boundary, not after it', () => {
    expect(validityAt(NOW + WARN_THRESHOLD_MS, NOW).urgency).toBe('warn');
    expect(validityAt(NOW + WARN_THRESHOLD_MS + 1, NOW).urgency).toBe('normal');
  });

  it('escalates to critical on the thirty-second boundary', () => {
    expect(validityAt(NOW + CRITICAL_THRESHOLD_MS, NOW).urgency).toBe('critical');
    expect(validityAt(NOW + CRITICAL_THRESHOLD_MS + 1, NOW).urgency).toBe('warn');
  });

  it('is still valid with a fraction of a second left — expiry is the instant, not the rounding', () => {
    expect(validityAt(NOW + 1, NOW).expired).toBe(false);
  });

  it('expires exactly at the deadline', () => {
    expect(validityAt(NOW, NOW)).toEqual({ remainingMs: 0, urgency: 'expired', expired: true });
  });

  it('clamps a past deadline at zero rather than counting backwards', () => {
    // A card whose window closed while the tab was asleep must read "expired", not "-4:12".
    expect(validityAt(NOW - 252_000, NOW)).toEqual({
      remainingMs: 0,
      urgency: 'expired',
      expired: true,
    });
  });
});

describe('formatRemaining', () => {
  it.each([
    [48_000, '0:48'],
    [60_000, '1:00'],
    [125_000, '2:05'],
    [3_600_000, '1:00:00'],
    [3_725_000, '1:02:05'],
    [0, '0:00'],
  ])('renders %ims as %s', (remainingMs, expected) => {
    expect(formatRemaining(remainingMs)).toBe(expected);
  });

  it('rounds up, so a still-valid window never displays 0:00', () => {
    // Flooring would show 0:00 beside a card that is still takeable — the one reading reserved for a closed window.
    expect(formatRemaining(1)).toBe('0:01');
    expect(formatRemaining(999)).toBe('0:01');
  });
});
