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
): Promise<ApiResult<T | undefined>> {
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
    // The body read is guarded, not just the send. A 2xx can still carry something that is not JSON — a proxy or
    // misconfigured host answering 200 with an HTML error page, or a truncated body — and an unguarded
    // `JSON.parse` throws *after* the promise has been handed to the caller. Every read surface consumes this as
    // `void getX().then(...)` with no `.catch`, so the rejection is unhandled and the surface sits on its
    // LoadingState forever with no error and no retry. A malformed body IS a failed read (the `failed` contract
    // above names "an unparseable body" already), so it maps to `failed` and the operator gets error + retry
    // (gh#951). The 4xx path has always done this via `readRefusal`; this closes the asymmetry.
    let data: unknown;
    try {
      data = response.status === 204 ? undefined : await readJsonOrUndefined(response);
    } catch {
      return {
        ok: false,
        kind: 'failed',
        status: response.status,
        error: 'The response body could not be read.',
      };
    }
    return { ok: true, data: data as T };
  }

  // A non-2xx that is not 401. A **4xx** that carries a JSON `{ error }` is the gate's ANSWER — surface it as a
  // refusal with the reason and, where the API names it, the binding layer. The 4xx gate is load-bearing: a 5xx
  // is never a gate answer even when it happens to carry an `{ error }` body (an unhandled exception / ASP.NET
  // ProblemDetails / proxy response does), so it must fall through to `failed` — retry is meaningful — and never
  // read to the operator as an authoritative refusal they cannot retry (R-11).
  if (response.status >= 400 && response.status < 500) {
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
  }
  return {
    ok: false,
    kind: 'failed',
    status: response.status,
    error: `The request failed (${response.status}).`,
  };
}

/**
 * {@link request} for a call that is supposed to come back **with a payload** — which is nearly all of them. A
 * 2xx whose body is absent returns `failed` rather than a success carrying `undefined`.
 *
 * This exists because `request` legitimately answers `undefined` for an empty body (that is how a 204 is
 * expressed), and ~40 readers dereferenced `result.data.<prop>` without allowing for it. On a `void …then(…)`
 * path — the shape every read surface uses — the resulting `TypeError` is an unhandled rejection, so the panel
 * never leaves its loading state (gh#963, third of the class after gh#951 and gh#954).
 *
 * The worst of those was not merely a stuck panel. `useExecutionOverlays` is written so an empty overlay reads as
 * **declared-unknown rather than a confirmed flat book** (R-13 / R-19) — but the throw happened *before* the flag
 * that says so was set, so the code guarding against "the chart looks flat" was the code that failed to run.
 *
 * `request` now returns `T | undefined`, so choosing between the two is a **compile-time** decision: a reader
 * that needs a payload cannot silently keep using `request` and dereference it. That is the sweep, enforced.
 */
export async function requestJson<T>(
  method: Method,
  path: string,
  body?: unknown,
): Promise<ApiResult<T>> {
  const result = await request<T>(method, path, body);
  if (!result.ok) {
    return result;
  }
  // `== null` catches BOTH an absent body (`undefined`) and a literal JSON `null`, which parses fine and then
  // throws on the same dereference. Nothing in this API emits `null` today — it takes a proxy or a contract
  // drift — but the whole point of this seam is that the caller cannot be handed something it will crash on.
  if (result.data == null) {
    // No `status`: the success branch does not carry one, and inventing `200` would be a guess (a 201 or 202 is
    // equally possible). Callers read `status` to spot a 401/404, and this is neither.
    return {
      ok: false,
      kind: 'failed',
      error: 'The response carried no data.',
    };
  }
  return { ok: true, data: result.data };
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
 * Turns a token-exchange response into a session, or into a **failed** result — the shared tail of
 * {@link signIn} and {@link acceptInvite}, which differ only in route and body.
 *
 * A 2xx is not on its own proof that there is a token to store, and the two ways it is not are different from
 * the one gh#951 closed. An **empty** body parses to `undefined` — that is the legitimate 204 path, so the
 * parse guard never fires — and a proxy or a contract drift can return JSON of its own. Dereferencing that threw
 * `TypeError` out of an un-caught `await` in the sign-in form, so `setSubmitting(false)` never ran and the
 * operator was left on a dead submit button with no error, on the surface that gates every other surface
 * (gh#954). A 2xx carrying no usable token is therefore a `failed` sign-in, which the existing error branch
 * already renders.
 *
 * The type check is `typeof === 'string'`, not `'token' in data`: a non-string token would otherwise be stored
 * as `[object Object]`, which is **worse than a refusal** — the app looks signed in and every later call 401s.
 */
function sessionFrom(result: ApiResult<TokenResponse | undefined>): ApiResult<void> {
  if (!result.ok) {
    return result;
  }
  const token = result.data?.token;
  if (typeof token !== 'string' || token.length === 0) {
    return {
      ok: false,
      kind: 'failed',
      error: 'Sign-in did not return a session token.',
    };
  }
  storeToken(token);
  return { ok: true, data: undefined };
}

/**
 * Signs in and stores the JWT. On success the token is the only thing that changes; every later call carries it.
 * A bad password returns `failed` with `status: 401` — the sign-in surface renders that as "check your
 * credentials", distinct from a mid-session 401 (which redirects), because sign-in is an anonymous path.
 */
export async function signIn(credentials: LoginRequest): Promise<ApiResult<void>> {
  return sessionFrom(
    await request<TokenResponse>('POST', '/auth/login' satisfies keyof paths, credentials),
  );
}

/** Signs the operator out locally by dropping the token. The next protected call will 401 and route to sign-in. */
export function signOut(): void {
  clearToken();
}

type AcceptInviteRequest = components['schemas']['AcceptInviteRequest'];

/**
 * Redeems an invitation and stores the JWT — the accept-invite analogue of {@link signIn}. The invitation model
 * is deliberately dormant (ADR-0015 / ADR-0017: one deployment, one operator; the sanctioned future is read-only
 * mentee observers), so this redeems the token the endpoint already accepts and nothing more.
 */
export async function acceptInvite(redemption: AcceptInviteRequest): Promise<ApiResult<void>> {
  return sessionFrom(
    await request<TokenResponse>('POST', '/auth/accept-invite' satisfies keyof paths, redemption),
  );
}

/** The signed-in operator, from `GET /auth/me` (an anonymous-object response the spec does not type, so named here). */
export interface CurrentUser {
  readonly id: string;
  readonly email: string;
  readonly displayName: string;
}

/**
 * Reads the signed-in operator's identity — used on load to establish the session from a stored token. A missing
 * or expired token comes back as `failed` with `status: 401` (via {@link request}, which also clears the token),
 * which the caller reads as "signed out"; it does not throw.
 */
export async function getCurrentUser(): Promise<ApiResult<CurrentUser>> {
  return requestJson<CurrentUser>('GET', '/auth/me' satisfies keyof paths);
}
