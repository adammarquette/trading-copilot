import type { Location } from 'react-router';
import { describe, expect, it } from 'vitest';

import { redirectTargetOf } from './redirect';

/** A `Location` carrying just the `state` under test; the rest is inert. */
function locationWith(state: unknown): Location {
  return { pathname: '/sign-in', search: '', hash: '', key: 'default', state } as Location;
}

describe('redirectTargetOf', () => {
  it('falls back to the workspace when nothing was remembered', () => {
    expect(redirectTargetOf(locationWith(null))).toBe('/');
    expect(redirectTargetOf(locationWith(undefined))).toBe('/');
    expect(redirectTargetOf(locationWith({}))).toBe('/');
  });

  it('returns a remembered same-origin relative path', () => {
    expect(redirectTargetOf(locationWith({ from: '/journal' }))).toBe('/journal');
    expect(redirectTargetOf(locationWith({ from: '/accounts/1/risk?tab=x' }))).toBe(
      '/accounts/1/risk?tab=x',
    );
  });

  it('refuses an absolute or protocol-relative target — no open redirect off the cockpit', () => {
    expect(redirectTargetOf(locationWith({ from: 'https://evil.example/phish' }))).toBe('/');
    expect(redirectTargetOf(locationWith({ from: '//evil.example' }))).toBe('/');
    expect(redirectTargetOf(locationWith({ from: 'javascript:alert(1)' }))).toBe('/');
  });

  it('ignores a non-string target', () => {
    expect(redirectTargetOf(locationWith({ from: 42 }))).toBe('/');
    expect(redirectTargetOf(locationWith({ from: { pathname: '/x' } }))).toBe('/');
  });
});
