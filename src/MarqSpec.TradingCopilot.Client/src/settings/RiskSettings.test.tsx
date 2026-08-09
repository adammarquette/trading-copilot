import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ConsistencyEnforcement,
  type DailyHeadroom,
  DefaultEntryAction,
  FloorSource,
  getHeadroom,
  getRiskProfile,
  type RiskProfile,
  SizingBasis,
  TrailingMode,
} from '../api/risk';
import { renderWithProviders } from '../testing/render';
import { RiskSettings } from './RiskSettings';

vi.mock('../api/risk', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/risk')>()),
  getRiskProfile: vi.fn(),
  getHeadroom: vi.fn(),
}));

// Stub the declare form: the toggle into and out of it is this component's job; the form's own behaviour
// (fields, validation, save) is covered in RiskProfileForm.test.
vi.mock('./RiskProfileForm', () => ({
  RiskProfileForm: ({ onSaved, onCancel }: { onSaved: () => void; onCancel: () => void }) => (
    <div data-testid="risk-form">
      <button type="button" onClick={onSaved}>
        form-saved
      </button>
      <button type="button" onClick={onCancel}>
        form-cancel
      </button>
    </div>
  ),
}));

const profileMock = vi.mocked(getRiskProfile);
const headroomMock = vi.mocked(getHeadroom);

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

const HEADROOM: DailyHeadroom = {
  underGovernor: 450,
  underDailyLossLimit: 800,
  governorSpent: false,
  dailyLossLimitSpent: false,
  dayLoss: 150,
  dayRealizedProfit: 0,
  dailyProfitTarget: 900,
  dailyTargetReached: false,
};

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(cleanup);

describe('RiskSettings', () => {
  it('loads and shows the declared profile and today’s headroom for the account', async () => {
    profileMock.mockResolvedValue({ ok: true, data: PROFILE });
    headroomMock.mockResolvedValue({ ok: true, data: HEADROOM });

    renderWithProviders(<RiskSettings accountId="a1" />);

    expect(await screen.findByTestId('risk-summary')).toBeTruthy();
    expect(screen.getByTestId('headroom')).toBeTruthy();
    expect(profileMock).toHaveBeenCalledWith('a1');
    expect(headroomMock).toHaveBeenCalledWith('a1');
  });

  it('says out loud that these values declare, not enforce', async () => {
    profileMock.mockResolvedValue({ ok: true, data: PROFILE });
    headroomMock.mockResolvedValue({ ok: true, data: HEADROOM });

    renderWithProviders(<RiskSettings accountId="a1" />);
    await screen.findByTestId('risk-summary');

    expect(screen.getByText(/declare/i)).toBeTruthy();
    expect(screen.getByText(/the gate is what/i)).toBeTruthy();
  });

  it('shows the undeclared empty state, not an error, when the account has no profile', async () => {
    profileMock.mockResolvedValue({ ok: true, data: null });
    headroomMock.mockResolvedValue({ ok: true, data: null });

    renderWithProviders(<RiskSettings accountId="a1" />);

    expect(await screen.findByText('No risk profile declared')).toBeTruthy();
    expect(screen.queryByTestId('risk-summary')).toBeNull();
  });

  it('surfaces a load failure with a retry', async () => {
    profileMock.mockResolvedValue({ ok: false, kind: 'failed', status: 500, error: 'boom' });
    headroomMock.mockResolvedValue({ ok: true, data: null });

    renderWithProviders(<RiskSettings accountId="a1" />);

    expect(await screen.findByText('Could not load the risk profile')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });

  it('shows the profile even when headroom fails — headroom is supplementary, never fatal', async () => {
    profileMock.mockResolvedValue({ ok: true, data: PROFILE });
    headroomMock.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 500,
      error: 'no headroom',
    });

    renderWithProviders(<RiskSettings accountId="a1" />);

    expect(await screen.findByTestId('risk-summary')).toBeTruthy();
    expect(screen.getByText(/headroom is unavailable/i)).toBeTruthy();
    expect(screen.queryByTestId('headroom')).toBeNull();
  });
});

describe('RiskSettings — declaring and editing', () => {
  it('opens the declare form from the undeclared state', async () => {
    profileMock.mockResolvedValue({ ok: true, data: null });
    headroomMock.mockResolvedValue({ ok: true, data: null });

    renderWithProviders(<RiskSettings accountId="a1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Declare risk profile' }));

    expect(screen.getByTestId('risk-form')).toBeTruthy();
  });

  it('opens the edit form from the declared state', async () => {
    profileMock.mockResolvedValue({ ok: true, data: PROFILE });
    headroomMock.mockResolvedValue({ ok: true, data: HEADROOM });

    renderWithProviders(<RiskSettings accountId="a1" />);
    await screen.findByTestId('risk-summary');
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));

    expect(screen.getByTestId('risk-form')).toBeTruthy();
    expect(screen.queryByTestId('risk-summary')).toBeNull();
  });

  it('refetches and leaves the form after a successful save', async () => {
    profileMock.mockResolvedValue({ ok: true, data: PROFILE });
    headroomMock.mockResolvedValue({ ok: true, data: HEADROOM });

    renderWithProviders(<RiskSettings accountId="a1" />);
    await screen.findByTestId('risk-summary');
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    fireEvent.click(screen.getByRole('button', { name: 'form-saved' }));

    await waitFor(() => expect(profileMock).toHaveBeenCalledTimes(2));
    expect(await screen.findByTestId('risk-summary')).toBeTruthy();
    expect(screen.queryByTestId('risk-form')).toBeNull();
  });
});
