import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { clearToken, readToken, storeToken } from './token';

// The JWT lives in localStorage under one key; these prove the three exports round-trip it and clean up.
beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  localStorage.clear();
});

describe('token', () => {
  it('reads null when the operator is signed out', () => {
    expect(readToken()).toBeNull();
  });

  it('reads back a stored token', () => {
    storeToken('jwt-abc');

    expect(readToken()).toBe('jwt-abc');
  });

  it('overwrites the previous token on a new sign-in', () => {
    storeToken('old-jwt');
    storeToken('new-jwt');

    expect(readToken()).toBe('new-jwt');
  });

  it('clears the token on sign-out', () => {
    storeToken('jwt-abc');

    clearToken();

    expect(readToken()).toBeNull();
  });

  it('clearToken is a no-op when already signed out', () => {
    expect(() => {
      clearToken();
    }).not.toThrow();
    expect(readToken()).toBeNull();
  });

  it('persists under the stable, namespaced storage key', () => {
    // The key is an implementation detail, but renaming it silently signs every operator out on the next deploy
    // (the old token is orphaned, unreadable), so it is pinned here on purpose.
    storeToken('jwt-xyz');

    expect(localStorage.getItem('tc.auth.jwt')).toBe('jwt-xyz');
  });
});
