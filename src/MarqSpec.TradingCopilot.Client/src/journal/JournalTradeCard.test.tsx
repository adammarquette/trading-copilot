import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { JournalTrade } from '../api/journal';
import { getSuggestion } from '../api/suggestions';
import { hexToRgb } from '../testing/color';
import { renderWithProviders } from '../testing/render';
import { colorTokens } from '../theme/tokens';
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

  // Tone is what actually gets PAINTED, read independently of `toneOf` (gh#1115 — `toneOf` already has
  // its own unit tests; a test that re-derives its expectation from `toneOf` cannot fail on a wiring
  // defect such as the realized figure being painted from a hardcoded side check instead of its own
  // sign). `mode` is forced explicitly — see `src/testing/color.ts` for why.
  describe('tone — read where it is rendered, not from toneOf', () => {
    it('paints a winning trade in the profit colour, never the loss one', () => {
      renderWithProviders(<JournalTradeCard trade={{ ...TRADE, realizedPnL: 1250 }} />, {
        mode: 'dark',
      });

      const color = getComputedStyle(screen.getByText('+$1,250.00')).color;

      expect(color).toBe(hexToRgb(colorTokens.dark.trading.long));
      expect(color).not.toBe(hexToRgb(colorTokens.dark.trading.short));
    });

    it('paints a losing trade in the loss colour, never the profit one', () => {
      renderWithProviders(<JournalTradeCard trade={{ ...TRADE, realizedPnL: -400 }} />, {
        mode: 'dark',
      });

      const color = getComputedStyle(screen.getByText('-$400.00')).color;

      expect(color).toBe(hexToRgb(colorTokens.dark.trading.short));
      expect(color).not.toBe(hexToRgb(colorTokens.dark.trading.long));
    });

    it("paints a scratch trade's $0.00 as untoned, not merely as 'not the loss colour'", () => {
      // A scratch trade is neither side. Asserting only "not red" would pass just as well if the wiring
      // painted it green instead — the acceptance bar (gh#1115) is that it takes NO tone, i.e. renders in
      // the ordinary text colour, so this pins it against the theme's own `text.primary` token rather
      // than against an absence of one particular wrong answer.
      renderWithProviders(<JournalTradeCard trade={{ ...TRADE, realizedPnL: 0 }} />, {
        mode: 'dark',
      });

      const color = getComputedStyle(screen.getByText('$0.00')).color;

      expect(color).toBe(hexToRgb(colorTokens.dark.content.high));
      expect(color).not.toBe(hexToRgb(colorTokens.dark.trading.long));
      expect(color).not.toBe(hexToRgb(colorTokens.dark.trading.short));
    });
  });
});
