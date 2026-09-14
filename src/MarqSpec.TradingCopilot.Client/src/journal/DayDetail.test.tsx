import { cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { type DayDetail as DayDetailData, getDayDetail, type JournalTrade } from '../api/journal';
import { renderWithProviders } from '../testing/render';
import { DayDetail } from './DayDetail';

vi.mock('../api/journal', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/journal')>()),
  getDayDetail: vi.fn(),
}));

vi.mock('./JournalTradeCard', () => ({
  JournalTradeCard: ({ trade }: { trade: { id: string } }) => (
    <div data-testid="trade-card">{trade.id}</div>
  ),
}));

const detailMock = vi.mocked(getDayDetail);

const TRADE: JournalTrade = {
  id: 't1',
  suggestionId: null,
  instrument: 'ES',
  side: 'Buy',
  size: 2,
  entryPrice: 5228.5,
  exitPrice: 5241,
  realizedPnL: 1250,
  closedAt: '2026-09-02T15:04:05+00:00',
};

const DETAIL: DayDetailData = { date: '2026-09-02', realizedPnL: 850, trades: [TRADE] };

beforeEach(() => {
  vi.clearAllMocks();
  detailMock.mockResolvedValue({ ok: true, data: DETAIL });
});

afterEach(cleanup);

describe('DayDetail', () => {
  it('heads the day with its date, trade count and realized net', async () => {
    renderWithProviders(<DayDetail accountId="a1" date="2026-09-02" />);

    expect(await screen.findByText('Wednesday, September 2')).toBeTruthy();
    expect(screen.getByTestId('day-summary').textContent).toBe('1 trade · +$850.00');
  });

  it('lists the day\u2019s trades', async () => {
    renderWithProviders(<DayDetail accountId="a1" date="2026-09-02" />);

    expect(
      (await screen.findAllByTestId('trade-card')).map((node) => node.textContent),
    ).toStrictEqual(['t1']);
  });

  it('says a quiet day was quiet rather than showing an error', async () => {
    detailMock.mockResolvedValue({
      ok: true,
      data: { date: '2026-09-05', realizedPnL: 0, trades: [] },
    });

    renderWithProviders(<DayDetail accountId="a1" date="2026-09-05" />);

    expect(await screen.findByText('No trades closed on this day.')).toBeTruthy();
  });

  it('names the undeclared-account case rather than reporting a broken read', async () => {
    // A 404 here is the account having no journal at all (R-14: an undeclared account trades nowhere).
    detailMock.mockResolvedValue({ ok: true, data: null });

    renderWithProviders(<DayDetail accountId="a1" date="2026-09-02" />);

    expect(await screen.findByText('This account has no journal')).toBeTruthy();
  });

  it('offers a retry when the day could not be read', async () => {
    detailMock.mockResolvedValueOnce({ ok: false, kind: 'failed', error: 'network down' });

    renderWithProviders(<DayDetail accountId="a1" date="2026-09-02" />);

    expect(await screen.findByText('network down')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('Wednesday, September 2')).toBeTruthy();
  });

  it('re-reads when the operator picks another day', async () => {
    const { rerender } = renderWithProviders(<DayDetail accountId="a1" date="2026-09-02" />);
    await screen.findByText('Wednesday, September 2');

    detailMock.mockResolvedValue({
      ok: true,
      data: { date: '2026-09-03', realizedPnL: -120, trades: [] },
    });
    // rerender replaces the tree in place -- no cleanup, which would unmount what rerender needs.
    rerender(<DayDetail accountId="a1" date="2026-09-03" />);

    expect(await screen.findByText('Thursday, September 3')).toBeTruthy();
    expect(detailMock).toHaveBeenLastCalledWith('a1', '2026-09-03');
  });
});
