import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  addTradeFeedback,
  getTradeFeedback,
  type TradeFeedbackEntry,
  type TradeFeedbackSummary,
} from '../api/journal';
import { renderWithProviders } from '../testing/render';
import { TradeFeedbackPanel } from './TradeFeedbackPanel';

vi.mock('../api/journal', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/journal')>()),
  getTradeFeedback: vi.fn(),
  addTradeFeedback: vi.fn(),
}));

const readMock = vi.mocked(getTradeFeedback);
const addMock = vi.mocked(addTradeFeedback);

const ENTRY: TradeFeedbackEntry = {
  id: 'f1',
  comment: 'Good read — waited for the reclaim.',
  tags: ['SPY led'],
  emotionalState: 'calm',
  author: 'Operator',
  createdAt: '2026-09-02T16:00:00+00:00',
};

function summary(overrides: Partial<TradeFeedbackSummary> = {}): TradeFeedbackSummary {
  return { tradeId: 't1', awaitingReview: false, entries: [ENTRY], ...overrides };
}

beforeEach(() => {
  // A factory mock keeps its calls between tests unless they are cleared; a leaked call count makes the
  // "reloads after a write" assertion pass against the previous test's read.
  vi.clearAllMocks();
  readMock.mockResolvedValue({ ok: true, data: summary() });
  addMock.mockResolvedValue({ ok: true, data: ENTRY });
});

afterEach(cleanup);

describe('TradeFeedbackPanel', () => {
  it('shows the feedback already recorded on the trade', async () => {
    renderWithProviders(<TradeFeedbackPanel tradeId="t1" />);

    expect(await screen.findByText('Good read — waited for the reclaim.')).toBeTruthy();
    expect(screen.getByText('SPY led')).toBeTruthy();
    expect(screen.getByText('calm')).toBeTruthy();
  });

  it('flags a closed trade nobody has reviewed', async () => {
    // R-8: a trade can arm and run unattended, so "awaiting review" is a real state, not a nag.
    readMock.mockResolvedValue({ ok: true, data: summary({ awaitingReview: true, entries: [] }) });

    renderWithProviders(<TradeFeedbackPanel tradeId="t1" />);

    expect(await screen.findByText('Awaiting review')).toBeTruthy();
  });

  it('refuses to submit an entirely empty note, matching what the server would refuse', async () => {
    renderWithProviders(<TradeFeedbackPanel tradeId="t1" />);
    await screen.findByText('Good read — waited for the reclaim.');

    expect(screen.getByRole('button', { name: 'Add feedback' }).hasAttribute('disabled')).toBe(
      true,
    );
    expect(addMock).not.toHaveBeenCalled();
  });

  it('sends the comment, the state of mind and the chosen tags together', async () => {
    renderWithProviders(<TradeFeedbackPanel tradeId="t1" />);
    await screen.findByText('Good read — waited for the reclaim.');

    fireEvent.change(screen.getByLabelText('Comment'), { target: { value: 'Chased it.' } });
    fireEvent.change(screen.getByLabelText('State of mind'), { target: { value: 'rushed' } });
    fireEvent.click(screen.getByRole('button', { name: 'Chased' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add feedback' }));

    await waitFor(() => {
      expect(addMock).toHaveBeenCalledWith('t1', {
        comment: 'Chased it.',
        emotionalState: 'rushed',
        tags: ['Chased'],
      });
    });
  });

  it('re-reads the trade after a write rather than guessing the new awaiting-review flag', async () => {
    // `AwaitingReview` is DERIVED server-side from who authored the entries. Flipping a local copy of it
    // is exactly the drift the server-side derivation exists to prevent.
    renderWithProviders(<TradeFeedbackPanel tradeId="t1" />);
    await screen.findByText('Good read — waited for the reclaim.');
    expect(readMock).toHaveBeenCalledTimes(1);

    fireEvent.change(screen.getByLabelText('Comment'), { target: { value: 'Chased it.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add feedback' }));

    await waitFor(() => {
      expect(readMock).toHaveBeenCalledTimes(2);
    });
  });

  it('keeps the operator\u2019s words on screen when the server refuses them', async () => {
    // Clearing a rejected comment loses what they wrote. The refusal is an answer to show, not a reset.
    addMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'Feedback can only be attached to a closed trade.',
    });

    renderWithProviders(<TradeFeedbackPanel tradeId="t1" />);
    await screen.findByText('Good read — waited for the reclaim.');

    fireEvent.change(screen.getByLabelText('Comment'), { target: { value: 'Chased it.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add feedback' }));

    expect(
      await screen.findByText('Feedback can only be attached to a closed trade.'),
    ).toBeTruthy();
    expect((screen.getByLabelText('Comment') as HTMLTextAreaElement).value).toBe('Chased it.');
  });

  it('offers a retry when the read itself failed', async () => {
    readMock.mockResolvedValueOnce({ ok: false, kind: 'failed', error: 'network down' });

    renderWithProviders(<TradeFeedbackPanel tradeId="t1" />);

    expect(await screen.findByText('network down')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('Good read — waited for the reclaim.')).toBeTruthy();
  });
});
