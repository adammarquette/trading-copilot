import { type ApiResult, requestJson } from './client';

/**
 * The journal read surface (gh#659, R-8 / R-9) — the account's realized P&L by **Central trading day**, one
 * day's closed trades, and the operator's per-trade feedback. Every route here reads what the journal writer
 * already recorded, plus the one optional write the operator makes: their own note on a closed trade.
 *
 * Three wire-format facts, two shared with {@link ./risk}:
 *
 * - A `decimal` can arrive **string-encoded**, so every money field is typed `number | string` and normalized
 *   at the display boundary rather than trusted by its runtime type.
 * - A `DateOnly` serializes as a bare `YYYY-MM-DD` with **no zone**. It is a *Central calendar day*, not an
 *   instant — never hand one to `new Date(...)` and read the local day back out, which is a day early west of
 *   UTC. `../journal/month` owns that arithmetic.
 * - These routes are **not** in `openapi/v1.json` (that spec is manual codegen input and predates gh#1062 /
 *   gh#1064), so their shapes are named here, mirroring `DailyJournalEndpoints` / `TradeFeedbackEndpoints`.
 */

/** One Central trading day's realized P&L — a row of the P&L-by-day calendar (R-9). */
export interface DailyRealizedPnL {
  /** The Central trading day, `YYYY-MM-DD`. */
  readonly date: string;
  /** The day's signed realized P&L. */
  readonly realizedPnL: number | string;
  /** How many realized trades closed that day. Always at least 1 — a quiet day carries no row at all. */
  readonly tradeCount: number;
}

/** One closed, realized trade in the day drill-down (R-8). */
export interface JournalTrade {
  readonly id: string;
  /** The suggestion this trade came from, or `null` for a manually entered one. */
  readonly suggestionId: string | null;
  readonly instrument: string;
  /** The entry side, as a name — `Buy` / `Sell`. */
  readonly side: string;
  readonly size: number;
  readonly entryPrice: number | string;
  readonly exitPrice: number | string;
  readonly realizedPnL: number | string;
  /** When the trade closed (ISO-8601). */
  readonly closedAt: string;
}

/** One Central trading day's drill-down (R-8): the day's realized sum and the trades behind it. */
export interface DayDetail {
  readonly date: string;
  readonly realizedPnL: number | string;
  readonly trades: readonly JournalTrade[];
}

/** One feedback entry on a closed trade (R-8). */
export interface TradeFeedbackEntry {
  readonly id: string;
  readonly comment: string | null;
  readonly tags: readonly string[];
  readonly emotionalState: string | null;
  /** Who wrote it, as a name — `Operator` / `Ai`. Nothing writes `Ai` today; the axis is a documented seam. */
  readonly author: string;
  readonly createdAt: string;
}

/**
 * A trade's feedback, plus the **derived** awaiting-review flag. `awaitingReview` is computed server-side from
 * whether any *operator*-authored entry exists, so it can never drift from the entries beside it — which is
 * why this client re-reads after a write instead of flipping a local copy of it.
 */
export interface TradeFeedbackSummary {
  readonly tradeId: string;
  readonly awaitingReview: boolean;
  readonly entries: readonly TradeFeedbackEntry[];
}

/** What the operator submits on a closed trade. At least one field must carry content, or the server refuses. */
export interface AddTradeFeedbackRequest {
  readonly comment: string | null;
  readonly tags: readonly string[];
  readonly emotionalState: string | null;
}

/** Mirrors `TradeFeedback.CommentMaxLength`; the field stops an over-long note before the server has to. */
export const COMMENT_MAX_LENGTH = 1000;

/** Mirrors `TradeFeedback.EmotionalStateMaxLength`. */
export const EMOTIONAL_STATE_MAX_LENGTH = 64;

/** The list envelope the calendar endpoint returns. Unwrapped below so no caller reaches through it. */
interface DailyRealizedPnLListResponse {
  readonly days: readonly DailyRealizedPnL[];
}

/**
 * The account's realized P&L by Central trading day over `[from, to]` — both inclusive, both `YYYY-MM-DD`
 * (R-9). Days the operator did not trade carry **no row**, so the calendar draws them as untraded rather than
 * inventing a `$0` day.
 *
 * **A 404 comes back as `null` data, not a failure.** The endpoint answers 404 for an account that is absent,
 * foreign (R-20 — never a disclosure) **or `Undeclared`** (R-14). The surface only ever asks about an account
 * the roster just handed it, so in practice this is the undeclared case: an account that trades nowhere and
 * therefore has nothing to report — a real state to name, distinct from a load that failed and should be
 * retried. It is deliberately not flattened to `[]`, which would claim "this account traded nothing this
 * month" — a different and much worse statement than "this account has no journal".
 *
 * The window is always sent explicitly. The endpoint would default to the current Central month, but its
 * "current" is the server's clock and the caller has already chosen a month to show; letting the two disagree
 * is how a calendar ends up labelled September and filled with August.
 */
export async function getDailyRealizedPnL(
  accountId: string,
  from: string,
  to: string,
): Promise<ApiResult<readonly DailyRealizedPnL[] | null>> {
  const query = new URLSearchParams({ from, to });
  const result = await requestJson<DailyRealizedPnLListResponse>(
    'GET',
    `/accounts/${accountId}/journal/daily?${query.toString()}`,
  );
  if (result.ok) {
    return { ok: true, data: result.data.days };
  }
  return result.status === 404 ? { ok: true, data: null } : result;
}

/**
 * One Central trading day's closed, realized trades (R-8). A day with no trades is a **200 with an empty
 * list** — the honest answer for a day the operator stood aside — never a 404 and never an error. A 404 is
 * the same no-journal case as {@link getDailyRealizedPnL}, and comes back as `null` for the same reason.
 */
export async function getDayDetail(
  accountId: string,
  date: string,
): Promise<ApiResult<DayDetail | null>> {
  const result = await requestJson<DayDetail>(
    'GET',
    `/accounts/${accountId}/journal/daily/${date}`,
  );
  if (result.ok) {
    return result;
  }
  return result.status === 404 ? { ok: true, data: null } : result;
}

/** A trade's feedback entries, oldest first, with the derived awaiting-review flag (R-8). */
export function getTradeFeedback(tradeId: string): Promise<ApiResult<TradeFeedbackSummary>> {
  return requestJson<TradeFeedbackSummary>('GET', `/trades/${tradeId}/feedback`);
}

/**
 * Attaches the operator's feedback to a closed trade (R-8) — optional, asynchronous, and never on the close
 * path. The server refuses an unclosed trade or an entirely empty submission with a 400, which arrives as a
 * **refusal** (an answer to render), not a failure to retry.
 */
export function addTradeFeedback(
  tradeId: string,
  feedback: AddTradeFeedbackRequest,
): Promise<ApiResult<TradeFeedbackEntry>> {
  return requestJson<TradeFeedbackEntry>('POST', `/trades/${tradeId}/feedback`, feedback);
}
