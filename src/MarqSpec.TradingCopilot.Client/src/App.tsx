import { useEffect, useState } from 'react';

/**
 * Outcome of the BFF reachability probe. `checking` is the pre-answer state and must be
 * distinguishable from `unreachable` -- a spinner that looks like a failure is worse than no signal.
 */
export type HealthStatus = 'checking' | 'reachable' | 'unreachable';

const HEALTH_LABELS: Record<HealthStatus, string> = {
  checking: 'checking…',
  reachable: 'reachable',
  unreachable: 'unreachable',
};

export function App() {
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

  return (
    <main className="app">
      <h1 className="app__title">Trading Co-Pilot</h1>
      <p className="app__subtitle">React + TypeScript scaffold</p>
      {/* role="status" is both the honest semantics (a polite live region announcing a result that
          arrives after paint) and what the test queries by -- no test-only attribute needed. */}
      <p className="app__health" role="status" data-status={health}>
        BFF <code>/health</code>: <strong>{HEALTH_LABELS[health]}</strong>
      </p>
    </main>
  );
}
