import { cleanup, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { TradingMode } from '../api/accounts';
import {
  OrderSide,
  type Suggestion,
  type SuggestionDisposition,
  SuggestionDeviation,
  SuggestionDispositionKind,
  SuggestionState,
  SuggestionOrigin,
} from '../api/suggestions';
import { renderWithProviders } from '../testing/render';
import { SuggestionDelta } from './SuggestionDelta';

const DISPOSITION: SuggestionDisposition = {
  suggestionId: 's1',
  kind: SuggestionDispositionKind.Modified,
  reasons: 0,
  deviations: SuggestionDeviation.Entry | SuggestionDeviation.Size,
  takenEntryPrice: 5229,
  takenStopPrice: 5222,
  takenTargetPrice: 5244,
  takenSize: 1,
  note: null,
  createdAt: '2026-09-02T14:00:00+00:00',
};

const SUGGESTION: Suggestion = {
  id: 's1',
  accountId: 'a1',
  instrument: 'ES',
  timeframeMinutes: 5,
  side: OrderSide.Buy,
  size: 2,
  entryPrice: 5228.5,
  stopPrice: 5222,
  targetPrice: 5244,
  mode: TradingMode.Practice,
  state: SuggestionState.ExpiredVoid,
  createdAt: '2026-09-02T13:30:00+00:00',
  rewardRiskRatio: 2.4,
  riskUsd: 325,
  rewardUsd: 775,
  rationale: 'VWAP reclaim with SPY leading.',
  origin: SuggestionOrigin.Scan,
  citedIndicator: 'VWAP',
  citedPeriod: 0,
  citedResolutionMinutes: 5,
  confidence: 68,
  expiresAt: '2026-09-02T14:30:00+00:00',
  stateChangedAt: null,
  version: 1,
  supersedesId: null,
  disposition: DISPOSITION,
};

afterEach(cleanup);

describe('SuggestionDelta', () => {
  it('puts what was suggested beside what was taken — the R-9 delta', () => {
    renderWithProviders(<SuggestionDelta suggestion={SUGGESTION} />);

    const entry = screen.getByTestId('delta-entry');
    expect(entry.textContent).toContain('5228.5');
    expect(entry.textContent).toContain('5229');
  });

  it('marks only the fields that actually deviated', () => {
    // The bitmask is the server's own record of what the operator changed. Re-deriving it from a price
    // comparison here would drift from what R-9 aggregates — and the stop/target were NOT changed.
    renderWithProviders(<SuggestionDelta suggestion={SUGGESTION} />);

    expect(screen.getByTestId('delta-entry').dataset.deviated).toBe('true');
    expect(screen.getByTestId('delta-size').dataset.deviated).toBe('true');
    expect(screen.getByTestId('delta-stop').dataset.deviated).toBe('false');
    expect(screen.getByTestId('delta-target').dataset.deviated).toBe('false');
  });

  it('never marks a field the bitmask did not name, even where the two values differ', () => {
    // The distinguishing case, and the reason the mask is read rather than the prices compared: a PASSED
    // suggestion was never taken, so every taken price is null while `deviations` is correctly `None`. A
    // client-side comparison would mark all four fields changed and report deviations on a trade the operator
    // never placed -- disagreeing with exactly the figure R-9 aggregates.
    renderWithProviders(
      <SuggestionDelta
        suggestion={{
          ...SUGGESTION,
          disposition: {
            ...DISPOSITION,
            kind: SuggestionDispositionKind.Passed,
            deviations: SuggestionDeviation.None,
            takenEntryPrice: null,
            takenStopPrice: null,
            takenTargetPrice: null,
            takenSize: null,
          },
        }}
      />,
    );

    for (const field of ['entry', 'stop', 'target', 'size']) {
      expect(screen.getByTestId(`delta-${field}`).dataset.deviated).toBe('false');
    }
    expect(screen.getByText('Passed')).toBeTruthy();
  });

  it('names the disposition the operator recorded', () => {
    renderWithProviders(<SuggestionDelta suggestion={SUGGESTION} />);

    expect(screen.getByText('Modified')).toBeTruthy();
  });

  it('reads an unmodified take as taken as suggested', () => {
    renderWithProviders(
      <SuggestionDelta
        suggestion={{
          ...SUGGESTION,
          disposition: {
            ...DISPOSITION,
            kind: SuggestionDispositionKind.Taken,
            deviations: SuggestionDeviation.None,
            takenEntryPrice: 5228.5,
            takenSize: 2,
          },
        }}
      />,
    );

    expect(screen.getByText('Taken as suggested')).toBeTruthy();
    expect(screen.getByTestId('delta-entry').dataset.deviated).toBe('false');
  });

  it('says no disposition was recorded rather than implying the operator acted', () => {
    // A suggestion that expired carries no disposition at all (gh#539) — R-9 must never read a timeout as
    // a decision. Neither may this surface.
    renderWithProviders(<SuggestionDelta suggestion={{ ...SUGGESTION, disposition: null }} />);

    expect(screen.getByText('No disposition recorded')).toBeTruthy();
    expect(screen.getByTestId('delta-entry').textContent).toContain('5228.5');
  });

  it('shows the suggestion\u2019s own reward:risk, so the taken-vs-suggested read has a benchmark', () => {
    renderWithProviders(<SuggestionDelta suggestion={SUGGESTION} />);

    expect(screen.getByText('2.4R')).toBeTruthy();
  });
});
