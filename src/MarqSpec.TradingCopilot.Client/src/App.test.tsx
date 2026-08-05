import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { App } from './App';
import { destinations } from './navigation/destinations';
import { ALL_WINDOW_SIZE_CLASSES, setWindowSizeClass } from './testing/viewport';

/**
 * `App` brings its own theme provider; only the router is missing, because production supplies a
 * `BrowserRouter` in `main.tsx` and a test wants a `MemoryRouter`. That split is the whole reason `App`
 * does not carry a router of its own.
 */
function renderApp(route = '/') {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <App />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  window.localStorage.clear();
  setWindowSizeClass('expanded');
  // Every surface renders inside the shell, and the shell probes /health. Left unstubbed the probe
  // would reject against jsdom's absent network and colour these tests with an unrelated failure.
  vi.stubGlobal(
    'fetch',
    vi.fn(() => new Promise<Response>(() => {})),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('App', () => {
  it('renders the application name', () => {
    renderApp();

    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Trading Co-Pilot');
  });

  it('opens on the workspace, because the chart is the central surface', () => {
    // R-10 / ADR-0004. The root route is the workspace on purpose; anything else makes the operator
    // navigate to the thing they came for.
    renderApp();

    expect(screen.getByTestId('surface').dataset.surface).toBe('workspace');
  });

  it.each(destinations.map((destination) => [destination.id, destination.path] as const))(
    'routes %s to its own surface',
    (id, path) => {
      // Routes and navigation are generated from one table, so this also proves no destination links
      // somewhere that does not exist.
      renderApp(path);

      expect(screen.getByTestId('surface').dataset.surface).toBe(id);
    },
  );

  it('navigates between surfaces without leaving the shell', () => {
    renderApp();

    fireEvent.click(screen.getByRole('link', { name: 'Journal' }));

    expect(screen.getByTestId('surface').dataset.surface).toBe('journal');
    // The shell -- and with it the safety region -- survives navigation rather than remounting.
    expect(screen.getByRole('region', { name: 'Safety controls' })).toBeTruthy();
  });

  it('keeps the operator inside the shell on an unknown path', () => {
    // A 404 is not a reason to drop the kill switch and the countdown.
    renderApp('/no-such-surface');

    expect(screen.getByRole('heading', { level: 2 }).textContent).toBe('No such surface');
    expect(screen.getByRole('region', { name: 'Safety controls' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Back to the workspace' })).toBeTruthy();
  });

  it.each(ALL_WINDOW_SIZE_CLASSES)('renders a complete shell at %s', (sizeClass) => {
    setWindowSizeClass(sizeClass);

    renderApp();

    expect(screen.getByRole('region', { name: 'Safety controls' })).toBeTruthy();
    expect(screen.getByTestId('mode-chip-slot')).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Primary' })).toBeTruthy();
    expect(screen.getByTestId('surface')).toBeTruthy();
  });
});
