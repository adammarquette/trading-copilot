import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiResult } from '../api/client';
import { type Trigger, confirmTrigger, deleteTrigger, listTriggers } from '../api/triggers';
import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { TriggersSurface } from './TriggersSurface';

vi.mock('../api/triggers', async (importOriginal) => {
  // The enum tables and `willFire` are the production ones on purpose: they encode the confirm-before-live rule
  // this surface exists to render, and a test that redefined them could assert the wrong rule and still pass.
  const actual = await importOriginal<typeof import('../api/triggers')>();
  return {
    ...actual,
    listTriggers: vi.fn(),
    confirmTrigger: vi.fn(),
    deleteTrigger: vi.fn(),
  };
});

const listMock = vi.mocked(listTriggers);
const confirmMock = vi.mocked(confirmTrigger);
const deleteMock = vi.mocked(deleteTrigger);

function trigger(overrides: Partial<Trigger> = {}): Trigger {
  return {
    id: 't-1',
    symbol: 'ES',
    indicator: 'rsi',
    period: 14,
    resolutionMinutes: 5,
    comparison: 2, // Above
    threshold: 70,
    hysteresis: null,
    route: 1,
    accountId: null,
    size: null,
    severity: 1,
    enabled: true,
    confirmation: 1, // Confirmed
    armState: 1,
    armCycle: 0,
    lastEvaluatedValue: null,
    lastFiredAt: null,
    createdAt: '2026-08-18T00:00:00Z',
    ...overrides,
  };
}

function renderSurface() {
  return render(
    <ThemeModeProvider initialMode="dark">
      <TriggersSurface />
    </ThemeModeProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(cleanup);

describe('TriggersSurface', () => {
  it('renders a confirmed, enabled trigger as Live with its condition', async () => {
    listMock.mockResolvedValue({ ok: true, data: [trigger()] } satisfies ApiResult<Trigger[]>);

    renderSurface();

    expect(await screen.findByText('Live')).toBeTruthy();
    expect(screen.getByText('ES rsi(14) 5m above 70')).toBeTruthy();
  });

  it('an ENABLED but UNCONFIRMED trigger says it will not fire — the whole point (gh#991)', async () => {
    // The combination the surface exists for. `enabled: true` with `confirmation: Unconfirmed` is inert, and an
    // operator reading two equal-weight badges would reasonably conclude the opposite and wait on an alert that
    // can never arrive (ADR-0019's silent-monitor class). Asserted on the WORDS, not on a colour or a chip
    // variant: a styling change must not be able to quietly remove the warning.
    listMock.mockResolvedValue({
      ok: true,
      data: [trigger({ enabled: true, confirmation: 0 })],
    } satisfies ApiResult<Trigger[]>);

    renderSurface();

    expect(await screen.findByText('Will not fire')).toBeTruthy();
    expect(screen.getByText(/never confirmed/i)).toBeTruthy();
    expect(screen.queryByText('Live')).toBeNull();
  });

  it('confirming replaces the row with what the server returned, not an assumed state', async () => {
    listMock.mockResolvedValue({
      ok: true,
      data: [trigger({ enabled: true, confirmation: 0 })],
    } satisfies ApiResult<Trigger[]>);
    // The server is the authority: it answers confirmed AND switched off, which is not what a client that just
    // flipped `confirmation` would have produced. The row must show the server's answer.
    confirmMock.mockResolvedValue({
      ok: true,
      data: trigger({ enabled: false, confirmation: 1 }),
    } satisfies ApiResult<Trigger>);

    renderSurface();
    fireEvent.click(await screen.findByRole('button', { name: /^Confirm / }));

    expect(await screen.findByText(/switched off/i)).toBeTruthy();
    expect(screen.queryByText('Live')).toBeNull();
  });

  it('a failed confirm surfaces the reason and leaves the row unchanged', async () => {
    listMock.mockResolvedValue({
      ok: true,
      data: [trigger({ enabled: true, confirmation: 0 })],
    } satisfies ApiResult<Trigger[]>);
    confirmMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'That trigger was edited since you loaded it.',
    } satisfies ApiResult<Trigger>);

    renderSurface();
    fireEvent.click(await screen.findByRole('button', { name: /^Confirm / }));

    expect((await screen.findByRole('alert')).textContent).toBe(
      'That trigger was edited since you loaded it.',
    );
    // Still inert, and still offering the action — a failed confirm must never read as a successful one.
    expect(screen.getByText('Will not fire')).toBeTruthy();
    expect(screen.getByRole('button', { name: /^Confirm / })).toBeTruthy();
  });

  it('a THROW out of confirm does not strand the row busy (gh#951 family)', async () => {
    listMock.mockResolvedValue({
      ok: true,
      data: [trigger({ enabled: true, confirmation: 0 })],
    } satisfies ApiResult<Trigger[]>);
    confirmMock.mockRejectedValue(new Error('boom'));

    renderSurface();
    fireEvent.click(await screen.findByRole('button', { name: /^Confirm / }));

    expect(await screen.findByRole('alert')).toBeTruthy();
    await waitFor(() =>
      expect(
        (screen.getByRole('button', { name: /^Confirm / }) as HTMLButtonElement).disabled,
      ).toBe(false),
    );
  });

  it('deleting removes the row', async () => {
    listMock.mockResolvedValue({ ok: true, data: [trigger()] } satisfies ApiResult<Trigger[]>);
    deleteMock.mockResolvedValue({ ok: true, data: undefined });

    renderSurface();
    fireEvent.click(await screen.findByRole('button', { name: /^Delete / }));

    await waitFor(() => expect(screen.queryByTestId('trigger-row')).toBeNull());
  });

  it('a FAILED delete keeps the row — never reports a live trigger as gone (gh#993 review)', async () => {
    // The gap the #993 review found: the suite passed with the row filter above the ok-guard, so a failed delete
    // could remove the row and tell the operator a trigger is gone while it is still standing at the server and
    // still able to fire. Worse than a stuck spinner — the operator stops expecting an alert that can still come.
    listMock.mockResolvedValue({ ok: true, data: [trigger()] } satisfies ApiResult<Trigger[]>);
    deleteMock.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 500,
      error: 'The trigger could not be deleted.',
    });

    renderSurface();
    fireEvent.click(await screen.findByRole('button', { name: /^Delete / }));

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.getByTestId('trigger-row')).toBeTruthy();
    expect(screen.getByText('ES rsi(14) 5m above 70')).toBeTruthy();
  });

  it('a THROW out of delete also keeps the row', async () => {
    listMock.mockResolvedValue({ ok: true, data: [trigger()] } satisfies ApiResult<Trigger[]>);
    deleteMock.mockRejectedValue(new Error('boom'));

    renderSurface();
    fireEvent.click(await screen.findByRole('button', { name: /^Delete / }));

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.getByTestId('trigger-row')).toBeTruthy();
  });

  it('acts on the row whose button was pressed, with several rows on screen', async () => {
    // Per-row actions need per-row handles. Without the aria-label the two Delete buttons are indistinguishable
    // to an assistive client and to this test, and a wrong-row delete would look identical to a right-row one.
    listMock.mockResolvedValue({
      ok: true,
      data: [
        trigger({ id: 'a', symbol: 'ES' }),
        trigger({ id: 'b', symbol: 'NQ', enabled: true, confirmation: 0 }),
      ],
    } satisfies ApiResult<Trigger[]>);
    deleteMock.mockResolvedValue({ ok: true, data: undefined });

    renderSurface();
    fireEvent.click(await screen.findByRole('button', { name: 'Delete NQ rsi(14) 5m above 70' }));

    await waitFor(() => expect(screen.queryByText('NQ rsi(14) 5m above 70')).toBeNull());
    expect(screen.getByText('ES rsi(14) 5m above 70')).toBeTruthy();
    expect(deleteMock).toHaveBeenCalledWith('b');
  });

  it('shows an empty state that says a new trigger does not fire until confirmed', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] } satisfies ApiResult<Trigger[]>);

    renderSurface();

    expect(await screen.findByTestId('empty-state')).toBeTruthy();
    expect(screen.getByText(/does not fire until you confirm it/i)).toBeTruthy();
  });

  it('a failed load offers a retry that reloads', async () => {
    listMock
      .mockResolvedValueOnce({
        ok: false,
        kind: 'failed',
        error: 'The request could not be sent.',
      } satisfies ApiResult<Trigger[]>)
      .mockResolvedValueOnce({ ok: true, data: [trigger()] } satisfies ApiResult<Trigger[]>);

    renderSurface();
    fireEvent.click(await screen.findByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('Live')).toBeTruthy();
  });

  it('a THROW out of the load renders an error rather than a permanent spinner', async () => {
    listMock.mockRejectedValue(new Error('boom'));

    renderSurface();

    expect(await screen.findByTestId('triggers-error')).toBeTruthy();
    expect(screen.queryByTestId('loading-state')).toBeNull();
  });
});
