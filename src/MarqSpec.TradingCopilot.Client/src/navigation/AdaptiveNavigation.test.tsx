import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AppShell } from '../layout/AppShell';
import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import type { WindowSizeClass } from '../theme/tokens';
import { ALL_WINDOW_SIZE_CLASSES, setWindowSizeClass } from '../testing/viewport';
import { NAVIGATION_FORM } from './AdaptiveNavigation';
import { destinations, primaryDestinations, secondaryDestinations } from './destinations';

/**
 * The shell plus the real route table shape, so a navigation click is asserted end to end -- the link
 * has to actually resolve to a surface, not merely exist.
 */
function renderShell(sizeClass: WindowSizeClass, route = '/') {
  setWindowSizeClass(sizeClass);

  return render(
    <ThemeModeProvider initialMode="dark">
      <MemoryRouter initialEntries={[route]}>
        <Routes>
          <Route element={<AppShell />}>
            {destinations.map((destination) =>
              destination.path === '/' ? (
                <Route key={destination.id} index element={<p>{destination.label} surface</p>} />
              ) : (
                <Route
                  key={destination.id}
                  path={destination.path}
                  element={<p>{destination.label} surface</p>}
                />
              ),
            )}
          </Route>
        </Routes>
      </MemoryRouter>
    </ThemeModeProvider>,
  );
}

function primaryNav() {
  return screen.getByRole('navigation', { name: 'Primary' });
}

beforeEach(() => {
  window.localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('adaptive navigation', () => {
  it('shows a bottom navigation bar on compact', () => {
    // ADR-0005's compact form. Note the divergence from the wireframe, which switches at 720px: those
    // are the mock's numbers for laying three device frames on one page. Material's compact class -- and
    // the ADR -- end at 599.
    renderShell('compact');

    expect(primaryNav().dataset.navForm).toBe(NAVIGATION_FORM.compact);
  });

  it('shows an icon navigation rail on medium', () => {
    renderShell('medium');

    expect(primaryNav().dataset.navForm).toBe(NAVIGATION_FORM.medium);
  });

  it('shows a labelled navigation rail on expanded', () => {
    renderShell('expanded');

    expect(primaryNav().dataset.navForm).toBe(NAVIGATION_FORM.expanded);
  });

  it('renders exactly one form at a time', () => {
    // Two navigations on screen at once is the classic adaptive-layout bug: both breakpoint branches
    // render, and on a tablet the operator gets a rail *and* a bar eating the bottom of the chart.
    for (const sizeClass of ALL_WINDOW_SIZE_CLASSES) {
      renderShell(sizeClass);

      expect(screen.getAllByRole('navigation', { name: 'Primary' })).toHaveLength(1);

      cleanup();
    }
  });

  it.each(ALL_WINDOW_SIZE_CLASSES)('keeps every destination reachable at %s', (sizeClass) => {
    // The form changes; the destinations do not. Material caps a bottom bar at five, so on compact the
    // secondary destinations move to the app bar -- this is what proves they moved rather than vanished.
    renderShell(sizeClass);

    for (const destination of destinations) {
      const link = screen.getByRole('link', { name: destination.label });

      expect(link.getAttribute('href')).toBe(destination.path);
    }
  });

  it('shows destination labels as text only on the expanded rail', () => {
    // The medium rail is icons-only by definition (ADR-0005). The label still has to reach assistive
    // technology, which is why the icon rail carries it as an accessible name instead of dropping it.
    renderShell('medium');
    const iconRailLink = screen.getByRole('link', { name: 'Journal' });
    expect(iconRailLink.textContent).not.toContain('Journal');

    cleanup();

    renderShell('expanded');
    expect(screen.getByRole('link', { name: 'Journal' }).textContent).toContain('Journal');
  });

  it('marks the current destination, and only that one', () => {
    // `/` is a prefix of every other path, so naive prefix matching lights up Workspace on every
    // surface. Checked on a non-root route because that is where the bug shows.
    renderShell('expanded', '/journal');

    const current = screen
      .getAllByRole('link')
      .filter((link) => link.getAttribute('aria-current') === 'page');

    expect(current).toHaveLength(1);
    expect(current[0].getAttribute('href')).toBe('/journal');
  });

  it.each(ALL_WINDOW_SIZE_CLASSES)('navigates to a destination when clicked at %s', (sizeClass) => {
    renderShell(sizeClass);
    expect(screen.getByText('Workspace surface')).toBeTruthy();

    fireEvent.click(screen.getByRole('link', { name: 'Rulebook' }));

    expect(screen.getByText('Rulebook surface')).toBeTruthy();
  });

  it('keeps the bottom bar within the five destinations Material allows', () => {
    // Not style policing: a sixth target on a 360px-wide bar is under 60px, which is below the touch
    // target Material specifies, on the surface an operator uses one-handed.
    expect(primaryDestinations.length).toBeLessThanOrEqual(5);
    expect(secondaryDestinations.length).toBeGreaterThan(0);
  });
});
