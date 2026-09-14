import { type ApiResult, requestJson } from './client';

/**
 * The operator's connections and the trading accounts under them — the reads behind the R-14 account context
 * (gh#653). Both list endpoints return a bare array. The resolved {@link TradingMode} is **server-authoritative**
 * and is never recomputed here: the architecture doc records that deriving mode from the venue's `simulated` flag
 * once classified all 293 accounts as practice, funded stages included — the gate failing in the dangerous
 * direction — so this layer only ever *displays* the mode the server resolved.
 */

/**
 * How the API serializes `TradingMode` — the enum's integer value, because there is no `JsonStringEnumConverter`
 * server-side. `Undeclared` is the zero value on purpose: an unclassified account must fail closed, tradeable in
 * no environment, never defaulting to something tradeable.
 */
export const TradingMode = { Undeclared: 0, Practice: 1, Live: 2 } as const;
export type TradingMode = (typeof TradingMode)[keyof typeof TradingMode];

/**
 * A firm connection (login). It never carries a secret: `credentialKey` **names** the env entry the credentials
 * live under, it is not the credential itself.
 */
export interface Connection {
  readonly id: string;
  readonly firmId: string;
  readonly platform: string;
  readonly credentialKey: string;
}

/** An account with the mode the firm's conventions currently resolve for it (server-authoritative, R-14). */
export interface Account {
  readonly id: string;
  readonly venueAccountKey: string;
  readonly name: string;
  readonly stage: number;
  readonly stageOverride: number | null;
  readonly mode: TradingMode;
  readonly canTrade: boolean;
  readonly isVisible: boolean;
  readonly balance: number;
  /**
   * R-14: whether *this deployment environment* permits trading it — practice anywhere, live only in production,
   * undeclared nowhere. Computed per request by the server, distinct from the venue's own `canTrade`.
   */
  readonly tradeableHere: boolean;
}

/** The operator's connections. */
export function listConnections(): Promise<ApiResult<Connection[]>> {
  return requestJson<Connection[]>('GET', '/connections');
}

/** The accounts discovered under one connection. */
export function listConnectionAccounts(connectionId: string): Promise<ApiResult<Account[]>> {
  return requestJson<Account[]>('GET', `/connections/${connectionId}/accounts`);
}

/**
 * Every account across every connection, flattened — the switcher's population. The connections are fetched once,
 * then their accounts concurrently. Any failing read fails the whole load rather than silently returning a
 * partial roster: an account switcher that quietly omits an account is how an operator ends up unable to find the
 * one they meant to trade.
 */
export async function listAllAccounts(): Promise<ApiResult<Account[]>> {
  const connections = await listConnections();
  if (!connections.ok) {
    return connections;
  }

  const perConnection = await Promise.all(
    connections.data.map((connection) => listConnectionAccounts(connection.id)),
  );

  const accounts: Account[] = [];
  for (const result of perConnection) {
    if (!result.ok) {
      return result;
    }
    accounts.push(...result.data);
  }
  return { ok: true, data: accounts };
}
