import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./client', () => ({ request: vi.fn(), requestJson: vi.fn() }));

import { requestJson } from './client';
import { getBars, getIndicators, getLevels } from './marketData';

const requestJsonMock = vi.mocked(requestJson);

beforeEach(() => {
  requestJsonMock.mockReset();
});

describe('getBars', () => {
  it('reads the bars route with the venue/instrument/resolution/window as query params', async () => {
    requestJsonMock.mockResolvedValue({
      ok: true,
      data: { venue: 'topstepx', instrument: 'ES', resolutionMinutes: 1, bars: [] },
    });

    await getBars('topstepx', 'ES', 1, '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z');

    expect(requestJsonMock).toHaveBeenCalledOnce();
    const [method, path] = requestJsonMock.mock.calls[0];
    expect(method).toBe('GET');
    const url = new URL(path, 'http://local');
    expect(url.pathname).toBe('/api/marketdata/bars');
    expect(url.searchParams.get('venue')).toBe('topstepx');
    expect(url.searchParams.get('instrument')).toBe('ES');
    expect(url.searchParams.get('resolution')).toBe('1');
    expect(url.searchParams.get('from')).toBe('2026-01-01T00:00:00Z');
    expect(url.searchParams.get('to')).toBe('2026-01-02T00:00:00Z');
  });

  it('returns the bar series on success', async () => {
    const wireSeries = {
      venue: 'topstepx',
      instrument: 'ES',
      resolutionMinutes: 1,
      bars: [
        {
          bucketStart: '2026-01-01T00:00:00Z',
          open: 5300,
          high: 5310,
          low: 5295,
          close: 5308,
          volume: 1200,
        },
      ],
    };
    requestJsonMock.mockResolvedValue({ ok: true, data: wireSeries });

    await expect(getBars('topstepx', 'ES', 1, 'a', 'b')).resolves.toEqual({
      ok: true,
      data: wireSeries,
    });
  });

  it('passes a refusal through unchanged (the window-too-wide bound is the gate answer, not an error)', async () => {
    requestJsonMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'window too wide',
    });

    await expect(getBars('topstepx', 'ES', 1, 'a', 'b')).resolves.toEqual({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'window too wide',
    });
  });
});

describe('getIndicators', () => {
  it('reads the indicators route with the indicator + period among the query params', async () => {
    requestJsonMock.mockResolvedValue({
      ok: true,
      data: {
        venue: 'projectx',
        instrument: 'ES',
        resolutionMinutes: 1,
        indicator: 'rsi',
        period: 14,
        points: [],
      },
    });

    await getIndicators(
      'projectx',
      'ES',
      1,
      'rsi',
      14,
      '2026-01-01T00:00:00Z',
      '2026-01-02T00:00:00Z',
    );

    const [method, path] = requestJsonMock.mock.calls[0];
    expect(method).toBe('GET');
    const url = new URL(path, 'http://local');
    expect(url.pathname).toBe('/api/marketdata/indicators');
    expect(url.searchParams.get('venue')).toBe('projectx');
    expect(url.searchParams.get('instrument')).toBe('ES');
    expect(url.searchParams.get('resolution')).toBe('1');
    expect(url.searchParams.get('indicator')).toBe('rsi');
    expect(url.searchParams.get('period')).toBe('14');
    expect(url.searchParams.get('from')).toBe('2026-01-01T00:00:00Z');
    expect(url.searchParams.get('to')).toBe('2026-01-02T00:00:00Z');
  });

  it('returns the indicator series on success', async () => {
    const series = {
      venue: 'projectx',
      instrument: 'ES',
      resolutionMinutes: 1,
      indicator: 'rsi',
      period: 14,
      points: [{ bucketStart: '2026-01-01T00:00:00Z', value: 62.5 }],
    };
    requestJsonMock.mockResolvedValue({ ok: true, data: series });

    await expect(getIndicators('projectx', 'ES', 1, 'rsi', 14, 'a', 'b')).resolves.toEqual({
      ok: true,
      data: series,
    });
  });

  it('passes an unknown-indicator refusal through unchanged (the closed vocabulary is the gate answer)', async () => {
    requestJsonMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: "Unknown indicator 'macd'.",
    });

    await expect(getIndicators('projectx', 'ES', 1, 'macd', 14, 'a', 'b')).resolves.toEqual({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: "Unknown indicator 'macd'.",
    });
  });
});

describe('getLevels', () => {
  it('reads the levels route with the venue/instrument + each timeframe as a repeated query param', async () => {
    requestJsonMock.mockResolvedValue({
      ok: true,
      data: { venue: 'projectx', instrument: 'ES', levels: [] },
    });

    await getLevels('projectx', 'ES', [1, 60]);

    expect(requestJsonMock).toHaveBeenCalledOnce();
    const [method, path] = requestJsonMock.mock.calls[0];
    expect(method).toBe('GET');
    const url = new URL(path, 'http://local');
    expect(url.pathname).toBe('/api/marketdata/levels');
    expect(url.searchParams.get('venue')).toBe('projectx');
    expect(url.searchParams.get('instrument')).toBe('ES');
    // The server binds `int[] timeframes` from a REPEATED param, not a comma list — one entry per timeframe.
    expect(url.searchParams.getAll('timeframes')).toEqual(['1', '60']);
  });

  it('returns the active levels on success', async () => {
    const levels = {
      venue: 'projectx',
      instrument: 'ES',
      levels: [
        {
          timeframeMinutes: 60,
          top: 5310,
          bottom: 5308,
          kind: 'Resistance',
          significance: 0.82,
          formedAtBucket: '2026-01-01T00:00:00Z',
          touchCount: 3,
        },
      ],
    };
    requestJsonMock.mockResolvedValue({ ok: true, data: levels });

    await expect(getLevels('projectx', 'ES', [60])).resolves.toEqual({ ok: true, data: levels });
  });

  it('passes a refusal through unchanged (an empty/invalid timeframe set is the gate answer, not an error)', async () => {
    requestJsonMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'specify at least one positive timeframe (minutes).',
    });

    await expect(getLevels('projectx', 'ES', [])).resolves.toEqual({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'specify at least one positive timeframe (minutes).',
    });
  });
});
