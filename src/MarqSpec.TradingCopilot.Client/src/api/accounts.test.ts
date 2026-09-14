import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  type Account,
  listAllAccounts,
  listConnectionAccounts,
  listConnections,
  TradingMode,
} from './accounts';

function response(status: number, body?: unknown): Response {
  const text = body === undefined ? '' : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(text),
  } as unknown as Response;
}

function stubFetch(impl: (input: RequestInfo | URL) => Promise<Response>) {
  const mock = vi.fn(impl);
  vi.stubGlobal('fetch', mock);
  return mock;
}

const CONNECTION = { id: 'c1', firmId: 'f1', platform: 'projectx', credentialKey: 'PX_A' };

function account(id: string, mode: number): Account {
  return {
    id,
    venueAccountKey: `V-${id}`,
    name: `Account ${id}`,
    stage: 1,
    stageOverride: null,
    mode: mode as TradingMode,
    canTrade: true,
    isVisible: true,
    balance: 1000,
    tradeableHere: true,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('accounts client', () => {
  it('lists the connections', async () => {
    stubFetch(() => Promise.resolve(response(200, [CONNECTION])));

    const result = await listConnections();

    expect(result).toEqual({ ok: true, data: [CONNECTION] });
  });

  it("reads a connection's accounts from its own path", async () => {
    const mock = stubFetch(() =>
      Promise.resolve(response(200, [account('a1', TradingMode.Practice)])),
    );

    const result = await listConnectionAccounts('c1');

    expect(mock.mock.calls[0][0]).toBe('/connections/c1/accounts');
    expect(result.ok && result.data[0].mode).toBe(TradingMode.Practice);
  });

  it('flattens accounts across every connection, in order', async () => {
    stubFetch((input) => {
      const url = String(input);
      if (url === '/connections') {
        return Promise.resolve(
          response(200, [
            { ...CONNECTION, id: 'c1' },
            { ...CONNECTION, id: 'c2' },
          ]),
        );
      }
      if (url === '/connections/c1/accounts') {
        return Promise.resolve(response(200, [account('a1', TradingMode.Practice)]));
      }
      if (url === '/connections/c2/accounts') {
        return Promise.resolve(
          response(200, [account('a2', TradingMode.Live), account('a3', TradingMode.Undeclared)]),
        );
      }
      return Promise.resolve(response(404));
    });

    const result = await listAllAccounts();

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.data.map((a) => a.id)).toEqual(['a1', 'a2', 'a3']);
    }
  });

  it('fails the whole load when the connections read fails — never a partial roster', async () => {
    stubFetch(() => Promise.resolve(response(503)));

    const result = await listAllAccounts();

    expect(result.ok).toBe(false);
  });

  it("fails the whole load when one connection's accounts fail", async () => {
    stubFetch((input) => {
      const url = String(input);
      return url === '/connections'
        ? Promise.resolve(response(200, [{ ...CONNECTION, id: 'c1' }]))
        : Promise.resolve(response(500));
    });

    const result = await listAllAccounts();

    expect(result.ok).toBe(false);
  });
});
