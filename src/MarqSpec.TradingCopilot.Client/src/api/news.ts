import { type ApiResult, request, requestJson } from './client';
import { SoftSignalKind } from './relevance';

/**
 * The personalized news feed and its star/mute feedback (gh#742/gh#27, R-2/R-6, ADR-0014). The feed is re-ranked
 * per operator by a **decayed salience** profile built from their own stars/mutes; feedback is a **soft salience
 * weight only** — it reorders what the operator sees and can never move a risk limit, a size, or a gate decision
 * (enforcement lives below the model). Every read and write is R-20-scoped to the operator server-side.
 *
 * Enums serialize as their **integer** value (no `JsonStringEnumConverter` server-side), the same wire convention
 * as the rest of the client. {@link SoftSignalKind} is shared with the relevance-config module.
 */
export { SoftSignalKind } from './relevance';

/** One explained reason an item was reweighted — so the weighting is never a hidden score (ADR-0014). */
export interface NewsSalienceReason {
  readonly dimension: string;
  readonly value: string;
  readonly contribution: number;
}

/** One ranked item in the operator's personalized feed, with their own feedback on it (if any). */
export interface NewsFeedItem {
  readonly dedupKey: string;
  readonly title: string;
  readonly url: string;
  readonly publishedAt: string;
  readonly instruments: readonly string[];
  readonly topics: readonly string[];
  readonly sources: readonly string[];
  readonly baseRelevance: number;
  readonly multiplier: number;
  readonly salience: number;
  /** A one-line, plain-language explanation of the personalized weighting (ADR-0014's "no hidden score"). */
  readonly whyWeighted: string;
  readonly reasons: readonly NewsSalienceReason[];
  /** The operator's own star/mute on this item, or `null` when they have not rated it. */
  readonly feedback: SoftSignalKind | null;
}

/**
 * The operator's personalized feed, ranked by salience (highest first). Unwraps the server's `{ items }` envelope
 * so callers work with the list directly.
 */
export async function getNewsFeed(limit?: number): Promise<ApiResult<NewsFeedItem[]>> {
  const query = limit === undefined ? '' : `?limit=${String(limit)}`;
  const result = await requestJson<{ items: NewsFeedItem[] }>('GET', `/api/news${query}`);
  return result.ok ? { ok: true, data: result.data.items } : result;
}

/**
 * Stars or mutes a news item (upsert — re-rating replaces, so an item carries at most one). A star raises the
 * salience of similar future items; a mute lowers it (never hiding it). The dedup key rides in the **body** — it is
 * a URL, not path-safe.
 */
export function setNewsFeedback(dedupKey: string, kind: SoftSignalKind): Promise<ApiResult<void>> {
  return request<void>('PUT', '/api/news/feedback', { dedupKey, kind });
}

/** Clears star/mute feedback on an item — its salience returns to base. The key is a query param (not path-safe). */
export function clearNewsFeedback(dedupKey: string): Promise<ApiResult<void>> {
  return request<void>('DELETE', `/api/news/feedback?dedupKey=${encodeURIComponent(dedupKey)}`);
}
