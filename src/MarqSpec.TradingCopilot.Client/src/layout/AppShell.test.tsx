import { cleanup, render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { ALL_WINDOW_SIZE_CLASSES, setWindowSizeClass } from '../testing/viewport';
import { AppShell, CONTENT_REGION_TEST_ID } from './AppShell';

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

  it.each(ALL_WINDOW_SIZE_CLASSES)('reserves both safety slots while empty at %s', (sizeClass) => {
    // The contents are gh#25's. The frame has to exist first, or the controls appear one day and shift
    // everything around them -- and an operator who has learned where the kill switch is finds it moved.
    setWindowSizeClass(sizeClass);

    renderShell(<Surface />);

    const region = safetyRegion();
    const timeToFlat = region.querySelector('[data-safety-slot="time-to-flat"]');
    const killSwitch = region.querySelector('[data-safety-slot="kill-switch"]');

    expect(timeToFlat).toBeTruthy();
    expect(killSwitch).toBeTruthy();
    expect(timeToFlat?.getAttribute('data-filled')).toBe('false');
    expect(killSwitch?.getAttribute('data-filled')).toBe('false');
  });

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
