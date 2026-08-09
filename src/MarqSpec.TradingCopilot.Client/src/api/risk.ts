import { type ApiResult, request } from './client';

/**
 * The account's declared risk profile and today's headroom — the reads and the one write behind the R-5 settings
 * surface (gh#658). Every value here **declares** an input the server-side risk gate enforces; this layer enforces
 * nothing. The gate is what stops a trade. A settings screen that read as the place limits *live* would invite the
 * belief that changing it is what stops a send — so the surface above only ever declares, and says so.
 *
 * Two wire-format facts, shared with {@link ./accounts}: enums serialize as their **integer** value (there is no
 * `JsonStringEnumConverter` server-side), and `decimal` money fields can arrive **string-encoded**, so every number
 * read tolerates a string — normalize at the form boundary, never by trusting the wire type.
 */

/** Whether the loss floor is imposed by the firm or chosen by the operator. */
export const FloorSource = { FirmImposed: 0, SelfImposed: 1 } as const;
export type FloorSource = (typeof FloorSource)[keyof typeof FloorSource];

/** How the drawdown floor trails the equity high-water mark. */
export const TrailingMode = { EndOfDay: 0, Intraday: 1 } as const;
export type TrailingMode = (typeof TrailingMode)[keyof typeof TrailingMode];

/** Whether position sizing measures risk to the working stop or the wider safety stop. */
export const SizingBasis = { ActualStop: 0, SafetyStop: 1 } as const;
export type SizingBasis = (typeof SizingBasis)[keyof typeof SizingBasis];

/** Whether breaching the consistency target refuses the order or only warns (gh#380). */
export const ConsistencyEnforcement = { Advisory: 0, Block: 1 } as const;
export type ConsistencyEnforcement =
  (typeof ConsistencyEnforcement)[keyof typeof ConsistencyEnforcement];

/** Which entry action is primary on the Approve split button (R-11, gh#218). */
export const DefaultEntryAction = { ApproveAndArm: 0, SendAsIs: 1 } as const;
export type DefaultEntryAction = (typeof DefaultEntryAction)[keyof typeof DefaultEntryAction];

/**
 * A declared risk profile as the server returns it (GET / the 200 of a PUT). Numbers tolerate a string because a
 * `decimal` can serialize either way; `null` on the optionals is a real "not set", never a zero to fill in.
 */
export interface RiskProfile {
  readonly accountId: string;
  readonly dailyLossLimit: number | string | null;
  readonly accountProfitTarget: number | string | null;
  readonly startingBalance: number | string;
  readonly floorSource: FloorSource;
  readonly trailingMode: TrailingMode;
  readonly trailingAmount: number | string;
  readonly locksAt: number | string | null;
  readonly perTradeRiskFraction: number | string;
  readonly targetRewardRatio: number | string;
  readonly maxDrawdownPerTrade: number | string;
  /** The personal daily governor — the operator's own max drawdown inside the firm's hard limit. */
  readonly dailyDrawdownGovernor: number | string;
  readonly dailyProfitTarget: number | string | null;
  readonly stopForDayAtProfitTarget: boolean;
  readonly sizingBasis: SizingBasis;
  readonly maxContractsPerOrder: number;
  /** The consistency target — best day ÷ total net profit — a prop-firm rule; `null` where the account has none. */
  readonly maxBestDayFraction: number | string | null;
  readonly consistencyEnforcement: ConsistencyEnforcement;
  readonly defaultEntryAction: DefaultEntryAction;
}

/**
 * The whole-profile declaration (PUT body). The server validates and stores it **whole** — a redeclaration
 * overwrites every field and never merges — so the form loads the current profile, edits, and sends all of it back.
 * `confirmSendAsIsDefault` is the explicit gesture the server demands (422 otherwise) before it will make the
 * practice-only `SendAsIs` the default entry action.
 */
export interface DeclareRiskProfileRequest {
  readonly dailyLossLimit: number | null;
  readonly accountProfitTarget: number | null;
  readonly startingBalance: number;
  readonly floorSource: FloorSource;
  readonly trailingMode: TrailingMode;
  readonly trailingAmount: number;
  readonly locksAt: number | null;
  readonly perTradeRiskFraction: number;
  readonly targetRewardRatio: number;
  readonly maxDrawdownPerTrade: number;
  readonly dailyDrawdownGovernor: number;
  readonly dailyProfitTarget: number | null;
  readonly stopForDayAtProfitTarget: boolean;
  readonly sizingBasis: SizingBasis;
  readonly maxContractsPerOrder: number;
  readonly maxBestDayFraction: number | null;
  readonly consistencyEnforcement: ConsistencyEnforcement;
  readonly defaultEntryAction: DefaultEntryAction;
  readonly confirmSendAsIsDefault: boolean;
}

/**
 * Today's remaining room before the personal governor and, where the firm imposes one, the hard daily limit —
 * readable without a send (gh#587). It is a projection off the declared profile, so it exists only once a profile
 * does.
 */
export interface DailyHeadroom {
  readonly underGovernor: number | string;
  readonly underDailyLossLimit: number | string | null;
  readonly governorSpent: boolean;
  readonly dailyLossLimitSpent: boolean;
  readonly dayLoss: number | string;
  readonly dayRealizedProfit: number | string;
  readonly dailyProfitTarget: number | string | null;
  readonly dailyTargetReached: boolean;
}

/**
 * The account's declared profile, or `null` when it has none. A 404 is **not** an error here: an account with no
 * declared risk is a real first-run state the surface renders as "not declared yet", distinct from a load that
 * failed and should be retried. Every other non-2xx passes through as the {@link ApiResult} failure it is.
 */
export async function getRiskProfile(accountId: string): Promise<ApiResult<RiskProfile | null>> {
  const result = await request<RiskProfile>('GET', `/accounts/${accountId}/risk`);
  if (!result.ok && result.status === 404) {
    return { ok: true, data: null };
  }
  return result;
}

/** Declares (overwrites) the account's whole risk profile. A refusal (400/409/422) surfaces as the gate's answer. */
export function declareRiskProfile(
  accountId: string,
  profile: DeclareRiskProfileRequest,
): Promise<ApiResult<void>> {
  return request<void>('PUT', `/accounts/${accountId}/risk`, profile);
}

/**
 * Today's headroom, or `null` when the account has no declared profile — the same meaningful-404 as
 * {@link getRiskProfile}: there is no governor to project against until one is declared.
 */
export async function getHeadroom(accountId: string): Promise<ApiResult<DailyHeadroom | null>> {
  const result = await request<DailyHeadroom>('GET', `/accounts/${accountId}/risk/headroom`);
  if (!result.ok && result.status === 404) {
    return { ok: true, data: null };
  }
  return result;
}
