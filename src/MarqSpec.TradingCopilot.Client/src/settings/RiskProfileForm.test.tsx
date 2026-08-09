import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ConsistencyEnforcement,
  declareRiskProfile,
  DefaultEntryAction,
  FloorSource,
  type RiskProfile,
  SizingBasis,
  TrailingMode,
} from '../api/risk';
import { renderWithProviders } from '../testing/render';
import { RiskProfileForm } from './RiskProfileForm';

vi.mock('../api/risk', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/risk')>()),
  declareRiskProfile: vi.fn(),
}));

const declareMock = vi.mocked(declareRiskProfile);

const PROFILE: RiskProfile = {
  accountId: 'a1',
  dailyLossLimit: 1000,
  accountProfitTarget: 3000,
  startingBalance: 50000,
  floorSource: FloorSource.FirmImposed,
  trailingMode: TrailingMode.EndOfDay,
  trailingAmount: 2000,
  locksAt: 52000,
  perTradeRiskFraction: 0.25,
  targetRewardRatio: 2,
  maxDrawdownPerTrade: 300,
  dailyDrawdownGovernor: 600,
  dailyProfitTarget: 900,
  stopForDayAtProfitTarget: true,
  sizingBasis: SizingBasis.ActualStop,
  maxContractsPerOrder: 3,
  maxBestDayFraction: 0.5,
  consistencyEnforcement: ConsistencyEnforcement.Advisory,
  defaultEntryAction: DefaultEntryAction.ApproveAndArm,
};

function field(label: RegExp): HTMLInputElement {
  return screen.getByLabelText(label, { exact: false }) as HTMLInputElement;
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(cleanup);

describe('RiskProfileForm', () => {
  it('pre-fills the form from the current profile', () => {
    renderWithProviders(
      <RiskProfileForm accountId="a1" initial={PROFILE} onSaved={vi.fn()} onCancel={vi.fn()} />,
    );

    expect(field(/personal daily governor/i).value).toBe('600');
    expect(field(/starting balance/i).value).toBe('50000');
  });

  it('declares the whole profile and calls onSaved on success', async () => {
    declareMock.mockResolvedValue({ ok: true, data: undefined });
    const onSaved = vi.fn();

    renderWithProviders(
      <RiskProfileForm accountId="a1" initial={PROFILE} onSaved={onSaved} onCancel={vi.fn()} />,
    );
    fireEvent.click(screen.getByRole('button', { name: /save risk profile/i }));

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(declareMock).toHaveBeenCalledWith(
      'a1',
      expect.objectContaining({ startingBalance: 50000, dailyDrawdownGovernor: 600 }),
    );
  });

  it('surfaces a gate refusal and does not call onSaved', async () => {
    declareMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'startingBalance must be positive',
    });
    const onSaved = vi.fn();

    renderWithProviders(
      <RiskProfileForm accountId="a1" initial={PROFILE} onSaved={onSaved} onCancel={vi.fn()} />,
    );
    fireEvent.click(screen.getByRole('button', { name: /save risk profile/i }));

    expect(await screen.findByText(/gate refused/i)).toBeTruthy();
    expect(onSaved).not.toHaveBeenCalled();
  });

  it('blocks the send and shows a field error when a required value is cleared', async () => {
    const onSaved = vi.fn();

    renderWithProviders(
      <RiskProfileForm accountId="a1" initial={PROFILE} onSaved={onSaved} onCancel={vi.fn()} />,
    );
    fireEvent.change(field(/starting balance/i), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: /save risk profile/i }));

    expect(await screen.findByText(/starting balance must be a positive number/i)).toBeTruthy();
    expect(declareMock).not.toHaveBeenCalled();
  });

  it('cancels without declaring anything', () => {
    const onCancel = vi.fn();

    renderWithProviders(
      <RiskProfileForm accountId="a1" initial={PROFILE} onSaved={vi.fn()} onCancel={onCancel} />,
    );
    fireEvent.click(screen.getByRole('button', { name: /cancel/i }));

    expect(onCancel).toHaveBeenCalled();
    expect(declareMock).not.toHaveBeenCalled();
  });

  it('starts an undeclared account from a blank form', () => {
    renderWithProviders(
      <RiskProfileForm accountId="a1" initial={null} onSaved={vi.fn()} onCancel={vi.fn()} />,
    );

    expect(field(/personal daily governor/i).value).toBe('');
    expect(field(/starting balance/i).value).toBe('');
  });
});
