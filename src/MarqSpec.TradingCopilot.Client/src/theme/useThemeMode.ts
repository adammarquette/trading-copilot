import { useContext } from 'react';

import { ThemeModeContext, type ThemeModeContextValue } from './ThemeModeProvider';

/**
 * Reads the current theme mode and the controls that change it.
 *
 * Throws outside the provider rather than handing back a default: a component that silently believes it
 * is in dark mode while the operator is in light is a rendering bug that only shows up as a colour, and
 * those are the expensive ones to chase.
 */
export function useThemeMode(): ThemeModeContextValue {
  const value = useContext(ThemeModeContext);

  if (value === null) {
    throw new Error('useThemeMode must be used inside a <ThemeModeProvider>.');
  }

  return value;
}
