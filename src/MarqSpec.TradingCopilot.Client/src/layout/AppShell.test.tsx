import { cleanup, render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { ALL_WINDOW_SIZE_CLASSES, setWindowSizeClass } from '../testing/viewport';
import { AppShell, CONTENT_REGION_TEST_ID } from './AppShell';

// The safety controls read from the server on mount. This suite is about the SHELL -- that both controls are
// present, at every size class, off the scrolling axis -- so the transport seam is stubbed rather than the
// components: stubbing the components themselves would let the shell stop passing one in and still pass here.
// Both reads refuse, which is the honest offline answer and the state each control renders as an explicit
// "unavailable" rather than a fabricated one.
vi.mock('../api/flatten', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/flatten')>()),
  getFlattenSchedule: vi.fn(() =>
    Promise.resolve({ ok: false, kind: 'failed' as const, status: 503, error: 'not under test' }),
  ),
}));

vi.mock('../api/protection', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/protection')>()),
  getProtectionState: vi.fn(() =>
    Promise.resolve({ ok: false, kind: 'failed' as const, status: 503, error: 'not under test' }),
  ),
}));

vi.mock('../api/killSwitch', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/killSwitch')>()),
  getKillSwitch: vi.fn(() =>
    Promise.resolve({ ok: false, kind: 'failed' as const, status: 503, error: 'not under test' }),
  ),
}));

/** Mounts the shell with a chosen surface underneath it, the way the real route table does. */
function renderShell(surface: ReactNode) {
  return render(
    <ThemeModeProvider initialMode="dark">
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route element={<AppShell />}>
            <Route index element={surface} />
          </Route>
        </Routes>
      </MemoryRouter>
    </ThemeModeProvider>,
  );
}

function Surface() {
  return <p>A surface</p>;
}

function ThrowingSurface(): never {
  throw new Error('the chart pane exploded');
}

function safetyRegion() {
  return screen.getByRole('region', { name: 'Safety controls' });
}

beforeEach(() => {
  window.localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('AppShell safety region', () => {
  it.each(ALL_WINDOW_SIZE_CLASSES)('renders the safety region at %s', (sizeClass) => {
    // ADR-0005: the time-to-flat countdown (R-13) and the kill switch are persistent at *every*
    // breakpoint. A responsive layout that drops them on a phone is the failure this catches.
    setWindowSizeClass(sizeClass);

    renderShell(<Surface />);

    expect(screen.getByTestId('app-shell').dataset.sizeClass).toBe(sizeClass);
    expect(safetyRegion()).toBeTruthy();
  });

  it.each(ALL_WINDOW_SIZE_CLASSES)('fills both safety slots at %s', (sizeClass) => {
    // gh#657 filled the frame gh#23 reserved. This assertion used to read `data-filled === 'false'`, which was
    // right while the contents were unbuilt and is now the regression that matters: the countdown (R-13) and the
    // kill switch (R-11) are present at EVERY size class. A responsive layout that drops either on a phone --
    // or a wiring change that quietly stops passing one in -- fails here.
    setWindowSizeClass(sizeClass);

    renderShell(<Surface />);

    const region = safetyRegion();
    const timeToFlat = region.querySelector('[data-safety-slot="time-to-flat"]');
    const killSwitch = region.querySelector('[data-safety-slot="kill-switch"]');

    expect(timeToFlat?.getAttribute('data-filled')).toBe('true');
    expect(killSwitch?.getAttribute('data-filled')).toBe('true');
    expect(timeToFlat?.querySelector('[data-testid="time-to-flat"]')).toBeTruthy();
    expect(killSwitch?.querySelector('[data-testid="kill-switch"]')).toBeTruthy();
  });

  it.each(ALL_WINDOW_SIZE_CLASSES)(
    'carries the degraded-protection indicator at %s',
    (sizeClass) => {
      // gh#222. It sits ahead of the two slots and outside their chrome -- an alert, not a labelled control. Its
      // presence at EVERY size class is the point: R-11's "alerted immediately" is not satisfied by a warning that
      // a narrow viewport drops, and this is the one strip element that reports a position may be less protected
      // than the operator believes.
      setWindowSizeClass(sizeClass);

      renderShell(<Surface />);

      expect(safetyRegion().querySelector('[data-testid="protection-status"]')).toBeTruthy();
    },
  );

  it.each(ALL_WINDOW_SIZE_CLASSES)(
    'keeps the safety region off the scrolling axis at %s',
    (sizeClass) => {
      // "Cannot be scrolled away" is a structural claim, so it gets a structural test: the shell scrolls
      // exactly one region, and the safety controls are not inside it. Testing a CSS `position` value
      // instead would pass while a parent's `overflow` quietly re-introduced a second scroll container.
      setWindowSizeClass(sizeClass);

      renderShell(<Surface />);

      const content = screen.getByTestId(CONTENT_REGION_TEST_ID);

      expect(content.contains(safetyRegion())).toBe(false);
    },
  );

  it('renders exactly one safety region, so there is no second one to hide', () => {
    setWindowSizeClass('expanded');

    renderShell(<Surface />);

    expect(screen.getAllByRole('region', { name: 'Safety controls' })).toHaveLength(1);
  });

  it('keeps the safety region mounted when the routed surface throws', () => {
    // The reason the error boundary sits around the outlet rather than around the whole app. A broken
    // pane must not be able to take the kill switch with it.
    setWindowSizeClass('expanded');
    // React logs the caught error itself; silencing it keeps a passing run readable, and the assertions
    // below -- not the log -- are what prove the throw was handled.
    vi.spyOn(console, 'error').mockImplementation(() => {});

    render(
      <ThemeModeProvider initialMode="dark">
        <MemoryRouter initialEntries={['/']}>
          <Routes>
            <Route element={<AppShell />}>
              <Route index element={<ThrowingSurface />} />
            </Route>
          </Routes>
        </MemoryRouter>
      </ThemeModeProvider>,
    );

    expect(screen.getByTestId('error-boundary-fallback')).toBeTruthy();
    expect(safetyRegion()).toBeTruthy();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Trading Co-Pilot');
  });
});

describe('AppShell app bar', () => {
  it.each(ALL_WINDOW_SIZE_CLASSES)(
    'reserves the practice/live mode chip slot at %s',
    (sizeClass) => {
      // R-14. ADR-0005 puts the mode chip in the app bar at every breakpoint; the shell owes it a place
      // even before there is a chip to put there.
      setWindowSizeClass(sizeClass);

      renderShell(<Surface />);

      const slot = screen.getByTestId('mode-chip-slot');

      expect(slot.dataset.filled).toBe('false');
      expect(screen.getByTestId(CONTENT_REGION_TEST_ID).contains(slot)).toBe(false);
    },
  );

  it('offers the theme toggle at every size class', () => {
    for (const sizeClass of ALL_WINDOW_SIZE_CLASSES) {
      setWindowSizeClass(sizeClass);
      renderShell(<Surface />);

      expect(screen.getByRole('button', { name: /Switch to (light|dark) theme/ })).toBeTruthy();

      cleanup();
    }
  });
});
