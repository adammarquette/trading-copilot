import type { components, paths } from './schema';
import { clearToken, readToken, storeToken } from './token';

/**
 * The BFF client (R-18). This module is the **one place** the JWT is attached to a request; every call to the
 * BFF goes through {@link request}, and there is no exported escape hatch that skips it, so a surface cannot
 * quietly issue an unauthenticated call. Types for request bodies come from the generated {@link paths} /
 * {@link components} (`npm run codegen` from `/openapi/v1.json`), so a change to the API contract surfaces as a
 * compile error here rather than a runtime 400. Response bodies the API returns as anonymous objects are not in
 * the spec, so those shapes are named locally where a caller needs one.
 */

/**
 * The result of a BFF call. The distinction is load-bearing (R-11):
 *
 * - `refused` — the request completed and the **gate said no**. That is an *answer*, not an error: the surface
 *   renders the `reason` (and the binding `layer`, where the API names one), never a generic failure toast, and
 *   retrying the same request would be refused again.
 * - `failed` — the request did **not** complete, or came back with no actionable answer (a network fault, a 5xx,
 *   an unparseable body). Retry is meaningful.
 *
 * A 401 is neither: the session is gone. {@link request} clears the token, notifies via
 * {@link setOnUnauthenticated}, and returns `failed` so the caller stops — it never retries a 401.
 */
export type ApiResult<T> =
  | { readonly ok: true; readonly data: T }
  | {
      readonly ok: false;
      readonly kind: 'refused';
      readonly status: number;
      readonly reason: string;
      readonly layer?: string;
    }
  | {
      readonly ok: false;
      readonly kind: 'failed';
      readonly status?: number;
      readonly error: string;
    };

/** Notified when a request is rejected with 401 — the token is already cleared; the app shell routes to sign-in. */
export type UnauthenticatedHandler = () => void;

let unauthenticatedHandler: UnauthenticatedHandler = () => {};

/** Registers the sign-out/redirect the app runs when a protected call comes back 401. Idempotent; last wins. */
export function setOnUnauthenticated(handler: UnauthenticatedHandler): void {
  unauthenticatedHandler = handler;
}

/**
 * The routes reachable **without** a token — the only ones {@link request} does not attach the bearer to, and the
 * only ones whose 401 is "wrong credentials" rather than "session expired". Kept explicit so adding an anonymous
 * call is a decision on the record (mirrors the BFF's own `[AllowAnonymous]` allow-list, R-18 / gh#604).
 */
const ANONYMOUS_PATHS: ReadonlySet<string> = new Set<string>([
  '/auth/login' satisfies keyof paths,
  '/auth/accept-invite' satisfies keyof paths,
]);

type Method = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

/**
 * Sends one request to the BFF and maps the response onto {@link ApiResult}. THE ONE PLACE the JWT is attached:
 * every protected path carries `Authorization: Bearer <token>`; the anonymous paths do not. Paths are relative on
 * purpose — the bundle is served same-origin by the BFF, so "same origin as the page" is always the right host.
 */
export async function request<T>(
  method: Method,
  path: string,
  body?: unknown,
): Promise<ApiResult<T>> {
  const headers: Record<string, string> = {};
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  if (!ANONYMOUS_PATHS.has(path)) {
    const token = readToken();
    if (token !== null) {
      headers['Authorization'] = `Bearer ${token}`;
    }
  }

  let response: Response;
  try {
    response = await fetch(path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch (cause) {
    // The request never reached a response — offline, DNS, aborted. Always a `failed`; retry is meaningful.
    return {
      ok: false,
      kind: 'failed',
      error: cause instanceof Error ? cause.message : 'The request could not be sent.',
    };
  }

  if (response.status === 401) {
    if (ANONYMOUS_PATHS.has(path)) {
      // A 401 from sign-in is bad credentials, not an expired session — no token to clear, no redirect.
      return {
        ok: false,
        kind: 'failed',
        status: 401,
        error: 'The email or password is incorrect.',
      };
    }
    // The session is gone. Clear it and route to sign-in; never retry a 401.
    clearToken();
    unauthenticatedHandler();
    return {
      ok: false,
      kind: 'failed',
      status: 401,
      error: 'The session has ended. Sign in again.',
    };
  }

  if (response.ok) {
    const data = (response.status === 204 ? undefined : await readJsonOrUndefined(response)) as T;
    return { ok: true, data };
  }

  // A non-2xx that is not 401. If it carries a JSON `{ error }`, the gate ANSWERED — surface it as a refusal with
  // the reason and, where the API names it, the binding layer. Otherwise the call failed and a retry is meaningful.
  const refusal = await readRefusal(response);
  if (refusal !== null) {
    return {
      ok: false,
      kind: 'refused',
      status: response.status,
      reason: refusal.reason,
      layer: refusal.layer,
    };
  }
  return {
    ok: false,
    kind: 'failed',
    status: response.status,
    error: `The request failed (${response.status}).`,
  };
}

async function readJsonOrUndefined(response: Response): Promise<unknown> {
  const text = await response.text();
  if (text.length === 0) {
    return undefined;
  }
  return JSON.parse(text) as unknown;
}

/** Reads a `{ error, layer? }` refusal body, or `null` when the response has no such actionable answer. */
async function readRefusal(response: Response): Promise<{ reason: string; layer?: string } | null> {
  let payload: unknown;
  try {
    payload = await readJsonOrUndefined(response);
  } catch {
    return null; // Unparseable body -> not an answer, a failure.
  }
  if (payload === null || typeof payload !== 'object') {
    return null;
  }
  const record = payload as Record<string, unknown>;
  if (typeof record['error'] !== 'string') {
    return null;
  }
  return {
    reason: record['error'],
    layer: typeof record['layer'] === 'string' ? record['layer'] : undefined,
  };
}

// --- Auth operations ------------------------------------------------------------------------------------------

type LoginRequest = components['schemas']['LoginRequest'];

/** The spec types the request but not the anonymous `{ token }` response body, so it is named here. */
interface TokenResponse {
  readonly token: string;
}

/**
 * Signs in and stores the JWT. On success the token is the only thing that changes; every later call carries it.
 * A bad password returns `failed` with `status: 401` — the sign-in surface renders that as "check your
 * credentials", distinct from a mid-session 401 (which redirects), because sign-in is an anonymous path.
 */
export async function signIn(credentials: LoginRequest): Promise<ApiResult<void>> {
  const result = await request<TokenResponse>(
    'POST',
    '/auth/login' satisfies keyof paths,
    credentials,
  );
  if (result.ok) {
    storeToken(result.data.token);
    return { ok: true, data: undefined };
  }
  return result;
}

/** Signs the operator out locally by dropping the token. The next protected call will 401 and route to sign-in. */
export function signOut(): void {
  clearToken();
}
