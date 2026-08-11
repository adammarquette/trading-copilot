import type { Account, Connection } from './accounts';
import { type ApiResult, request } from './client';

// The walk's writes return the same shapes the read paths serve — re-exported so a caller has one import site
// for everything onboarding.
export type { Account, Connection } from './accounts';

/**
 * The onboarding write ops (gh#653, R-14/R-17): register a firm, declare what its stages mean, add a login, and
 * discover the accounts under it — the firm → conventions → connection → discover walk a new deployment takes
 * before it can trade anything. The reads that back the account context live in {@link ./accounts}; these are the
 * writes that first populate it.
 *
 * A firm and its conventions are **deployment-global** config, not account-scoped — there is no active account yet
 * when this walk runs. Enums serialize as their **integer** value (no `JsonStringEnumConverter` server-side), the
 * same wire convention as the rest of the client.
 */

/** A firm's kind. A prop firm has staged programmes (Evaluation/Funded); a brokerage's accounts are live or paper. */
export const FirmType = { PropFirm: 0, Brokerage: 1 } as const;
export type FirmType = (typeof FirmType)[keyof typeof FirmType];

/**
 * An account's stage. `Unknown` is a refusable zero: a stage that could not be read is never declared and resolves
 * to Undeclared (tradeable nowhere), rather than defaulting to something tradeable.
 */
export const AccountStage = { Unknown: 0, Practice: 1, Evaluation: 2, Funded: 3 } as const;
export type AccountStage = (typeof AccountStage)[keyof typeof AccountStage];

/**
 * One stage declaration — what a stage means at a firm. `capitalAtRisk` is the whole safety point: it maps the
 * stage to Live (`true`) or Practice (`false`). Deriving it from the venue's own flag once classified every funded
 * account as practice, so the operator declares it and the venue never gets a vote.
 */
export interface StageConvention {
  readonly stage: AccountStage;
  readonly capitalAtRisk: boolean;
}

/** A registered firm and the stage conventions declared for it. */
export interface Firm {
  readonly id: string;
  readonly name: string;
  readonly type: FirmType;
  readonly conventions: readonly StageConvention[];
}

/** The request to register a firm the operator trades with. */
export interface CreateFirmInput {
  readonly name: string;
  readonly type: FirmType;
}

/**
 * The request to add a firm login. `credentialKey` **names** the env entry the credentials live under — it is not
 * the credential itself, and no secret ever reaches the browser (ADR-0015). Only wired platforms are accepted.
 */
export interface CreateConnectionInput {
  readonly firmId: string;
  readonly platform: string;
  readonly credentialKey: string;
}

/** Registers a firm the operator trades with. The name is unique within the operator's workspace. */
export function createFirm(input: CreateFirmInput): Promise<ApiResult<Firm>> {
  return request<Firm>('POST', '/firms', input);
}

/**
 * Declares the firm's full set of stage conventions — the request replaces any existing set whole (the server
 * never merges a partial), and a re-declaration re-resolves the mode of every account under the firm.
 */
export function declareConventions(
  firmId: string,
  conventions: readonly StageConvention[],
): Promise<ApiResult<Firm>> {
  return request<Firm>('PUT', `/firms/${firmId}/conventions`, { conventions });
}

/** Adds a login for a firm on a platform (one per firm × platform). Returns the connection; never a secret. */
export function createConnection(input: CreateConnectionInput): Promise<ApiResult<Connection>> {
  return request<Connection>('POST', '/connections', input);
}

/**
 * Discovers the accounts reachable through a connection, resolving each account's mode from the firm's declared
 * conventions. Rediscovery is an upsert — it refreshes the roster and never duplicates. Returns the accounts as
 * the same {@link Account} shape the read paths serve.
 */
export function discoverAccounts(connectionId: string): Promise<ApiResult<Account[]>> {
  return request<Account[]>('POST', `/connections/${connectionId}/accounts/discover`);
}
