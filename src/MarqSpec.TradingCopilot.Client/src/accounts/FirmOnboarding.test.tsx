import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { Account } from '../api/accounts';
import {
  AccountStage,
  type Connection,
  createConnection,
  createFirm,
  declareConventions,
  discoverAccounts,
  type Firm,
  FirmType,
} from '../api/onboarding';
import { getVenueSetup, type VenueSetup } from '../api/venues';
import { renderWithProviders } from '../testing/render';
import { FirmOnboarding } from './FirmOnboarding';

vi.mock('../api/onboarding', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/onboarding')>()),
  createFirm: vi.fn(),
  declareConventions: vi.fn(),
  createConnection: vi.fn(),
  discoverAccounts: vi.fn(),
}));
vi.mock('../api/venues', () => ({ getVenueSetup: vi.fn() }));

const createFirmMock = vi.mocked(createFirm);
const declareMock = vi.mocked(declareConventions);
const createConnectionMock = vi.mocked(createConnection);
const discoverMock = vi.mocked(discoverAccounts);
const getVenueSetupMock = vi.mocked(getVenueSetup);

const PROP_FIRM: Firm = { id: 'f1', name: 'Topstep', type: FirmType.PropFirm, conventions: [] };
const CONNECTION: Connection = {
  id: 'c1',
  firmId: 'f1',
  platform: 'projectx',
  credentialKey: 'topstep-main',
};
const ACCOUNT: Account = {
  id: 'a1',
  venueAccountKey: 'PRAC-1',
  name: 'PRAC-1',
  stage: AccountStage.Practice,
  stageOverride: null,
  mode: 1,
  canTrade: true,
  isVisible: true,
  balance: 150000,
  tradeableHere: true,
};
const PROJECTX_SETUP: VenueSetup = {
  venueId: 'projectx',
  displayName: 'ProjectX / TopstepX',
  credentials: [
    {
      key: 'ApiKey',
      label: 'Username',
      secret: false,
      required: true,
      helpText: 'Your TopstepX username.',
    },
    {
      key: 'ApiSecret',
      label: 'API key',
      secret: true,
      required: true,
      helpText: 'Your TopstepX API key.',
    },
  ],
};

/** A promise whose resolution we drive by hand, so we can assert the UI mid-request. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}

beforeEach(() => {
  vi.clearAllMocks();
  getVenueSetupMock.mockResolvedValue({ ok: true, data: [PROJECTX_SETUP] });
});

afterEach(cleanup);

describe('FirmOnboarding', () => {
  it('walks a new prop firm from firm → conventions → connection → discover, then finishes', async () => {
    createFirmMock.mockResolvedValue({ ok: true, data: PROP_FIRM });
    declareMock.mockResolvedValue({ ok: true, data: PROP_FIRM });
    createConnectionMock.mockResolvedValue({ ok: true, data: CONNECTION });
    discoverMock.mockResolvedValue({ ok: true, data: [ACCOUNT] });
    const onComplete = vi.fn();

    renderWithProviders(<FirmOnboarding onComplete={onComplete} />);

    // Step 1 — name the firm; prop firm is the default type.
    fireEvent.change(screen.getByLabelText(/firm name/i, { exact: false }), {
      target: { value: 'Topstep' },
    });
    fireEvent.click(screen.getByRole('button', { name: /create firm/i }));
    await waitFor(() =>
      expect(createFirmMock).toHaveBeenCalledWith({ name: 'Topstep', type: FirmType.PropFirm }),
    );

    // Step 2 — conventions. Defaults (Evaluation practice, Funded at risk) are sent, and Practice is
    // always declared not-at-risk so a discovered practice account resolves to Practice, not Undeclared.
    fireEvent.click(await screen.findByRole('button', { name: /declare conventions/i }));
    await waitFor(() =>
      expect(declareMock).toHaveBeenCalledWith('f1', [
        { stage: AccountStage.Practice, capitalAtRisk: false },
        { stage: AccountStage.Evaluation, capitalAtRisk: false },
        { stage: AccountStage.Funded, capitalAtRisk: true },
      ]),
    );

    // Step 3 — the login. The credential key names the server-side env entry, never the secret.
    fireEvent.change(await screen.findByLabelText(/credential key/i, { exact: false }), {
      target: { value: 'topstep-main' },
    });
    fireEvent.click(screen.getByRole('button', { name: /add connection/i }));
    await waitFor(() =>
      expect(createConnectionMock).toHaveBeenCalledWith({
        firmId: 'f1',
        platform: 'projectx',
        credentialKey: 'topstep-main',
      }),
    );

    // Step 4 — discover the accounts under the login, then finish.
    fireEvent.click(await screen.findByRole('button', { name: /discover accounts/i }));
    await waitFor(() => expect(discoverMock).toHaveBeenCalledWith('c1'));
    expect(await screen.findByText('PRAC-1')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /finish/i }));
    expect(onComplete).toHaveBeenCalledTimes(1);
  });

  it('offers Brokerage as not-yet-supported, so a walk can never strand accounts as Undeclared (gh#780)', () => {
    renderWithProviders(<FirmOnboarding onComplete={vi.fn()} />);

    // A brokerage account has no mode-resolution path yet — the per-stage convention model can't classify it and
    // no brokerage platform is wired — so completing a brokerage walk would leave every discovered account
    // Undeclared (untradeable everywhere). The type is therefore disabled rather than silently leading there.
    const brokerage = screen.getByRole('button', { name: /brokerage/i }) as HTMLButtonElement;
    expect(brokerage.disabled).toBe(true);

    // It cannot be selected: clicking the disabled option is a no-op and prop firm stays the type, so no firm is
    // ever created without the conventions step that gives its accounts a mode.
    fireEvent.click(brokerage);
    expect(createFirmMock).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: /prop firm/i }).getAttribute('aria-pressed')).toBe(
      'true',
    );
  });

  it('surfaces a refused firm create and stays on the first step', async () => {
    createFirmMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: "A firm named 'Topstep' already exists.",
    });

    renderWithProviders(<FirmOnboarding onComplete={vi.fn()} />);

    fireEvent.change(screen.getByLabelText(/firm name/i, { exact: false }), {
      target: { value: 'Topstep' },
    });
    fireEvent.click(screen.getByRole('button', { name: /create firm/i }));

    expect(await screen.findByText(/already exists/i)).toBeTruthy();
    // Still on step 1: the conventions step never opened.
    expect(screen.queryByRole('button', { name: /declare conventions/i })).toBeNull();
  });

  it('makes clear the credential key is a name, not the secret itself', async () => {
    createFirmMock.mockResolvedValue({ ok: true, data: PROP_FIRM });
    declareMock.mockResolvedValue({ ok: true, data: PROP_FIRM });

    renderWithProviders(<FirmOnboarding onComplete={vi.fn()} />);

    // Walk to the connection step: name the firm, then accept the default conventions.
    fireEvent.change(screen.getByLabelText(/firm name/i, { exact: false }), {
      target: { value: 'Topstep' },
    });
    fireEvent.click(screen.getByRole('button', { name: /create firm/i }));
    fireEvent.click(await screen.findByRole('button', { name: /declare conventions/i }));

    await screen.findByLabelText(/credential key/i, { exact: false });
    expect(screen.getByText(/not the credentials/i)).toBeTruthy();
  });

  it('shows the venue credential schema on the connection step — ApiKey labelled Username, ApiSecret marked secret', async () => {
    createFirmMock.mockResolvedValue({ ok: true, data: PROP_FIRM });
    declareMock.mockResolvedValue({ ok: true, data: PROP_FIRM });

    renderWithProviders(<FirmOnboarding onComplete={vi.fn()} />);

    fireEvent.change(screen.getByLabelText(/firm name/i, { exact: false }), {
      target: { value: 'Topstep' },
    });
    fireEvent.click(screen.getByRole('button', { name: /create firm/i }));
    fireEvent.click(await screen.findByRole('button', { name: /declare conventions/i }));
    await screen.findByLabelText(/credential key/i, { exact: false });

    // The contract fetched from GET /venues/setup is rendered where the operator wires the login, so the naming
    // trap (ApiKey is really the username) is a label they read, not folklore in an env comment.
    const guidance = await screen.findByTestId('venue-credential-guidance');
    expect(within(guidance).getByText('Username')).toBeTruthy();
    expect(within(guidance).getByText('API key')).toBeTruthy();
    expect(
      within(within(guidance).getByTestId('venue-credential-ApiSecret')).getByText('secret'),
    ).toBeTruthy();
  });

  it('still lets the operator connect when the venue setup fetch fails — the guidance is a reference, not a gate', async () => {
    getVenueSetupMock.mockResolvedValue({ ok: false, kind: 'failed', error: 'boom' });
    createFirmMock.mockResolvedValue({ ok: true, data: PROP_FIRM });
    declareMock.mockResolvedValue({ ok: true, data: PROP_FIRM });
    createConnectionMock.mockResolvedValue({ ok: true, data: CONNECTION });

    renderWithProviders(<FirmOnboarding onComplete={vi.fn()} />);

    fireEvent.change(screen.getByLabelText(/firm name/i, { exact: false }), {
      target: { value: 'Topstep' },
    });
    fireEvent.click(screen.getByRole('button', { name: /create firm/i }));
    fireEvent.click(await screen.findByRole('button', { name: /declare conventions/i }));
    fireEvent.change(await screen.findByLabelText(/credential key/i, { exact: false }), {
      target: { value: 'topstep-main' },
    });

    // No guidance rendered (the fetch failed), but the walk still proceeds — credentials are set server-side, so
    // the guidance is a reference, never a gate (ADR-0015).
    expect(screen.queryByTestId('venue-credential-guidance')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /add connection/i }));
    await waitFor(() => expect(createConnectionMock).toHaveBeenCalled());
  });

  it('guards the create button while the firm POST is in flight, so a double-click fires one create', async () => {
    const pending = deferred<Awaited<ReturnType<typeof createFirm>>>();
    createFirmMock.mockReturnValue(pending.promise);

    renderWithProviders(<FirmOnboarding onComplete={vi.fn()} />);

    fireEvent.change(screen.getByLabelText(/firm name/i, { exact: false }), {
      target: { value: 'Topstep' },
    });
    const create = screen.getByRole('button', { name: /create firm/i }) as HTMLButtonElement;
    fireEvent.click(create);

    expect(create.disabled).toBe(true);
    fireEvent.click(create);
    expect(createFirmMock).toHaveBeenCalledTimes(1);

    pending.resolve({ ok: true, data: PROP_FIRM });
    await screen.findByRole('button', { name: /declare conventions/i });
  });
});
