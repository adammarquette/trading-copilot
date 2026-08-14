import { type ApiResult, request } from './client';

/**
 * The operator's own AI spend, from the durable `AIUsage` ledger (gh#741) — the read behind the "AI usage & spend"
 * settings section (gh#62, ADR-0015 "your keys, your bill"). Reads the ledger, never the export-only Prometheus meter
 * (ADR-0002). Money fields arrive as JSON numbers (the server's `decimal`s) — fine to display. `dailyBudgetUsd` is
 * `null` when no cap is configured (the governor is inert): a real "no cap", never a zero to fill in.
 */

/** One model's share of period spend. */
export interface AiSpendModelSlice {
  readonly model: string;
  readonly costUsd: number;
}

/** One Central trading day's spend. `day` is an ISO date (`YYYY-MM-DD`). */
export interface AiSpendDaySlice {
  readonly day: string;
  readonly costUsd: number;
}

/**
 * The operator's AI spend over a period, against the governor's **daily** cap. The cap is per-day, so `todayUsd`
 * against `dailyBudgetUsd` is the live "against the cap" figure; `totalUsd` is the period's context.
 */
export interface AiSpend {
  readonly from: string;
  readonly to: string;
  readonly totalUsd: number;
  readonly todayUsd: number;
  /** The governor's daily cap, or `null` when no cap is configured (inert) — a real "no cap", not a zero. */
  readonly dailyBudgetUsd: number | null;
  readonly byModel: readonly AiSpendModelSlice[];
  readonly byDay: readonly AiSpendDaySlice[];
}

/**
 * Reads the operator's AI spend for a period, through the authenticated client (R-18 — never a raw `fetch`). With no
 * bounds the server defaults to the current Central month; pass `from` / `to` (ISO-8601 UTC) to override. Owner-scoped
 * server-side (R-20, ADR-0015): the operator's own bill, distinct from the governor's deployment-wide enforcement.
 */
export function getAiSpend(from?: string, to?: string): Promise<ApiResult<AiSpend>> {
  const query = new URLSearchParams();
  if (from !== undefined) {
    query.set('from', from);
  }
  if (to !== undefined) {
    query.set('to', to);
  }
  const suffix = query.toString();
  return request<AiSpend>('GET', suffix.length === 0 ? '/api/ai/spend' : `/api/ai/spend?${suffix}`);
}
