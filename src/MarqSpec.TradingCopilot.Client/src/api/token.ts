/**
 * The single home for the operator's JWT.
 *
 * Stored in `localStorage`, deliberately. This client can send orders, so the token is a real credential and
 * `localStorage` is XSS-reachable — the trade-off is on the record in `documentation/adr/0003-authentication.md`.
 * The mitigation is that the bundle is same-origin, first-party, and served under a strict CSP by the BFF, so
 * there is no third-party script surface; the alternative (an httpOnly cookie) would move the auth model off the
 * `Authorization: Bearer` header the BFF already validates and onto cookie auth + CSRF handling — a larger change
 * than R-18's client half warrants here. Revisit if the threat model changes (ADR-0003 owns that decision).
 *
 * Nothing outside this module touches the storage key: reads happen through {@link readToken} (only in the one
 * request path that attaches the header), and the two writers are sign-in and sign-out.
 */
const STORAGE_KEY = 'tc.auth.jwt';

/** The stored JWT, or `null` when the operator is signed out. */
export function readToken(): string | null {
  return localStorage.getItem(STORAGE_KEY);
}

/** Persists the JWT after a successful sign-in. */
export function storeToken(token: string): void {
  localStorage.setItem(STORAGE_KEY, token);
}

/** Drops the JWT — on sign-out, or when a request comes back 401 and the session is gone. */
export function clearToken(): void {
  localStorage.removeItem(STORAGE_KEY);
}
