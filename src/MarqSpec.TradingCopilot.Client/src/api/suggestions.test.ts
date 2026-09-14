import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  getSuggestion,
  listActionableSuggestions,
  passSuggestion,
  SuggestionPassReason,
  takeSuggestion,
} from './suggestions';

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

function sentBody(mock: ReturnType<typeof stubFetch>): unknown {
  return JSON.parse(String(mock.mock.calls[0][1]?.body)) as unknown;
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('suggestions client — reads', () => {
  it('unwraps the list envelope and preserves the server order', async () => {
    // Order is the server's — newest first, never re-sorted here, and above all never by confidence (R-4).
    const mock = stubFetch(() =>
      Promise.resolve(response(200, { items: [{ id: 's1' }, { id: 's2' }] })),
    );

    const result = await listActionableSuggestions('acc-1');

    expect(mock.mock.calls[0][0]).toBe('/accounts/acc-1/suggestions');
    expect(result.ok && result.data.map((suggestion) => suggestion.id)).toEqual(['s1', 's2']);
  });

  it('sends no state filter, because the API default IS the actionable set', async () => {
    // Active AND un-dispositioned is the server's default. Restating it as a client-side filter would be a
    // second definition of "actionable", and the take path re-checks against the server's.
    const mock = stubFetch(() => Promise.resolve(response(200, { items: [] })));

    await listActionableSuggestions('acc-1');

    expect(String(mock.mock.calls[0][0])).not.toContain('state=');
  });

  it('reads one suggestion by id — the card stays addressable after it leaves the list', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, { id: 's1' })));

    const result = await getSuggestion('s1');

    expect(mock.mock.calls[0][0]).toBe('/suggestions/s1');
    expect(result.ok && result.data.id).toBe('s1');
  });
});

describe('suggestions client — pass', () => {
  it('posts the reasons bitmask and the note', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, { suggestionId: 's1' })));

    const reasons = SuggestionPassReason.NewsRisk | SuggestionPassReason.WrongTime;
    const result = await passSuggestion('s1', reasons, '  CPI print in 10 min  ');

    expect(mock.mock.calls[0][0]).toBe('/suggestions/s1/pass');
    expect(sentBody(mock)).toEqual({ reasons: 6, note: 'CPI print in 10 min' });
    expect(result.ok).toBe(true);
  });

  it('posts a bare, reasonless pass — a neutral decline is a complete answer (R-4)', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, { suggestionId: 's1' })));

    await passSuggestion('s1');

    expect(sentBody(mock)).toEqual({ reasons: 0, note: null });
  });

  it('sends a whitespace-only note as no note, not as an empty one', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, { suggestionId: 's1' })));

    await passSuggestion('s1', SuggestionPassReason.Sizing, '   ');

    expect(sentBody(mock)).toEqual({ reasons: 32, note: null });
  });

  it('surfaces a second pass as a refusal carrying the 409 reason', async () => {
    stubFetch(() =>
      Promise.resolve(response(409, { error: 'This suggestion already has a disposition.' })),
    );

    const result = await passSuggestion('s1');

    expect(result.ok).toBe(false);
    expect(!result.ok && result.kind === 'refused' && result.reason).toBe(
      'This suggestion already has a disposition.',
    );
  });
});

describe('suggestions client — take', () => {
  it('posts only the reference price — arming needs nothing else from the client', async () => {
    // The tick size, point value and safety-stop distance come from the SERVER's spec source. A client that
    // could supply them could mis-size every downstream risk calculation.
    const mock = stubFetch(() => Promise.resolve(response(200, { orderId: 'o1' })));

    await takeSuggestion('s1', 5230.5);

    expect(mock.mock.calls[0][0]).toBe('/suggestions/s1/take');
    expect(sentBody(mock)).toEqual({ referencePrice: 5230.5 });
  });

  it.each([
    [
      422,
      'The market has drifted past this suggestion’s entry tolerance; it can no longer be taken at that level.',
    ],
    [422, "This suggestion's validity window has passed and it can no longer be taken."],
    [422, "No contract spec is configured for 'ZZ', so it cannot be sized or taken."],
    [409, 'This suggestion already has a disposition and cannot be taken.'],
    [409, 'This suggestion already has a live order; cancel or complete it before taking again.'],
  ])('carries the %i refusal through verbatim: %s', async (status, error) => {
    // Every one of these is a DIFFERENT fact about a different problem, and the operator's next move differs.
    // The seam must not normalise, map or summarise them — it hands the server's own sentence up untouched.
    stubFetch(() => Promise.resolve(response(status, { error })));

    const result = await takeSuggestion('s1', 5230.5);

    expect(!result.ok && result.kind === 'refused' && result.reason).toBe(error);
  });

  it('keeps a 5xx a FAILURE, never a refusal — retry is meaningful and the gate said nothing', async () => {
    stubFetch(() => Promise.resolve(response(503, { error: 'upstream exploded' })));

    const result = await takeSuggestion('s1', 5230.5);

    expect(!result.ok && result.kind).toBe('failed');
  });

  it('turns a bodiless 404 into an explanatory refusal, not "The request failed (404)."', async () => {
    // R-20 makes another operator's suggestion a 404 rather than a disclosure, so a 404 here is an ANSWER:
    // the row does not exist for this operator and retrying will 404 again.
    stubFetch(() => Promise.resolve(response(404)));

    const result = await takeSuggestion('s1', 5230.5);

    expect(!result.ok && result.kind).toBe('refused');
    expect(!result.ok && result.kind === 'refused' && result.reason).toBe(
      'That suggestion no longer exists on this account.',
    );
  });
});
