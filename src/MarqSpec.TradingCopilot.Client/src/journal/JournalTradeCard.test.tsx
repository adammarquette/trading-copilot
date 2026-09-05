import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { JournalTrade } from '../api/journal';
import { getSuggestion } from '../api/suggestions';
import { renderWithProviders } from '../testing/render';
import { JournalTradeCard } from './JournalTradeCard';

vi.mock('../api/suggestions', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/suggestions')>()),
  getSuggestion: vi.fn(),
}));

// The feedback panel owns its own reads; this card's job is to mount it under the right trade.
vi.mock('./TradeFeedbackPanel', () => ({
  TradeFeedbackPanel: ({ tradeId }: { tradeId: string }) => (
    <div data-testid="feedback-panel">{tradeId}</div>
  ),
}));

// Likewise the delta: what is asserted here is that the card fetches the suggestion and hands it over.
vi.mock('./SuggestionDelta', () => ({
  SuggestionDelta: ({ suggestion }: { suggestion: { id: string } }) => (
    <div data-testid="suggestion-delta">{suggestion.id}</div>
  ),
}));

const suggestionMock = vi.mocked(getSuggestion);

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

beforeEach(() => {
  vi.clearAllMocks();
  suggestionMock.mockResolvedValue({
    ok: true,
    data: { id: 's1' } as never,
  });
});

afterEach(cleanup);

describe('JournalTradeCard', () => {
  it('reads out the round trip at a glance', () => {
    renderWithProviders(<JournalTradeCard trade={TRADE} />);

    expect(screen.getByText('LONG')).toBeTruthy();
    expect(screen.getByText('ES')).toBeTruthy();
    expect(screen.getByText('2 @ 5228.5 → 5241')).toBeTruthy();
    expect(screen.getByText('+$1,250.00')).toBeTruthy();
  });

  it('reads a sell as a short', () => {
    renderWithProviders(<JournalTradeCard trade={{ ...TRADE, side: 'Sell' }} />);

    expect(screen.getByText('SHORT')).toBeTruthy();
  });

  it('fetches nothing until the operator opens the trade', () => {
    // A day is a list; loading every trade's suggestion and feedback up front turns one drill-down into a
    // burst of reads the operator never asked for.
    renderWithProviders(<JournalTradeCard trade={TRADE} />);

    expect(suggestionMock).not.toHaveBeenCalled();
    expect(screen.queryByTestId('feedback-panel')).toBeNull();
  });

  it('opens onto the feedback panel and the suggestion that produced the trade', async () => {
    renderWithProviders(<JournalTradeCard trade={TRADE} />);

    fireEvent.click(screen.getByRole('button', { name: /ES/ }));

    expect(await screen.findByTestId('suggestion-delta')).toBeTruthy();
    expect(suggestionMock).toHaveBeenCalledWith('s1');
    expect(screen.getByTestId('feedback-panel').textContent).toBe('t1');
  });

  it('says a trade had no suggestion behind it rather than showing an empty delta', async () => {
    // A manual ticket is a legitimate trade. Drawing a blank suggested column beside it would read as a
    // suggestion the co-pilot failed to record.
    renderWithProviders(<JournalTradeCard trade={{ ...TRADE, suggestionId: null }} />);

    fireEvent.click(screen.getByRole('button', { name: /ES/ }));

    expect(
      await screen.findByText('Entered manually — no suggestion behind this trade.'),
    ).toBeTruthy();
    expect(suggestionMock).not.toHaveBeenCalled();
  });

  it('keeps the feedback panel usable when the suggestion read fails', async () => {
    // The delta is supplementary. Losing it must not take the operator's own feedback down with it.
    suggestionMock.mockResolvedValue({ ok: false, kind: 'failed', error: 'network down' });

    renderWithProviders(<JournalTradeCard trade={TRADE} />);
    fireEvent.click(screen.getByRole('button', { name: /ES/ }));

    expect(await screen.findByText('The originating suggestion could not be read.')).toBeTruthy();
    expect(screen.getByTestId('feedback-panel')).toBeTruthy();
  });

  it('does not re-read the suggestion each time the trade is toggled', async () => {
    renderWithProviders(<JournalTradeCard trade={TRADE} />);
    const toggle = screen.getByRole('button', { name: /ES/ });

    fireEvent.click(toggle);
    await screen.findByTestId('suggestion-delta');
    fireEvent.click(toggle);
    fireEvent.click(toggle);

    await waitFor(() => {
      expect(screen.getByTestId('suggestion-delta')).toBeTruthy();
    });
    expect(suggestionMock).toHaveBeenCalledTimes(1);
  });
});
