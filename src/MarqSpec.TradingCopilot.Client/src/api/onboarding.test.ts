import { afterEach, describe, expect, it, vi } from 'vitest';

import type { Account, Connection } from './accounts';
import {
  AccountStage,
  createConnection,
  createFirm,
  declareConventions,
  discoverAccounts,
  type Firm,
  FirmType,
  type StageConvention,
} from './onboarding';

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

const FIRM: Firm = { id: 'f1', name: 'Topstep', type: FirmType.PropFirm, conventions: [] };
const CONVENTIONS: readonly StageConvention[] = [
  { stage: AccountStage.Evaluation, capitalAtRisk: false },
  { stage: AccountStage.Funded, capitalAtRisk: true },
];
const CONNECTION: Connection = {
  id: 'c1',
  firmId: 'f1',
  platform: 'projectx',
  credentialKey: 'topstep-main',
};
const ACCOUNT: Account = {
  id: 'a1',
  venueAccountKey: 'PRAC-1',
  name: 'PRAC-1',
  stage: AccountStage.Practice,
  stageOverride: null,
  mode: 1,
  canTrade: true,
  isVisible: true,
  balance: 150000,
  tradeableHere: true,
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('onboarding client — firm', () => {
  it('creates a firm with a POST of name and type, and returns the firm', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, FIRM)));

    const result = await createFirm({ name: 'Topstep', type: FirmType.PropFirm });

    expect(mock.mock.calls[0][0]).toBe('/firms');
    expect(mock.mock.calls[0][1]?.method).toBe('POST');
    expect(JSON.parse(String(mock.mock.calls[0][1]?.body))).toEqual({
      name: 'Topstep',
      type: FirmType.PropFirm,
    });
    expect(result).toEqual({ ok: true, data: FIRM });
  });

  it('surfaces the refusal from a duplicate-firm create rather than a success', async () => {
    stubFetch(() =>
      Promise.resolve(response(409, { error: "A firm named 'Topstep' already exists." })),
    );

    const result = await createFirm({ name: 'Topstep', type: FirmType.PropFirm });

    expect(result.ok).toBe(false);
    if (!result.ok && result.kind === 'refused') {
      expect(result.reason).toContain('already exists');
    } else {
      expect.unreachable('a 409 must classify as a refusal carrying the reason');
    }
  });
});

describe('onboarding client — conventions', () => {
  it('declares conventions with a PUT to the firm id, wrapping the list in a conventions body', async () => {
    const mock = stubFetch(() =>
      Promise.resolve(response(200, { ...FIRM, conventions: CONVENTIONS })),
    );

    const result = await declareConventions('f1', CONVENTIONS);

    expect(mock.mock.calls[0][0]).toBe('/firms/f1/conventions');
    expect(mock.mock.calls[0][1]?.method).toBe('PUT');
    expect(JSON.parse(String(mock.mock.calls[0][1]?.body))).toEqual({ conventions: CONVENTIONS });
    expect(result.ok).toBe(true);
  });

  it('surfaces a refusal (an undeclarable stage) rather than a success', async () => {
    stubFetch(() =>
      Promise.resolve(response(400, { error: "Stage 'Unknown' cannot be declared." })),
    );

    const result = await declareConventions('f1', [
      { stage: AccountStage.Unknown, capitalAtRisk: false },
    ]);

    expect(result.ok).toBe(false);
  });
});

describe('onboarding client — connection', () => {
  it('creates a connection with a POST of firm, platform and credential key', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, CONNECTION)));

    const result = await createConnection({
      firmId: 'f1',
      platform: 'projectx',
      credentialKey: 'topstep-main',
    });

    expect(mock.mock.calls[0][0]).toBe('/connections');
    expect(mock.mock.calls[0][1]?.method).toBe('POST');
    expect(JSON.parse(String(mock.mock.calls[0][1]?.body))).toEqual({
      firmId: 'f1',
      platform: 'projectx',
      credentialKey: 'topstep-main',
    });
    expect(result).toEqual({ ok: true, data: CONNECTION });
  });

  it('surfaces the refusal from an unwired platform rather than a success', async () => {
    stubFetch(() =>
      Promise.resolve(response(400, { error: "Platform 'rithmic' has no wired adapter." })),
    );

    const result = await createConnection({
      firmId: 'f1',
      platform: 'rithmic',
      credentialKey: 'topstep-main',
    });

    expect(result.ok).toBe(false);
  });
});

describe('onboarding client — discover', () => {
  it('discovers accounts with a POST to the connection, returning the roster', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, [ACCOUNT])));

    const result = await discoverAccounts('c1');

    expect(mock.mock.calls[0][0]).toBe('/connections/c1/accounts/discover');
    expect(mock.mock.calls[0][1]?.method).toBe('POST');
    expect(result).toEqual({ ok: true, data: [ACCOUNT] });
  });

  it('surfaces a venue failure rather than a success', async () => {
    stubFetch(() =>
      Promise.resolve(
        response(502, { error: 'The venue refused or failed the account discovery.' }),
      ),
    );

    const result = await discoverAccounts('c1');

    expect(result.ok).toBe(false);
  });
});
