import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';

import type { ApiResult, CurrentUser } from '../api/client';
import {
  acceptInvite as apiAcceptInvite,
  getCurrentUser,
  setOnUnauthenticated,
  signIn as apiSignIn,
  signOut as apiSignOut,
} from '../api/client';
import type { components } from '../api/schema';

type LoginRequest = components['schemas']['LoginRequest'];
type AcceptInviteRequest = components['schemas']['AcceptInviteRequest'];

/**
 * The operator's session (R-18). Three states, kept distinct on purpose: `loading` is the pre-answer state on
 * first paint (a stored token is being checked) and must not be shown as either signed-in or signed-out, because
 * flashing the sign-in form and then the workspace on every reload is worse than a brief, honest spinner.
 */
export type Session =
  | { readonly status: 'loading' }
  | { readonly status: 'authenticated'; readonly user: CurrentUser }
  | { readonly status: 'anonymous' };

interface AuthContextValue {
  readonly session: Session;
  /** Signs in and, on success, establishes the session. Returns the client result so the surface can render a failure. */
  readonly signIn: (credentials: LoginRequest) => Promise<ApiResult<void>>;
  /** Redeems an invitation and, on success, establishes the session — the accept-invite analogue of {@link signIn}. */
  readonly acceptInvite: (redemption: AcceptInviteRequest) => Promise<ApiResult<void>>;
  /** Signs out locally (drops the token) and moves to `anonymous`. */
  readonly signOut: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

/** The session and its actions. Throws outside an {@link AuthProvider} so a mis-mounted surface fails loudly. */
export function useAuth(): AuthContextValue {
  const value = useOptionalAuth();
  if (value === null) {
    throw new Error('useAuth must be used inside an <AuthProvider>.');
  }
  return value;
}

/**
 * The session, or `null` when no {@link AuthProvider} is above. For the rare control that legitimately renders
 * with *or* without a session — the app-bar account menu, which the shell shows only once authenticated — a
 * missing provider is not a bug to throw on. Every gated surface should prefer {@link useAuth}.
 */
export function useOptionalAuth(): AuthContextValue | null {
  return useContext(AuthContext);
}

/**
 * Owns the session. On mount it establishes the session from a stored token via `GET /auth/me`, and wires the
 * client's 401 handler so that an expired token *mid-session* drops the session to `anonymous` — the
 * {@link RequireAuth} guard then routes to sign-in. State only, no navigation: keeping the redirect in the guard
 * (a property of the route tree) rather than here means there is one place a signed-out operator can end up.
 */
export function AuthProvider({ children }: { readonly children: ReactNode }) {
  const [session, setSession] = useState<Session>({ status: 'loading' });

  useEffect(() => {
    let active = true;
    // Wire the 401 handler BEFORE the probe, so a 401 from the probe itself lands here too.
    setOnUnauthenticated(() => {
      if (active) {
        setSession({ status: 'anonymous' });
      }
    });
    void getCurrentUser().then((result) => {
      if (!active) {
        return;
      }
      setSession(
        result.ok ? { status: 'authenticated', user: result.data } : { status: 'anonymous' },
      );
    });
    return () => {
      active = false;
      setOnUnauthenticated(() => {});
    };
  }, []);

  // Both entry paths share one tail: the operation stores a token, then we read the identity back so the session
  // carries a name, not just "signed in". Factored out so sign-in and accept-invite cannot drift.
  const establishFrom = useCallback(
    async (grant: Promise<ApiResult<void>>): Promise<ApiResult<void>> => {
      const granted = await grant;
      if (!granted.ok) {
        return granted;
      }
      const me = await getCurrentUser();
      setSession(me.ok ? { status: 'authenticated', user: me.data } : { status: 'anonymous' });
      return me.ok ? { ok: true, data: undefined } : me;
    },
    [],
  );

  const signIn = useCallback(
    (credentials: LoginRequest) => establishFrom(apiSignIn(credentials)),
    [establishFrom],
  );

  const acceptInvite = useCallback(
    (redemption: AcceptInviteRequest) => establishFrom(apiAcceptInvite(redemption)),
    [establishFrom],
  );

  const signOut = useCallback(() => {
    apiSignOut();
    setSession({ status: 'anonymous' });
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({ session, signIn, acceptInvite, signOut }),
    [session, signIn, acceptInvite, signOut],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
