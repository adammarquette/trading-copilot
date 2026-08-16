import { cleanup, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { type AiAttribution, getAiAttribution } from '../api/ai';
import { renderWithProviders } from '../testing/render';
import { AiCostAttribution } from './AiCostAttribution';

vi.mock('../api/ai', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/ai')>()),
  getAiAttribution: vi.fn(),
}));

const attributionMock = vi.mocked(getAiAttribution);

const ATTRIBUTION: AiAttribution = {
  from: '2026-07-01T05:00:00Z',
  to: '2026-07-15T18:00:00Z',
  suggestions: [
    {
      suggestionId: 's1',
      instrument: 'ES',
      side: 'Buy',
      costUsd: 0.0077,
      calls: 2,
      escalated: true,
      createdAt: '2026-07-15T14:00:00Z',
    },
    {
      suggestionId: 's2',
      instrument: 'NQ',
      side: 'Sell',
      costUsd: 0.0005,
      calls: 1,
      escalated: false,
      createdAt: '2026-07-15T13:00:00Z',
    },
  ],
  takenTrades: [
    {
      tradeId: 't1',
      instrument: 'ES',
      realizedPnL: 125,
      suggestionCostUsd: 0.0077,
      closedAt: '2026-07-15T15:00:00Z',
    },
  ],
  unattributedUsd: 0.0004,
};

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(cleanup);

describe('AiCostAttribution', () => {
  it('shows cost per suggestion, flagging an escalated one, with sub-cent costs not rounded to $0.00', async () => {
    attributionMock.mockResolvedValue({ ok: true, data: ATTRIBUTION });

    renderWithProviders(<AiCostAttribution />);

    expect(await screen.findByTestId('ai-attribution')).toBeTruthy();
    expect(screen.getByTestId('ai-attribution-suggestions')).toBeTruthy();
    expect(screen.getByText('ES Buy')).toBeTruthy();
    expect(screen.getByText('$0.0077')).toBeTruthy(); // a real sub-cent cost reads as non-zero, never $0.00
    expect(screen.getByText('escalated')).toBeTruthy();
  });

  it('shows cost per taken trade against its realized outcome', async () => {
    attributionMock.mockResolvedValue({ ok: true, data: ATTRIBUTION });

    renderWithProviders(<AiCostAttribution />);
    await screen.findByTestId('ai-attribution');

    expect(screen.getByTestId('ai-attribution-trades')).toBeTruthy();
    expect(screen.getByText('+$125.00')).toBeTruthy();
  });

  it('surfaces the unattributed spend of reviews that produced no suggestion — never drops it', async () => {
    attributionMock.mockResolvedValue({ ok: true, data: ATTRIBUTION });

    renderWithProviders(<AiCostAttribution />);
    await screen.findByTestId('ai-attribution');

    const unattributed = screen.getByTestId('ai-attribution-unattributed');
    expect(unattributed.textContent).toMatch(/no suggestion/i);
    expect(unattributed.textContent).toMatch(/\$0\.0004/);
  });

  it('shows an empty message when nothing is attributed in the period', async () => {
    attributionMock.mockResolvedValue({
      ok: true,
      data: { ...ATTRIBUTION, suggestions: [], takenTrades: [], unattributedUsd: 0 },
    });

    renderWithProviders(<AiCostAttribution />);

    expect(await screen.findByTestId('ai-attribution-empty')).toBeTruthy();
  });

  it('surfaces a load failure with a retry', async () => {
    attributionMock.mockResolvedValue({ ok: false, kind: 'failed', status: 500, error: 'boom' });

    renderWithProviders(<AiCostAttribution />);

    expect(await screen.findByText('Could not load AI cost attribution')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });
});
