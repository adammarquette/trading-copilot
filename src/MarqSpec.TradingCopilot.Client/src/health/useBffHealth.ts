import { useEffect, useState } from 'react';

/**
 * Outcome of the BFF reachability probe. `checking` is the pre-answer state and must be
 * distinguishable from `unreachable` -- a spinner that looks like a failure is worse than no signal.
 */
export type HealthStatus = 'checking' | 'reachable' | 'unreachable';

/**
 * Probes the BFF once on mount. Carried over from the gh#646 scaffold, where it proved the SPA and its
 * server were talking; in the shell it backs the app bar's connection indicator.
 *
 * A one-shot probe, deliberately. A live connection light belongs to the SignalR stream, not to a
 * polling loop against /health, and a poll here would be a second, disagreeing source of truth.
 */
export function useBffHealth(): HealthStatus {
  const [health, setHealth] = useState<HealthStatus>('checking');

  useEffect(() => {
    // StrictMode runs effects twice in development, so an in-flight probe from the discarded first
    // mount would otherwise settle onto the second mount's state. Abort on cleanup instead.
    const controller = new AbortController();

    // Relative URL on purpose: the bundle is served by the BFF from its own wwwroot, so "same origin
    // as whatever served this page" is always right. A configured host would be wrong in every
    // environment but the one it was written for.
    fetch('/health', { signal: controller.signal })
      .then((response) => {
        setHealth(response.ok ? 'reachable' : 'unreachable');
      })
      .catch(() => {
        // An aborted probe is our own teardown, not a dead BFF -- reporting it would flash a false
        // failure on every StrictMode remount.
        if (!controller.signal.aborted) {
          setHealth('unreachable');
        }
      });

    return () => {
      controller.abort();
    };
  }, []);

  return health;
}
