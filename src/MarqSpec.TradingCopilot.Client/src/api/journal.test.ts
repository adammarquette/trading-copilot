import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  addTradeFeedback,
  type DayDetail,
  type DailyRealizedPnL,
  getDailyRealizedPnL,
  getDayDetail,
  getTradeFeedback,
  type JournalTrade,
  type TradeFeedbackSummary,
} from './journal';

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

const DAY: DailyRealizedPnL = { date: '2026-09-02', realizedPnL: 1250, tradeCount: 3 };

const TRADE: JournalTrade = {
  id: 't1',
  suggestionId: 's1',
  instrument: 'ES',
  side: 'Buy',
  size: 2,
  entryPrice: 5228.5,
  exitPrice: 5241,
  realizedPnL: 1250,
  closedAt: '2026-09-02T15:04:05+00:00',
};

const DETAIL: DayDetail = { date: '2026-09-02', realizedPnL: 1250, trades: [TRADE] };

const FEEDBACK: TradeFeedbackSummary = {
  tradeId: 't1',
  awaitingReview: true,
  entries: [],
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('getDailyRealizedPnL', () => {
  it('asks the account journal for the requested Central-day window', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(200, { days: [DAY] })));

    const result = await getDailyRealizedPnL('a1', '2026-09-01', '2026-09-30');

    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      '/accounts/a1/journal/daily?from=2026-09-01&to=2026-09-30',
    );
    expect(result.ok && result.data).toStrictEqual([DAY]);
  });

  it('unwraps the days envelope so a caller never reaches through it', async () => {
    stubFetch(() => Promise.resolve(response(200, { days: [] })));

    const result = await getDailyRealizedPnL('a1', '2026-09-01', '2026-09-30');

    expect(result.ok && result.data).toStrictEqual([]);
  });

  it('reads a 404 as "this account has no journal", not as a failed load', async () => {
    // The endpoint 404s an absent, foreign OR **Undeclared** account (R-14/R-20). An undeclared account
    // trades nowhere, so it genuinely has nothing to report — a real state the surface names, distinct from
    // a load that failed and should be retried. Fabricating an empty-but-fine month would read as "$0
    // traded", which is a different and much worse claim.
    stubFetch(() => Promise.resolve(response(404)));

    const result = await getDailyRealizedPnL('a1', '2026-09-01', '2026-09-30');

    expect(result.ok && result.data).toBeNull();
  });

  it('passes a real failure through rather than flattening it to "no journal"', async () => {
    stubFetch(() => Promise.resolve(response(500)));

    const result = await getDailyRealizedPnL('a1', '2026-09-01', '2026-09-30');

    expect(result.ok).toBe(false);
  });

  it('surfaces the endpoint\u2019s own refusal reason for a bad window', async () => {
    stubFetch(() => Promise.resolve(response(400, { error: 'from must not be after to.' })));

    const result = await getDailyRealizedPnL('a1', '2026-09-30', '2026-09-01');

    expect(result.ok).toBe(false);
    expect(!result.ok && result.kind === 'refused' && result.reason).toBe(
      'from must not be after to.',
    );
  });
});

describe('getDayDetail', () => {
  it('reads one Central trading day', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(200, DETAIL)));

    const result = await getDayDetail('a1', '2026-09-02');

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/accounts/a1/journal/daily/2026-09-02');
    expect(result.ok && result.data).toStrictEqual(DETAIL);
  });

  it('reads a 404 as "this account has no journal", the same as the calendar', async () => {
    stubFetch(() => Promise.resolve(response(404)));

    const result = await getDayDetail('a1', '2026-09-02');

    expect(result.ok && result.data).toBeNull();
  });

  it('keeps a quiet day as a real, empty day rather than an absence', async () => {
    // Zero trades on a day the operator did not trade is the honest answer, and it is a 200 — never a 404,
    // and never an error screen.
    stubFetch(() =>
      Promise.resolve(response(200, { date: '2026-09-05', realizedPnL: 0, trades: [] })),
    );

    const result = await getDayDetail('a1', '2026-09-05');

    expect(result.ok && result.data?.trades).toStrictEqual([]);
  });
});

describe('getTradeFeedback', () => {
  it('reads a trade\u2019s feedback and its awaiting-review flag', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(200, FEEDBACK)));

    const result = await getTradeFeedback('t1');

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/trades/t1/feedback');
    expect(result.ok && result.data.awaitingReview).toBe(true);
  });
});

describe('addTradeFeedback', () => {
  it('posts the operator\u2019s note to the trade', async () => {
    const fetchMock = stubFetch(() =>
      Promise.resolve(
        response(201, {
          id: 'f1',
          comment: 'Followed the plan.',
          tags: ['Good loss'],
          emotionalState: null,
          author: 'Operator',
          createdAt: '2026-09-02T16:00:00+00:00',
        }),
      ),
    );

    const result = await addTradeFeedback('t1', {
      comment: 'Followed the plan.',
      tags: ['Good loss'],
      emotionalState: null,
    });

    const [url, init] = fetchMock.mock.calls[0] ?? [];
    expect(url).toBe('/trades/t1/feedback');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toStrictEqual({
      comment: 'Followed the plan.',
      tags: ['Good loss'],
      emotionalState: null,
    });
    expect(result.ok && result.data.author).toBe('Operator');
  });

  it('surfaces the server\u2019s refusal rather than a generic failure', async () => {
    // "Feedback can only be attached to a closed trade" is an ANSWER (R-11): the surface renders the reason,
    // and retrying the identical request would be refused again.
    stubFetch(() =>
      Promise.resolve(response(400, { error: 'Feedback can only be attached to a closed trade.' })),
    );

    const result = await addTradeFeedback('t1', {
      comment: 'too early',
      tags: [],
      emotionalState: null,
    });

    expect(!result.ok && result.kind).toBe('refused');
    expect(!result.ok && result.kind === 'refused' && result.reason).toBe(
      'Feedback can only be attached to a closed trade.',
    );
  });
});
