import { render as rtlRender, type RenderOptions, type RenderResult } from '@testing-library/react';
import type { ReactElement, ReactNode } from 'react';
import { MemoryRouter } from 'react-router';

import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import type { ThemeMode } from '../theme/tokens';

/**
 * Renders inside the two providers every component in this app assumes: the theme (so `palette.trading`
 * resolves) and a router (so links do not throw).
 *
 * `MemoryRouter` rather than the production `BrowserRouter` -- which is exactly why `App` does not carry
 * its own router. A test can then start at any path without touching `window.history`.
 */
export interface RenderWithProvidersOptions extends Omit<RenderOptions, 'wrapper'> {
  /** Initial location. */
  readonly route?: string;
  /** Forces a theme mode. Left off, the provider reads the operator's stored choice, as production does. */
  readonly mode?: ThemeMode;
}

export function renderWithProviders(
  ui: ReactElement,
  { route = '/', mode, ...options }: RenderWithProvidersOptions = {},
): RenderResult {
  function Wrapper({ children }: { readonly children: ReactNode }) {
    return (
      <ThemeModeProvider initialMode={mode}>
        <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>
      </ThemeModeProvider>
    );
  }

  return rtlRender(ui, { wrapper: Wrapper, ...options });
}
