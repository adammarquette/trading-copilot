import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { App } from './App';
import { destinations } from './navigation/destinations';
import { ALL_WINDOW_SIZE_CLASSES, setWindowSizeClass } from './testing/viewport';

// The signed-in shell mounts a RealtimeProvider, which would otherwise open a real SignalR socket against a URL
// jsdom cannot resolve. Stub the transport at its seam — the same posture as the `/health` fetch stub below — so
// these tests stay about the shell, not the wire. The connection's own behaviour is covered in `realtime/`.
vi.mock('./realtime/connection', () => ({
  createRealtimeConnection: () => ({
    start: () => Promise.resolve(),
    stop: () => Promise.resolve(),
    state: 'down',
  }),
}));

// The workspace's MarketChart mounts a real Lightweight-Charts canvas jsdom cannot render. Stub it at its seam too,
// the same posture, so the shell tests stay about the shell; the chart's own behaviour is covered in `chart/`.
vi.mock('lightweight-charts', () => ({
  createChart: () => ({
    addSeries: () => ({ setData: () => {} }),
    remove: () => {},
    applyOptions: () => {},
    timeScale: () => ({ fitContent: () => {} }),
  }),
  CandlestickSeries: 'Candlestick',
}));

/**
 * `App` brings its own theme provider and, since gh#652, its own session provider; only the router is
 * missing, because production supplies a `BrowserRouter` in `main.tsx` and a test wants a `MemoryRouter`.
 *
 * The app now boots through the R-18 gate: on load it probes `GET /auth/me` to decide signed-in vs
 * signed-out, and shows a brief loading state until it answers. So these tests choose who the operator
 * is by choosing what `/auth/me` returns, and wait for the gate to settle before asserting.
 */
const OPERATOR = { id: 'op-1', email: 'operator@local', displayName: 'Operator' } as const;

/** A Response stand-in the client reads via `.text()` — the same shape `api/client` parses. */
function jsonResponse(status: number, body?: unknown): Response {
  const text = body === undefined ? '' : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(text),
  } as unknown as Response;
}

/**
 * Answers `/auth/me` with `me`; leaves every other call (the shell's `/health` probe) pending, exactly as
 * this suite did before the gate existed — an unstubbed probe would reject against jsdom's absent network
 * and colour these tests with an unrelated failure.
 */
function stubAppFetch(me: () => Promise<Response>) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      return url.endsWith('/auth/me') ? me() : new Promise<Response>(() => {});
    }),
  );
}

/**
 * Renders the app and waits for the gate to resolve the session. The loading state carries no heading; the
 * shell's `h1` appears only once the session is established, so awaiting it steps past the loading flash.
 */
async function renderApp(route = '/') {
  const result = render(
    <MemoryRouter initialEntries={[route]}>
      <App />
    </MemoryRouter>,
  );
  await screen.findByRole('heading', { level: 1 });
  return result;
}

beforeEach(() => {
  window.localStorage.clear();
  setWindowSizeClass('expanded');
  // Signed in by default: the shell tests are about the signed-in app. The gate tests below override this.
  stubAppFetch(() => Promise.resolve(jsonResponse(200, OPERATOR)));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('App', () => {
  it('renders the application name', async () => {
    await renderApp();

    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Trading Co-Pilot');
  });

  it('opens on the workspace, because the chart is the central surface', async () => {
    // R-10 / ADR-0004. The root route is the workspace on purpose; anything else makes the operator
    // navigate to the thing they came for.
    await renderApp();

    expect(screen.getByTestId('surface').dataset.surface).toBe('workspace');
  });

  it.each(destinations.map((destination) => [destination.id, destination.path] as const))(
    'routes %s to its own surface',
    async (id, path) => {
      // Routes and navigation are generated from one table, so this also proves no destination links
      // somewhere that does not exist.
      await renderApp(path);

      expect(screen.getByTestId('surface').dataset.surface).toBe(id);
    },
  );

  it('navigates between surfaces without leaving the shell', async () => {
    await renderApp();

    fireEvent.click(screen.getByRole('link', { name: 'Journal' }));

    expect(screen.getByTestId('surface').dataset.surface).toBe('journal');
    // The shell -- and with it the safety region -- survives navigation rather than remounting.
    expect(screen.getByRole('region', { name: 'Safety controls' })).toBeTruthy();
  });

  it('keeps the operator inside the shell on an unknown path', async () => {
    // A 404 is not a reason to drop the kill switch and the countdown.
    await renderApp('/no-such-surface');

    expect(screen.getByRole('heading', { level: 2 }).textContent).toBe('No such surface');
    expect(screen.getByRole('region', { name: 'Safety controls' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Back to the workspace' })).toBeTruthy();
  });

  it.each(ALL_WINDOW_SIZE_CLASSES)('renders a complete shell at %s', async (sizeClass) => {
    setWindowSizeClass(sizeClass);

    await renderApp();

    expect(screen.getByRole('region', { name: 'Safety controls' })).toBeTruthy();
    expect(screen.getByTestId('mode-chip-slot')).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Primary' })).toBeTruthy();
    expect(screen.getByTestId('surface')).toBeTruthy();
  });
});

describe('App — the R-18 authorization gate', () => {
  it('shows sign-in, not the shell, when there is no session', async () => {
    // Anonymous access is refused: the first surface is sign-in, and the safety controls are not on it.
    stubAppFetch(() => Promise.resolve(jsonResponse(401)));

    render(
      <MemoryRouter initialEntries={['/']}>
        <App />
      </MemoryRouter>,
    );

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in' })).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Safety controls' })).toBeNull();
    expect(screen.queryByTestId('surface')).toBeNull();
    // The credential surface carries no account state — nothing to shoulder-surf beside the password field.
    expect(screen.queryByRole('button', { name: 'Account' })).toBeNull();
  });

  it('sends a deep link through sign-in when the session is gone, not to a broken app', async () => {
    // Acceptance: an expired token routes back to sign-in rather than showing a broken app.
    stubAppFetch(() => Promise.resolve(jsonResponse(401)));

    render(
      <MemoryRouter initialEntries={['/journal']}>
        <App />
      </MemoryRouter>,
    );

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in' })).toBeTruthy();
    expect(screen.queryByTestId('surface')).toBeNull();
  });
});
