import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiResult } from '../api/client';
import { type Trigger, createTrigger } from '../api/triggers';
import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { TriggerForm } from './TriggerForm';

vi.mock('../api/triggers', async (importOriginal) => {
  // Production enum tables and `willFire` on purpose: they encode the confirm-before-live rule, and a test that
  // redefined them could assert the wrong rule and still pass.
  const actual = await importOriginal<typeof import('../api/triggers')>();
  return { ...actual, createTrigger: vi.fn() };
});

const createMock = vi.mocked(createTrigger);

function created(overrides: Partial<Trigger> = {}): Trigger {
  return {
    id: 'new-1',
    symbol: 'ES',
    indicator: 'rsi',
    period: 14,
    resolutionMinutes: 5,
    comparison: 2,
    threshold: 70,
    hysteresis: null,
    route: 1,
    accountId: null,
    size: null,
    severity: 1,
    enabled: true,
    confirmation: 0, // the server's default: authored is NOT armed
    armState: 0,
    armCycle: 0,
    lastEvaluatedValue: null,
    lastFiredAt: null,
    createdAt: '2026-08-19T00:00:00Z',
    ...overrides,
  };
}

function renderForm(onCreated = vi.fn()) {
  render(
    <ThemeModeProvider initialMode="dark">
      <TriggerForm onCreated={onCreated} />
    </ThemeModeProvider>,
  );
  return { onCreated };
}

/** Fills the fields every valid mechanical trigger needs. */
function fillMinimum() {
  fireEvent.change(screen.getByLabelText(/Instrument/i), { target: { value: 'ES' } });
  fireEvent.change(screen.getByLabelText(/Period/i), { target: { value: '14' } });
  fireEvent.change(screen.getByLabelText(/Bar size/i), { target: { value: '5' } });
  fireEvent.change(screen.getByLabelText(/Threshold/i), { target: { value: '70' } });
}

/** MUI renders a select as a combobox, not a native <select>, so it is driven the way an operator drives it. */
function selectRoute(option: string) {
  fireEvent.mouseDown(screen.getByLabelText(/Route/i));
  fireEvent.click(screen.getByRole('option', { name: option }));
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(cleanup);

describe('TriggerForm', () => {
  it('creates a mechanical trigger from the minimum fields', async () => {
    createMock.mockResolvedValue({ ok: true, data: created() } satisfies ApiResult<Trigger>);
    const { onCreated } = renderForm();

    fillMinimum();
    fireEvent.click(screen.getByRole('button', { name: 'Create trigger' }));

    await waitFor(() => expect(createMock).toHaveBeenCalledTimes(1));
    const sent = createMock.mock.calls[0][0];
    expect(sent.symbol).toBe('ES');
    expect(sent.period).toBe(14);
    expect(sent.resolutionMinutes).toBe(5);
    expect(sent.threshold).toBe(70);
    expect(onCreated).toHaveBeenCalledWith(created());
  });

  it('says creating is not arming — the created trigger will not fire yet (gh#1003)', async () => {
    // The safety continuity from gh#991. The server defaults a new trigger to Unconfirmed, so authoring must not
    // read as arming: the operator has to learn that a second, deliberate act is still required. Asserted on the
    // words, since that is what the operator acts on.
    createMock.mockResolvedValue({
      ok: true,
      data: created({ confirmation: 0 }),
    } satisfies ApiResult<Trigger>);
    renderForm();

    fillMinimum();
    fireEvent.click(screen.getByRole('button', { name: 'Create trigger' }));

    expect(await screen.findByText(/will not fire until you confirm it/i)).toBeTruthy();
  });

  it('requires an account and a size once the route is agent review', async () => {
    // Two fields become mandatory because of a third. The server refuses either omission, but the operator should
    // not have to discover that by submitting.
    renderForm();

    fillMinimum();
    selectRoute('agent review');
    fireEvent.click(screen.getByRole('button', { name: 'Create trigger' }));

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(createMock).not.toHaveBeenCalled();
  });

  it('a mechanical trigger does not demand an account or a size', async () => {
    // The control for the case above: without it, a form that demanded them ALWAYS would pass that test.
    createMock.mockResolvedValue({ ok: true, data: created() } satisfies ApiResult<Trigger>);
    renderForm();

    fillMinimum();
    fireEvent.click(screen.getByRole('button', { name: 'Create trigger' }));

    await waitFor(() => expect(createMock).toHaveBeenCalledTimes(1));
  });

  it("renders the server's refusal in its own words, and keeps what was typed", async () => {
    // The server owns the vocabulary of its refusals (ADR-0003 maps a 4xx `{error}` to `refused`). Re-typing a
    // form because the server said no is a punishment for the server's opinion.
    createMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'The hysteresis band must be positive when set — null means none.',
    } satisfies ApiResult<Trigger>);
    renderForm();

    fillMinimum();
    fireEvent.click(screen.getByRole('button', { name: 'Create trigger' }));

    expect((await screen.findByRole('alert')).textContent).toBe(
      'The hysteresis band must be positive when set — null means none.',
    );
    expect((screen.getByLabelText(/Instrument/i) as HTMLInputElement).value).toBe('ES');
  });

  it('a THROW out of create leaves the form usable — no dead submit button', async () => {
    // The gh#951 / gh#954 / gh#963 / gh#973 family, on a new path.
    createMock.mockRejectedValue(new Error('boom'));
    renderForm();

    fillMinimum();
    fireEvent.click(screen.getByRole('button', { name: 'Create trigger' }));

    expect(await screen.findByRole('alert')).toBeTruthy();
    await waitFor(() =>
      expect(
        (screen.getByRole('button', { name: 'Create trigger' }) as HTMLButtonElement).disabled,
      ).toBe(false),
    );
  });
});
