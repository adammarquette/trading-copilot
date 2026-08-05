/**
 * The service worker's cache boundary (gh#650, R-19). The installable PWA is a **presentation client only**: the
 * app *shell* is cached for a fast, offline-capable launch, but **server state is never cached**. A cached position
 * read is a lie about money, and an offline-looking kill-switch state is worse — the operator would read protection
 * that is not there. So every request under a state path must go to the network and show *unavailable* rather than
 * *stale* (the same declared-unknown posture `PositionReconciliationService` takes server-side, R-19 / ADR-0013).
 *
 * This module is the single source of truth for that boundary: `sw.ts` uses {@link NETWORK_ONLY_PATH} as its
 * navigation denylist, and `sw-cache-policy.test.ts` asserts it directly — the acceptance criterion "no state read
 * is cache-served" is proven here, by a test, not by inspection of the worker.
 */

/**
 * Matches every path that returns server state — account, order, position, suggestion, risk and market data all
 * live under `/api/` — plus the `/health` and `/ready` liveness probes. A request matching this is served from the
 * network or not at all; it is never cached, and the shell's navigation fallback never answers it.
 */
export const NETWORK_ONLY_PATH = /^\/(api|health|ready)(\/|$)/i;

/**
 * True when a path must always reach the network and may never be served (or written) from cache. Belt-and-braces
 * companion to {@link NETWORK_ONLY_PATH} for call sites that hold a pathname rather than a route pattern.
 */
export function isNetworkOnlyPath(pathname: string): boolean {
  return NETWORK_ONLY_PATH.test(pathname);
}
