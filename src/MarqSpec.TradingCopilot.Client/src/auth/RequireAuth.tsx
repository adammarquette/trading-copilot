import { Navigate, Outlet, useLocation } from 'react-router';

import { LoadingState } from '../components/LoadingState';
import { useAuth } from './AuthProvider';

/**
 * The gate in front of the authenticated app (R-18). It is the **one** place a signed-out operator is turned away,
 * so there is a single answer to "how did I end up at sign-in".
 *
 * - `loading` — show a loading state, not the sign-in form. A stored token is still being checked, and flashing
 *   sign-in and then the workspace on every reload is worse than a brief, honest spinner.
 * - `anonymous` — redirect to sign-in, remembering where the operator was headed so sign-in can return them there.
 * - `authenticated` — render the routed surface.
 */
export function RequireAuth() {
  const { session } = useAuth();
  const location = useLocation();

  if (session.status === 'loading') {
    return <LoadingState label="Restoring your session…" />;
  }
  if (session.status === 'anonymous') {
    return (
      <Navigate to="/sign-in" replace state={{ from: `${location.pathname}${location.search}` }} />
    );
  }
  return <Outlet />;
}
