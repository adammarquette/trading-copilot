import { cleanup, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { type AiSpend, getAiSpend } from '../api/ai';
import { renderWithProviders } from '../testing/render';
import { AiSpendSettings } from './AiSpendSettings';

vi.mock('../api/ai', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/ai')>()),
  getAiSpend: vi.fn(),
}));

const spendMock = vi.mocked(getAiSpend);

const SPEND: AiSpend = {
  from: '2026-07-01T05:00:00Z',
  to: '2026-07-15T18:00:00Z',
  totalUsd: 34.1,
  todayUsd: 1.2,
  dailyBudgetUsd: 5,
  byModel: [
    { model: 'claude-opus-5', costUsd: 20 },
    { model: 'claude-haiku-4-5', costUsd: 14.1 },
  ],
  byDay: [
    { day: '2026-07-14', costUsd: 10 },
    { day: '2026-07-15', costUsd: 24.1 },
  ],
};

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(cleanup);

describe('AiSpendSettings', () => {
  it('shows today against the daily cap, the period total, and the by-model breakdown', async () => {
    spendMock.mockResolvedValue({ ok: true, data: SPEND });

    renderWithProviders(<AiSpendSettings />);

    expect(await screen.findByTestId('ai-spend')).toBeTruthy();
    expect(screen.getByTestId('ai-spend-today').textContent?.trim()).toBe('$1.20');
    expect(screen.getByText(/of \$5\.00 daily cap/)).toBeTruthy();
    expect(screen.getByText(/\$34\.10 total/)).toBeTruthy();
    expect(screen.getByTestId('ai-spend-by-model')).toBeTruthy();
    expect(screen.getByText('claude-opus-5')).toBeTruthy();
    expect(screen.getByTestId('ai-spend-by-day')).toBeTruthy();
  });

  it('says out loud that spend is the operator’s own and the cap never blocks manual trading', async () => {
    spendMock.mockResolvedValue({ ok: true, data: SPEND });

    renderWithProviders(<AiSpendSettings />);
    await screen.findByTestId('ai-spend');

    expect(screen.getByText(/your own/i)).toBeTruthy();
    expect(screen.getByText(/stops proposing/i)).toBeTruthy();
    expect(screen.getByText(/never blocks you from trading manually/i)).toBeTruthy();
  });

  it('reports over-cap without implying manual trading is blocked', async () => {
    spendMock.mockResolvedValue({ ok: true, data: { ...SPEND, todayUsd: 7, dailyBudgetUsd: 5 } });

    renderWithProviders(<AiSpendSettings />);
    await screen.findByTestId('ai-spend');

    expect(screen.getByText(/over today’s cap/i)).toBeTruthy();
    expect(screen.getByText(/manual trading is unaffected/i)).toBeTruthy();
  });

  it('shows "no daily cap set" when the governor is inert (null budget)', async () => {
    spendMock.mockResolvedValue({ ok: true, data: { ...SPEND, dailyBudgetUsd: null } });

    renderWithProviders(<AiSpendSettings />);
    await screen.findByTestId('ai-spend');

    expect(screen.getByText(/no daily cap set/i)).toBeTruthy();
  });

  it('shows an empty message when there is no spend in the period', async () => {
    spendMock.mockResolvedValue({
      ok: true,
      data: { ...SPEND, totalUsd: 0, todayUsd: 0, byModel: [], byDay: [] },
    });

    renderWithProviders(<AiSpendSettings />);
    await screen.findByTestId('ai-spend');

    expect(screen.getByText(/no ai spend in this period yet/i)).toBeTruthy();
  });

  it('surfaces a load failure with a retry', async () => {
    spendMock.mockResolvedValue({ ok: false, kind: 'failed', status: 500, error: 'boom' });

    renderWithProviders(<AiSpendSettings />);

    expect(await screen.findByText('Could not load AI spend')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });
});
