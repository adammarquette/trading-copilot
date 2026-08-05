import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useTheme } from '@mui/material/styles';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ThemeModeToggle } from '../layout/ThemeModeToggle';
import { DEFAULT_THEME_MODE, THEME_MODE_STORAGE_KEY, ThemeModeProvider } from './ThemeModeProvider';
import { colorTokens } from './tokens';

/**
 * Reports the values the theme is actually handing to components. Asserting on these -- rather than on
 * a class name or a snapshot -- is what makes "the toggle swaps tokens" a real claim: a toggle that
 * flipped a flag without moving the palette would pass a snapshot test and fail this one.
 */
function ThemeProbe() {
  const theme = useTheme();

  return (
    <span data-testid="probe" data-mode={theme.palette.mode}>
      {`${theme.palette.background.default}|${theme.palette.primary.main}|${theme.palette.trading.long}`}
    </span>
  );
}

function probe() {
  return screen.getByTestId('probe');
}

function paletteOf(mode: 'dark' | 'light') {
  const tokens = colorTokens[mode];
  return `${tokens.surface.background}|${tokens.accent.primary}|${tokens.trading.long}`;
}

beforeEach(() => {
  window.localStorage.clear();
  delete document.documentElement.dataset.theme;
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('ThemeModeProvider', () => {
  it('starts dark when the operator has never chosen', () => {
    // ADR-0005: dark is the product default, not the OS preference. An all-day cockpit that opens white
    // because the desktop is in light mode is the wrong first impression at 6am.
    render(
      <ThemeModeProvider>
        <ThemeProbe />
      </ThemeModeProvider>,
    );

    expect(DEFAULT_THEME_MODE).toBe('dark');
    expect(probe().dataset.mode).toBe('dark');
    expect(probe().textContent).toBe(paletteOf('dark'));
  });

  it('restores a previously chosen mode', () => {
    window.localStorage.setItem(THEME_MODE_STORAGE_KEY, 'light');

    render(
      <ThemeModeProvider>
        <ThemeProbe />
      </ThemeModeProvider>,
    );

    expect(probe().textContent).toBe(paletteOf('light'));
  });

  it('falls back to the default when the stored value is not a mode', () => {
    // Storage is shared with anything else on the origin and survives every deploy; a value written by
    // an older build (or by hand) must not be able to stop the app rendering.
    window.localStorage.setItem(THEME_MODE_STORAGE_KEY, 'solarized');

    render(
      <ThemeModeProvider>
        <ThemeProbe />
      </ThemeModeProvider>,
    );

    expect(probe().dataset.mode).toBe('dark');
  });

  it('swaps every token when the operator toggles, and remembers the choice', () => {
    render(
      <ThemeModeProvider>
        <ThemeModeToggle />
        <ThemeProbe />
      </ThemeModeProvider>,
    );

    expect(probe().textContent).toBe(paletteOf('dark'));

    fireEvent.click(screen.getByRole('button', { name: 'Switch to light theme' }));

    expect(probe().textContent).toBe(paletteOf('light'));
    expect(window.localStorage.getItem(THEME_MODE_STORAGE_KEY)).toBe('light');

    // Back again: the toggle has to be symmetric, or the operator can leave dark mode but not return.
    fireEvent.click(screen.getByRole('button', { name: 'Switch to dark theme' }));

    expect(probe().textContent).toBe(paletteOf('dark'));
    expect(window.localStorage.getItem(THEME_MODE_STORAGE_KEY)).toBe('dark');
  });

  it('mirrors the mode onto the document so hand-themed panes follow it', () => {
    // The wireframe's `:root[data-theme="light"]` switch. Lightweight Charts is not a MUI component; it
    // reads CSS, and this attribute plus the --tc-* variables are how it stays in step (ADR-0005).
    window.localStorage.setItem(THEME_MODE_STORAGE_KEY, 'light');

    render(
      <ThemeModeProvider>
        <ThemeProbe />
      </ThemeModeProvider>,
    );

    expect(document.documentElement.dataset.theme).toBe('light');
  });

  it('still renders when storage is unavailable', () => {
    // Safari private browsing and locked-down kiosks throw on access rather than returning null. Losing
    // the theme preference is acceptable; losing the cockpit is not.
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('storage disabled');
      },
      setItem: () => {
        throw new Error('storage disabled');
      },
    });

    render(
      <ThemeModeProvider>
        <ThemeProbe />
      </ThemeModeProvider>,
    );

    expect(probe().dataset.mode).toBe('dark');
  });
});
