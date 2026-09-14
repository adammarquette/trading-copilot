import { afterEach, describe, expect, it, vi } from 'vitest';

import { getVenueSetup, type VenueSetup } from './venues';

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

const PROJECTX: VenueSetup = {
  venueId: 'projectx',
  displayName: 'ProjectX / TopstepX',
  credentials: [
    {
      key: 'ApiKey',
      label: 'Username',
      secret: false,
      required: true,
      helpText: 'Your TopstepX username.',
    },
    {
      key: 'ApiSecret',
      label: 'API key',
      secret: true,
      required: true,
      helpText: 'Your TopstepX API key.',
    },
  ],
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('venues client', () => {
  it('fetches the registered setup contracts with a GET to /venues/setup', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, [PROJECTX])));

    const result = await getVenueSetup();

    expect(mock.mock.calls[0][0]).toBe('/venues/setup');
    expect(mock.mock.calls[0][1]?.method).toBe('GET');
    expect(result).toEqual({ ok: true, data: [PROJECTX] });
  });

  it('surfaces a failure rather than a success when the read fails', async () => {
    stubFetch(() => Promise.resolve(response(500, { error: 'boom' })));

    const result = await getVenueSetup();

    expect(result.ok).toBe(false);
  });
});
