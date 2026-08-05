import { describe, expect, it } from 'vitest';

import { createAppTheme } from './createAppTheme';
import { colorTokens, radii, typefaces, type ThemeMode } from './tokens';

const MODES: readonly ThemeMode[] = ['dark', 'light'];

describe('createAppTheme', () => {
  it.each(MODES)('never lets a trading semantic be reachable as the accent in %s mode', (mode) => {
    // The guard that matters at the point of use. `tokens.test.ts` proves the *values* differ; this
    // proves the *theme* keeps them apart, so a component writing `color="primary"` on a P&L figure
    // cannot accidentally get green -- and one writing `palette.trading.long` cannot get brand blue.
    const { palette } = createAppTheme(mode);
    const accent = [palette.primary.main, palette.primary.light, palette.primary.contrastText].map(
      (value) => value.toUpperCase(),
    );

    for (const [name, value] of Object.entries(palette.trading)) {
      expect(accent, `palette.trading.${name} is reachable as the accent`).not.toContain(
        value.toUpperCase(),
      );
    }
  });

  it.each(MODES)('exposes the trading scale under its own namespace in %s mode', (mode) => {
    const { palette } = createAppTheme(mode);

    expect(palette.trading).toStrictEqual(colorTokens[mode].trading);
  });

  it.each(MODES)('builds every surface, on-colour and outline from the %s tokens', (mode) => {
    const tokens = colorTokens[mode];
    const { palette } = createAppTheme(mode);

    expect(palette.mode).toBe(mode);
    expect(palette.background.default).toBe(tokens.surface.background);
    expect(palette.background.paper).toBe(tokens.surface.level1);
    expect(palette.background.surface1).toBe(tokens.surface.level1);
    expect(palette.background.surface2).toBe(tokens.surface.level2);
    expect(palette.background.surface3).toBe(tokens.surface.level3);
    expect(palette.background.surface4).toBe(tokens.surface.level4);
    expect(palette.text.primary).toBe(tokens.content.high);
    expect(palette.text.secondary).toBe(tokens.content.medium);
    expect(palette.text.disabled).toBe(tokens.content.dim);
    expect(palette.divider).toBe(tokens.outline.soft);
    expect(palette.outline).toStrictEqual(tokens.outline);
  });

  it('swaps the whole palette when the mode swaps', () => {
    // "A token swap, not a redesign" (ADR-0005) has a testable consequence: the same structural theme
    // comes out, holding different values.
    const dark = createAppTheme('dark');
    const light = createAppTheme('light');

    expect(light.palette.background.default).not.toBe(dark.palette.background.default);
    expect(light.palette.primary.main).not.toBe(dark.palette.primary.main);
    expect(light.palette.trading.long).not.toBe(dark.palette.trading.long);
    expect(light.shadows[1]).not.toBe(dark.shadows[1]);

    // ...and shape is not a mode concern, so it must not move.
    expect(light.shape.borderRadius).toBe(dark.shape.borderRadius);
  });

  it.each(MODES)('draws every elevation step from the three %s shadow tokens', (mode) => {
    const { elevation } = colorTokens[mode];
    const { shadows } = createAppTheme(mode);
    const allowed = new Set(['none', elevation.e1, elevation.e2, elevation.e3]);

    expect(shadows).toHaveLength(25);
    expect(shadows[0]).toBe('none');
    for (const shadow of shadows) {
      // MUI would otherwise fall back to its own 24-step Material 2 shadow ramp -- 24 values that are
      // not in the token set and do not swap with the theme.
      expect(allowed.has(shadow), `${shadow} is not an elevation token`).toBe(true);
    }
  });

  it('offers tabular monospaced figures for price and P&L columns', () => {
    // ADR-0005 requires tabular numerals "wherever prices / P&L align in columns". A proportional or
    // non-tabular figure makes a column of numbers jitter as the last digit ticks.
    const { typography } = createAppTheme('dark');

    expect(typography.numeric.fontFamily).toBe(typefaces.mono);
    expect(typography.numeric.fontVariantNumeric).toBe('tabular-nums');
  });

  it('uses the token typeface and shape scale', () => {
    const theme = createAppTheme('dark');

    expect(theme.typography.fontFamily).toBe(typefaces.sans);
    expect(theme.shape.borderRadius).toBe(radii.medium);
  });

  it.each(MODES)('publishes the %s tokens as CSS custom properties', (mode) => {
    // The hand-off to the non-MUI panes (Lightweight Charts, ADR-0004).
    const overrides = createAppTheme(mode).components?.MuiCssBaseline?.styleOverrides;
    const root = (overrides as Record<string, Record<string, string>> | undefined)?.[':root'];

    expect(root?.['--tc-primary']).toBe(colorTokens[mode].accent.primary);
    expect(root?.['--tc-long']).toBe(colorTokens[mode].trading.long);
    expect(root?.['--tc-critical']).toBe(colorTokens[mode].trading.critical);
  });
});
