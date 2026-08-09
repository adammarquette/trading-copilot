import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  ConsistencyEnforcement,
  type DailyHeadroom,
  type DeclareRiskProfileRequest,
  DefaultEntryAction,
  FloorSource,
  type RiskProfile,
  SizingBasis,
  TrailingMode,
  declareRiskProfile,
  getHeadroom,
  getRiskProfile,
} from './risk';

function response(status: number, body?: unknown): Response {
  const text = body === undefined ? '' : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(text),
  } as unknown as Response;
}

function stubFetch(impl: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>) {
  const mock = vi.fn(impl);
  vi.stubGlobal('fetch', mock);
  return mock;
}

const PROFILE: RiskProfile = {
  accountId: 'a1',
  dailyLossLimit: 1000,
  accountProfitTarget: 3000,
  startingBalance: 50000,
  floorSource: FloorSource.FirmImposed,
  trailingMode: TrailingMode.EndOfDay,
  trailingAmount: 2000,
  locksAt: 52000,
  perTradeRiskFraction: 0.25,
  targetRewardRatio: 2,
  maxDrawdownPerTrade: 300,
  dailyDrawdownGovernor: 600,
  dailyProfitTarget: 900,
  stopForDayAtProfitTarget: true,
  sizingBasis: SizingBasis.ActualStop,
  maxContractsPerOrder: 3,
  maxBestDayFraction: 0.5,
  consistencyEnforcement: ConsistencyEnforcement.Advisory,
  defaultEntryAction: DefaultEntryAction.ApproveAndArm,
};

const DECLARE: DeclareRiskProfileRequest = {
  dailyLossLimit: 1000,
  accountProfitTarget: 3000,
  startingBalance: 50000,
  floorSource: FloorSource.SelfImposed,
  trailingMode: TrailingMode.Intraday,
  trailingAmount: 2000,
  locksAt: null,
  perTradeRiskFraction: 0.25,
  targetRewardRatio: 2,
  maxDrawdownPerTrade: 300,
  dailyDrawdownGovernor: 600,
  dailyProfitTarget: null,
  stopForDayAtProfitTarget: false,
  sizingBasis: SizingBasis.SafetyStop,
  maxContractsPerOrder: 0,
  maxBestDayFraction: null,
  consistencyEnforcement: ConsistencyEnforcement.Block,
  defaultEntryAction: DefaultEntryAction.ApproveAndArm,
  confirmSendAsIsDefault: false,
};

const HEADROOM: DailyHeadroom = {
  underGovernor: 450,
  underDailyLossLimit: 800,
  governorSpent: false,
  dailyLossLimitSpent: false,
  dayLoss: 150,
  dayRealizedProfit: 0,
  dailyProfitTarget: 900,
  dailyTargetReached: false,
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('risk client — reads', () => {
  it('reads the declared profile from the account risk path', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, PROFILE)));

    const result = await getRiskProfile('a1');

    expect(mock.mock.calls[0][0]).toBe('/accounts/a1/risk');
    expect(result).toEqual({ ok: true, data: PROFILE });
  });

  it('maps a 404 to a null profile — an undeclared account is a state, not an error', async () => {
    stubFetch(() => Promise.resolve(response(404)));

    const result = await getRiskProfile('a1');

    expect(result).toEqual({ ok: true, data: null });
  });

  it('passes a server refusal through unchanged rather than swallowing it as "undeclared"', async () => {
    stubFetch(() => Promise.resolve(response(400, { error: 'startingBalance must be positive' })));

    const result = await getRiskProfile('a1');

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.kind === 'refused' && result.reason).toContain('positive');
  });

  it('reads headroom from the headroom path', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, HEADROOM)));

    const result = await getHeadroom('a1');

    expect(mock.mock.calls[0][0]).toBe('/accounts/a1/risk/headroom');
    expect(result).toEqual({ ok: true, data: HEADROOM });
  });

  it('maps a headroom 404 to null — with no declared profile there is nothing to project', async () => {
    stubFetch(() => Promise.resolve(response(404)));

    const result = await getHeadroom('a1');

    expect(result).toEqual({ ok: true, data: null });
  });
});

describe('risk client — declaration', () => {
  it('PUTs the whole profile to the account risk path', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200)));

    const result = await declareRiskProfile('a1', DECLARE);

    expect(mock.mock.calls[0][0]).toBe('/accounts/a1/risk');
    expect(mock.mock.calls[0][1]?.method).toBe('PUT');
    expect(JSON.parse(String(mock.mock.calls[0][1]?.body))).toEqual(DECLARE);
    expect(result.ok).toBe(true);
  });

  it('surfaces a 422 (SendAsIs without confirmation) as a refusal, not a success', async () => {
    stubFetch(() =>
      Promise.resolve(response(422, { error: 'SendAsIs requires an explicit confirmation.' })),
    );

    const result = await declareRiskProfile('a1', {
      ...DECLARE,
      defaultEntryAction: DefaultEntryAction.SendAsIs,
    });

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.status).toBe(422);
  });
});
