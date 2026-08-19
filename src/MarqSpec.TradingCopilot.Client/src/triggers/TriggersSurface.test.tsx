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

  it('deleting removes the row', async () => {
    listMock.mockResolvedValue({ ok: true, data: [trigger()] } satisfies ApiResult<Trigger[]>);
    deleteMock.mockResolvedValue({ ok: true, data: undefined });

    renderSurface();
    fireEvent.click(await screen.findByRole('button', { name: /^Delete / }));

    await waitFor(() => expect(screen.queryByTestId('trigger-row')).toBeNull());
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

  it('confirming updates ONLY the row acted on, with several rows on screen (gh#993 review)', async () => {
    // The delete bug, one handler over: `map(() => updated)` writes the confirmed trigger over every row, so a
    // second, still-unconfirmed trigger would render as Live and the operator would rely on an alert that cannot
    // fire. Found because the reviewer mutated the sibling of a handler I had just fixed rather than the one.
    listMock.mockResolvedValue({
      ok: true,
      data: [
        trigger({ id: 'a', symbol: 'ES', enabled: true, confirmation: 0 }),
        trigger({ id: 'b', symbol: 'NQ', enabled: true, confirmation: 0 }),
      ],
    } satisfies ApiResult<Trigger[]>);
    confirmMock.mockResolvedValue({
      ok: true,
      data: trigger({ id: 'a', symbol: 'ES', enabled: true, confirmation: 1 }),
    } satisfies ApiResult<Trigger>);

    renderSurface();
    fireEvent.click(await screen.findByRole('button', { name: 'Confirm ES rsi(14) 5m above 70' }));

    // The confirmed one goes Live; the untouched one must still say it will not fire.
    expect(await screen.findByText('Live')).toBeTruthy();
    expect(screen.getAllByText('Will not fire')).toHaveLength(1);
    expect(confirmMock).toHaveBeenCalledWith('a');
  });

  it('a trigger that is neither confirmed nor switched on says both, not just the switch', async () => {
    // The fourth quadrant. If this branch claimed the trigger were confirmed, the operator would flip the switch
    // and wait on an alert that still cannot arrive — a wrong next step, not merely an incomplete message.
    listMock.mockResolvedValue({
      ok: true,
      data: [trigger({ enabled: false, confirmation: 0 })],
    } satisfies ApiResult<Trigger[]>);

    renderSurface();

    expect(await screen.findByText('Will not fire')).toBeTruthy();
    expect(screen.getByText(/Not confirmed, and switched off/i)).toBeTruthy();
  });

  // The two row actions are MIRROR handlers, and the gh#993 review found three defects in a row that existed on
  // exactly one side: the assertion set was being carried across by hand, one property at a time, and decayed at
  // the rate new properties were added. The third was found by injecting one defect into each half — the confirm
  // side reddened, the delete side did not.
  //
  // So the properties that must hold for BOTH are a table, not two hand-kept copies. Adding a row here covers
  // every handler by construction. That matters more for the authoring increment than for this one: create and
  // enable/disable take the pair to four, and a pair already asymmetric at two will be worse at four.
  const ROW_ACTIONS = [
    { action: 'confirm' as const, button: /^Confirm /, mockOf: () => confirmMock },
    { action: 'delete' as const, button: /^Delete /, mockOf: () => deleteMock },
  ];

  describe.each(ROW_ACTIONS)(
    '$action — the invariants both row actions share',
    ({ button, mockOf }) => {
      it('a refusal surfaces its reason and leaves the row standing', async () => {
        // A row that vanishes, or silently stays, on a refused action tells the operator something untrue about a
        // trigger that is still exactly as it was.
        listMock.mockResolvedValue({
          ok: true,
          data: [trigger({ enabled: true, confirmation: 0 })],
        } satisfies ApiResult<Trigger[]>);
        mockOf().mockResolvedValue({
          ok: false,
          kind: 'refused',
          status: 409,
          reason: 'That trigger was edited since you loaded it.',
        });

        renderSurface();
        fireEvent.click(await screen.findByRole('button', { name: button }));

        expect((await screen.findByRole('alert')).textContent).toBe(
          'That trigger was edited since you loaded it.',
        );
        expect(screen.getByTestId('trigger-row')).toBeTruthy();
        expect(screen.getByText('Will not fire')).toBeTruthy();
      });

      it('a THROW leaves the row present and its buttons usable — never stranded busy', async () => {
        // `busyId` gates BOTH buttons on the row, so a catch that forgets to clear it kills Confirm and Delete
        // together until a reload. That is the gh#951 / gh#973 stranding family, at row scope.
        listMock.mockResolvedValue({
          ok: true,
          data: [trigger({ enabled: true, confirmation: 0 })],
        } satisfies ApiResult<Trigger[]>);
        mockOf().mockRejectedValue(new Error('boom'));

        renderSurface();
        fireEvent.click(await screen.findByRole('button', { name: button }));

        expect(await screen.findByRole('alert')).toBeTruthy();
        expect(screen.getByTestId('trigger-row')).toBeTruthy();
        await waitFor(() => {
          expect(
            (screen.getByRole('button', { name: /^Confirm / }) as HTMLButtonElement).disabled,
          ).toBe(false);
          expect(
            (screen.getByRole('button', { name: /^Delete / }) as HTMLButtonElement).disabled,
          ).toBe(false);
        });
      });
    },
  );

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
