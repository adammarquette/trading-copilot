import CssBaseline from '@mui/material/CssBaseline';
import { ThemeProvider } from '@mui/material/styles';
import { createContext, useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';

import { createAppTheme } from './createAppTheme';
import type { ThemeMode } from './tokens';

/**
 * Where the operator's choice is kept. Namespaced because the SPA shares an origin with the BFF, and an
 * unprefixed "theme" key is the sort of thing two unrelated features both claim.
 */
export const THEME_MODE_STORAGE_KEY = 'trading-copilot.theme-mode';

/** Dark is the product default (ADR-0005) -- an all-day cockpit, not the OS's preference. */
export const DEFAULT_THEME_MODE: ThemeMode = 'dark';

export interface ThemeModeContextValue {
  readonly mode: ThemeMode;
  readonly setMode: (mode: ThemeMode) => void;
  readonly toggleMode: () => void;
}

export const ThemeModeContext = createContext<ThemeModeContextValue | null>(null);

function isThemeMode(value: unknown): value is ThemeMode {
  return value === 'dark' || value === 'light';
}

/**
 * Reads the stored choice, tolerating both a hostile value and no storage at all.
 *
 * `localStorage` throws outright when storage is disabled (Safari private browsing, a locked-down
 * kiosk), and a stored value can be anything if some other tab wrote it. Neither is a reason to fail to
 * render a trading cockpit, so both fall back to the default.
 */
function readStoredMode(): ThemeMode {
  try {
    const stored = window.localStorage.getItem(THEME_MODE_STORAGE_KEY);
    return isThemeMode(stored) ? stored : DEFAULT_THEME_MODE;
  } catch {
    return DEFAULT_THEME_MODE;
  }
}

export interface ThemeModeProviderProps {
  readonly children: ReactNode;
  /** Test seam. Production reads the operator's stored choice. */
  readonly initialMode?: ThemeMode;
}

/**
 * Owns the mode, the theme derived from it, and the persistence of the operator's choice.
 *
 * The theme is rebuilt only when the mode changes, so a mode flip is one re-render of the tree rather
 * than a new theme object on every render (which would remount every styled subtree).
 */
export function ThemeModeProvider({ children, initialMode }: ThemeModeProviderProps) {
  const [mode, setMode] = useState<ThemeMode>(() => initialMode ?? readStoredMode());

  useEffect(() => {
    try {
      window.localStorage.setItem(THEME_MODE_STORAGE_KEY, mode);
    } catch {
      // A refused write costs the operator the choice on next load, nothing more. Failing the render
      // over it would cost them the cockpit.
    }

    // The wireframe switches on `:root[data-theme="light"]`, and ADR-0005's gh#28 follow-up says to
    // carry that same variant into the SPA. Mirroring the attribute keeps hand-themed panes (the chart)
    // and any plain CSS on the same switch as MUI.
    document.documentElement.dataset.theme = mode;
  }, [mode]);

  const toggleMode = useCallback(() => {
    setMode((current) => (current === 'dark' ? 'light' : 'dark'));
  }, []);

  const theme = useMemo(() => createAppTheme(mode), [mode]);
  const value = useMemo<ThemeModeContextValue>(
    () => ({ mode, setMode, toggleMode }),
    [mode, toggleMode],
  );

  return (
    <ThemeModeContext value={value}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        {children}
      </ThemeProvider>
    </ThemeModeContext>
  );
}
