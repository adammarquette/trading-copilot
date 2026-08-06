import type { TradingMode } from './accounts';
import { type ApiResult, request } from './client';
import type { components } from './schema';

/**
 * The suggestion surface's reads and its two operator acts (gh#654, R-4 / R-12) — list, get-by-id, **pass**
 * (gh#547) and **take** (gh#548).
 *
 * Two properties of this seam are load-bearing and neither is negotiable in a later edit:
 *
 * - **Take arms; it never sends.** `POST /suggestions/{id}/take` stages an editable, *unsent* ticket. Arming and
 *   sending are separate gated steps (R-11b), and nothing in this module reaches the send path.
 * - **A refusal is an answer, not an error.** The take route re-validates at take time (R-12) and answers with a
 *   specific `{ error }`, which {@link request} surfaces as `kind: 'refused'` carrying that exact text. Callers
 *   render the `reason` verbatim — "the market drifted" and "the window closed" are different facts, and
 *   collapsing them into "something went wrong" is the failure this whole card exists to prevent.
 *
 * Response bodies are anonymous objects the OpenAPI document does not type (the endpoints declare only their
 * status codes), so the shapes are named here against `Api/Suggestions/SuggestionContracts.cs`. Request bodies
 * *are* in the spec, so they come from the generated {@link components} and a contract change breaks the build.
 */

/** How the API serializes `OrderSide` — the enum's integer value; there is no `JsonStringEnumConverter` server-side. */
export const OrderSide = { Buy: 0, Sell: 1 } as const;
export type OrderSide = (typeof OrderSide)[keyof typeof OrderSide];

/**
 * The lifecycle state, as the API serializes it. Forward-only: `Active` → `Stale` (drift) → `ExpiredVoid` (time).
 * `Unknown` is the refusable zero — it is never persisted, and the surface should never see one.
 */
export const SuggestionState = { Unknown: 0, Active: 1, Stale: 2, ExpiredVoid: 3 } as const;
export type SuggestionState = (typeof SuggestionState)[keyof typeof SuggestionState];

/** What the operator did with a suggestion — an operator act only; expiry is the clock's, never a disposition. */
export const SuggestionDispositionKind = {
  Unknown: 0,
  Taken: 1,
  Modified: 2,
  Passed: 3,
} as const;
export type SuggestionDispositionKind =
  (typeof SuggestionDispositionKind)[keyof typeof SuggestionDispositionKind];

/**
 * Why the operator passed — the canonical eight, as a `[Flags]` bitmask in one integer (gh#539). `None` (zero) is
 * a **valid** answer: a pass is a neutral decline and R-4 makes its reason explicitly optional.
 */
export const SuggestionPassReason = {
  None: 0,
  AlreadyPositioned: 1 << 0,
  NewsRisk: 1 << 1,
  WrongTime: 1 << 2,
  WaitingBetterLevel: 1 << 3,
  WeakRewardRisk: 1 << 4,
  Sizing: 1 << 5,
  AgainstARule: 1 << 6,
  LowConviction: 1 << 7,
} as const;
export type SuggestionPassReason = (typeof SuggestionPassReason)[keyof typeof SuggestionPassReason];

/** Mirrors `SuggestionDisposition.NoteMaxLength`. The server refuses a longer note; the field stops it earlier. */
export const NOTE_MAX_LENGTH = 1000;

/** The operator's recorded act on a suggestion, with the take-time deviations where there were any. */
export interface SuggestionDisposition {
  readonly suggestionId: string;
  readonly kind: SuggestionDispositionKind;
  readonly reasons: number;
  readonly deviations: number;
  readonly takenEntryPrice: number | null;
  readonly takenStopPrice: number | null;
  readonly takenTargetPrice: number | null;
  readonly takenSize: number | null;
  readonly note: string | null;
  readonly createdAt: string;
}

/**
 * One suggestion as the operator sees it — the whole R-4 spine. Every field here is rendered on the card:
 * a suggestion is *fully specified* by definition, so hiding part of it behind a click would make the operator
 * decide on less than the model proposed.
 */
export interface Suggestion {
  readonly id: string;
  readonly accountId: string;
  readonly instrument: string;
  /** The bar size the setup is framed on (gh#592) — what tells a scalp from a swing. */
  readonly timeframeMinutes: number;
  readonly side: OrderSide;
  /** Contracts. The operator's trigger's size, never the model's. */
  readonly size: number;
  readonly entryPrice: number;
  readonly stopPrice: number;
  readonly targetPrice: number;
  readonly mode: TradingMode;
  readonly state: SuggestionState;
  readonly createdAt: string;
  /** Reward ÷ risk as a unit-free multiple; `null` when the stop equals the entry (no ratio to express). */
  readonly rewardRiskRatio: number | null;
  /** Dollars at the stop; `null` when the instrument has no configured contract spec — omitted, never guessed. */
  readonly riskUsd: number | null;
  /** Dollars at the target; `null` likewise. */
  readonly rewardUsd: number | null;
  /**
   * The model's plain-language reasoning. **Untrusted display data** — render it as text (JSX escapes it), never
   * as markup, and never feed it back into a prompt as instruction.
   */
  readonly rationale: string;
  readonly citedIndicator: string;
  readonly citedPeriod: number;
  readonly citedResolutionMinutes: number;
  /**
   * 0–100, **display only**. R-4: confidence "never moves size, geometry or the gate, and a zero still issues."
   * Nothing in this client may branch on it.
   */
  readonly confidence: number;
  /**
   * When the suggestion stops being actionable — already clamped server-side to the session's auto-flatten
   * deadline. The countdown is derived from this against the browser's clock; the server sends no remainder,
   * because a remainder is stale the instant it is serialized.
   */
  readonly expiresAt: string;
  /** When `state` last changed; `null` while still in the state it was issued in. */
  readonly stateChangedAt: string | null;
  /** Version along the supersede chain — 1 for a first issuance, one higher for each re-formed setup. */
  readonly version: number;
  /** The suggestion this one supersedes, or `null` for the first version. */
  readonly supersedesId: string | null;
  /** Present only on the get-by-id read, and only once the operator has acted. */
  readonly disposition: SuggestionDisposition | null;
}

/**
 * What a take hands back: a **Staged** ticket plus the gate's decision on it. Nothing here has reached the venue —
 * `status` is `Staged`, and sending is the order surface's own, separately gated step.
 */
export interface StagedTicket {
  readonly orderId: string;
  readonly status: string;
  readonly outcome: string;
  readonly approvedQuantity: number;
  readonly bindingLayer: number | null;
  readonly reason: string;
  readonly target: number | null;
}

interface SuggestionListResponse {
  readonly items: readonly Suggestion[];
}

type PassRequestBody = components['schemas']['SuggestionPassRequest'];
type TakeRequestBody = components['schemas']['SuggestionTakeRequest'];

/**
 * A 404 on a suggestion route is an **answer**, not a failure: the row does not exist for this operator (R-20
 * makes a stranger's suggestion a 404 rather than a disclosure), and retrying will 404 again. {@link request}
 * cannot know that — a bodiless 404 is indistinguishable from any other dead response there — so the translation
 * happens here, at the layer that knows what the route means. Without it the operator would read the one
 * genuinely explanatory case as "The request failed (404)."
 */
function missingIsRefusal<T>(result: ApiResult<T>): ApiResult<T> {
  if (!result.ok && result.kind === 'failed' && result.status === 404) {
    return {
      ok: false,
      kind: 'refused',
      status: 404,
      reason: 'That suggestion no longer exists on this account.',
    };
  }
  return result;
}

/**
 * The **actionable** list for an account, newest first. Passing no state is deliberate: the API's default is
 * `Active` *and* un-dispositioned, which is exactly the decision surface — a passed or expired setup stays in the
 * journal, reachable by id, but it is not something the operator can still act on.
 */
export async function listActionableSuggestions(
  accountId: string,
  limit?: number,
): Promise<ApiResult<readonly Suggestion[]>> {
  const query = limit === undefined ? '' : `?limit=${String(limit)}`;
  const result = await request<SuggestionListResponse>(
    'GET',
    `/accounts/${accountId}/suggestions${query}`,
  );
  return result.ok ? { ok: true, data: result.data.items } : result;
}

/** One suggestion by id, in **any** state — the journal outlives the decision window. */
export async function getSuggestion(id: string): Promise<ApiResult<Suggestion>> {
  return missingIsRefusal(await request<Suggestion>('GET', `/suggestions/${id}`));
}

/**
 * Records a **neutral pass** (gh#547). Both arguments are optional because the pass itself is the datum; the
 * reasons and the note are what the R-9 learning loop reads on top of it. One disposition per suggestion — a
 * second pass is refused with a 409, which the caller renders rather than retrying.
 */
export async function passSuggestion(
  id: string,
  reasons: number = SuggestionPassReason.None,
  note?: string,
): Promise<ApiResult<SuggestionDisposition>> {
  const trimmed = note?.trim();
  const body: PassRequestBody = {
    reasons,
    // A blank note is no note — the server normalizes this too, but sending `""` would misreport the operator
    // as having written something.
    note: trimmed === undefined || trimmed.length === 0 ? null : trimmed,
  };
  return missingIsRefusal(
    await request<SuggestionDisposition>('POST', `/suggestions/${id}/pass`, body),
  );
}

/**
 * **Arms** an editable, unsent ticket from a suggestion (gh#548). It does not send, and there is no argument here
 * that could make it send.
 *
 * `referencePrice` is the caller's current market price, exactly as on every other order path — the server fetches
 * no venue quote. It does double duty: it is the value the take-time drift re-check measures against the entry
 * tolerance **now** (R-12), and it becomes the fat-finger band's reference at the gate (R-16). Which is precisely
 * why no caller may default it to the suggestion's own entry: that would make the drift check measure zero drift
 * every time and quietly delete the re-check.
 */
export async function takeSuggestion(
  id: string,
  referencePrice: number,
): Promise<ApiResult<StagedTicket>> {
  const body: TakeRequestBody = { referencePrice };
  return missingIsRefusal(await request<StagedTicket>('POST', `/suggestions/${id}/take`, body));
}
