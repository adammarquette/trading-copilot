import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  createMap,
  createTopic,
  deleteMap,
  deleteTopic,
  listMaps,
  listTopics,
  type TickerMap,
  type Topic,
  TopicScope,
  updateTopic,
} from './relevance';

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

const MAP: TickerMap = { ticker: 'SPY', instrument: 'ES' };
const TOPIC: Topic = {
  id: 't1',
  name: 'fomc',
  keywords: ['rate', 'powell'],
  scope: TopicScope.Global,
  instrument: null,
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('relevance client — maps', () => {
  it('lists the ticker↔instrument maps', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, [MAP])));

    const result = await listMaps();

    expect(mock.mock.calls[0][0]).toBe('/api/relevance/maps');
    expect(result).toEqual({ ok: true, data: [MAP] });
  });

  it('creates a map with a POST of the pair', async () => {
    const mock = stubFetch(() => Promise.resolve(response(201, MAP)));

    const result = await createMap(MAP);

    expect(mock.mock.calls[0][0]).toBe('/api/relevance/maps');
    expect(mock.mock.calls[0][1]?.method).toBe('POST');
    expect(JSON.parse(String(mock.mock.calls[0][1]?.body))).toEqual(MAP);
    expect(result.ok).toBe(true);
  });

  it('deletes a map by its ticker/instrument pair in the path, url-encoded', async () => {
    const mock = stubFetch(() => Promise.resolve(response(204)));

    const result = await deleteMap('SPY', 'ES');

    expect(mock.mock.calls[0][0]).toBe('/api/relevance/maps/SPY/ES');
    expect(mock.mock.calls[0][1]?.method).toBe('DELETE');
    expect(result.ok).toBe(true);
  });

  it('surfaces a refusal from a duplicate-map create, rather than a success', async () => {
    stubFetch(() => Promise.resolve(response(409, { error: 'A map for SPY→ES already exists.' })));

    const result = await createMap(MAP);

    expect(result.ok).toBe(false);
  });
});

describe('relevance client — topics', () => {
  it('lists the topics', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, [TOPIC])));

    const result = await listTopics();

    expect(mock.mock.calls[0][0]).toBe('/api/relevance/topics');
    expect(result).toEqual({ ok: true, data: [TOPIC] });
  });

  it('creates a topic', async () => {
    const mock = stubFetch(() => Promise.resolve(response(201, TOPIC)));

    const result = await createTopic({
      name: 'fomc',
      keywords: ['rate', 'powell'],
      scope: TopicScope.Global,
      instrument: null,
    });

    expect(mock.mock.calls[0][0]).toBe('/api/relevance/topics');
    expect(mock.mock.calls[0][1]?.method).toBe('POST');
    expect(result.ok).toBe(true);
  });

  it('replaces a topic whole with a PATCH to its id', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, TOPIC)));

    const result = await updateTopic('t1', {
      name: 'fomc',
      keywords: ['rate'],
      scope: TopicScope.Instrument,
      instrument: 'ES',
    });

    expect(mock.mock.calls[0][0]).toBe('/api/relevance/topics/t1');
    expect(mock.mock.calls[0][1]?.method).toBe('PATCH');
    expect(result.ok).toBe(true);
  });

  it('deletes a topic by id', async () => {
    const mock = stubFetch(() => Promise.resolve(response(204)));

    const result = await deleteTopic('t1');

    expect(mock.mock.calls[0][0]).toBe('/api/relevance/topics/t1');
    expect(mock.mock.calls[0][1]?.method).toBe('DELETE');
    expect(result.ok).toBe(true);
  });
});
