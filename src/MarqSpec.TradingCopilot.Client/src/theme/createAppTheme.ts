import { createTheme, type Shadows, type Theme } from '@mui/material/styles';
import type { CSSProperties } from 'react';

import {
  colorTokens,
  cssVariables,
  radii,
  typefaces,
  type ColorTokens,
  type OutlineTokens,
  type ThemeMode,
  type TradingTokens,
} from './tokens';

/**
 * Module augmentation is how the trading semantics stay *out of* the accent.
 *
 * ADR-0005: "semantic trading colors are separate from the accent". If long/short/warn/critical were
 * folded into `palette.primary` (or reachable as a `color="primary"` variant), a feature component
 * would eventually paint a P&L cell in brand blue -- or paint brand chrome green -- and the whole
 * status-reads-at-a-glance argument goes with it. Giving them their own `palette.trading` namespace
 * makes the correct call the obvious one and the incorrect call impossible to reach by accident.
 */
declare module '@mui/material/styles' {
  interface Palette {
    trading: TradingTokens;
    outline: OutlineTokens;
  }
  interface PaletteOptions {
    trading?: TradingTokens;
    outline?: OutlineTokens;
  }

  /** The four Material tonal surfaces. `paper` stays as MUI's own alias for level 1. */
  interface TypeBackground {
    surface1: string;
    surface2: string;
    surface3: string;
    surface4: string;
  }

  /** Prices and P&L only line up in a column if the figures are monospaced and tabular. */
  interface TypographyVariants {
    numeric: CSSProperties;
  }
  interface TypographyVariantsOptions {
    numeric?: CSSProperties;
  }
}

declare module '@mui/material/Typography' {
  interface TypographyPropsVariantOverrides {
    numeric: true;
  }
}

/**
 * MUI wants all 25 elevation steps. We have three. Rather than invent 22 more shadows -- which is how a
 * token set stops being the source of truth -- the three are stretched across the scale, so any
 * `elevation` a component picks still lands on a token.
 */
function elevationScale(tokens: ColorTokens): Shadows {
  const { e1, e2, e3 } = tokens.elevation;
  const steps = Array.from({ length: 25 }, (_unused, index) => {
    if (index === 0) return 'none';
    if (index <= 2) return e1;
    if (index <= 8) return e2;
    return e3;
  });

  // `Shadows` is a 25-tuple; Array.from cannot prove its own length to the compiler. The length is
  // fixed above, so this is a shape assertion rather than a loosening -- note it is not `any`.
  return steps as unknown as Shadows;
}

/**
 * Builds the MUI theme for a mode. Both modes run through this one function on purpose: a light theme
 * that is "a token swap, not a redesign" (ADR-0005) has to be structurally the same theme with a
 * different token set, not a second hand-tuned theme that drifts.
 */
export function createAppTheme(mode: ThemeMode): Theme {
  const tokens = colorTokens[mode];

  return createTheme({
    palette: {
      mode,

      primary: {
        main: tokens.accent.primary,
        // The wireframe's `--primary-ctr` -- the tonal container behind an accent chip or a selected
        // rail item. MUI would otherwise synthesise `light`/`dark` by lightening the accent, which is
        // not the same colour the wireframe shows.
        light: tokens.accent.primaryContainer,
        dark: tokens.accent.primaryContainer,
        contrastText: tokens.accent.onPrimary,
      },

      // MUI needs error/warning/success/info for its own components (form validation, alerts). Left
      // unset they fall back to MUI's stock reds and greens, which would put un-tokened colour on the
      // screen. Aliasing them onto the semantic scale keeps every painted colour a token -- but a P&L
      // figure or a position side belongs on `palette.trading`, which says what it means; `error` here
      // means "this input is invalid", not "this trade lost money".
      error: { main: tokens.trading.critical, contrastText: tokens.content.high },
      warning: { main: tokens.trading.warn, contrastText: tokens.surface.background },
      success: { main: tokens.trading.long, contrastText: tokens.surface.background },
      info: { main: tokens.accent.primary, contrastText: tokens.accent.onPrimary },

      background: {
        default: tokens.surface.background,
        paper: tokens.surface.level1,
        surface1: tokens.surface.level1,
        surface2: tokens.surface.level2,
        surface3: tokens.surface.level3,
        surface4: tokens.surface.level4,
      },

      text: {
        primary: tokens.content.high,
        secondary: tokens.content.medium,
        disabled: tokens.content.dim,
      },

      // The hairline between panes is the soft outline; the strong one frames a whole surface and is
      // reached explicitly through `palette.outline.strong`.
      divider: tokens.outline.soft,
      outline: tokens.outline,

      trading: tokens.trading,
    },

    shape: { borderRadius: radii.medium },
    shadows: elevationScale(tokens),

    typography: {
      fontFamily: typefaces.sans,
      // A dense cockpit; Material's default 16px body pushes too little data per screen (ADR-0005
      // "expect to tighten component density"). The wireframe reads at 14.
      fontSize: 14,
      button: { textTransform: 'none', fontWeight: 600 },
      numeric: {
        fontFamily: typefaces.mono,
        fontVariantNumeric: 'tabular-nums',
        fontSize: '0.875rem',
        lineHeight: 1.4,
      },
    },

    components: {
      MuiCssBaseline: {
        styleOverrides: {
          // Same tokens, reachable from plain CSS. Lightweight Charts (ADR-0004) is not a Material
          // component and has to be themed by hand; pointing it at these variables is what stops the
          // chart from looking foreign when the mode flips.
          ':root': cssVariables(tokens),

          // The shell sizes itself to the viewport and scrolls only its content region, so the safety
          // controls cannot be scrolled off. That only holds if the document itself does not scroll.
          'html, body, #root': { height: '100%' },
          body: { overscrollBehavior: 'none' },
        },
      },
      MuiTypography: {
        defaultProps: {
          // `numeric` is an inline figure, not a block of prose.
          variantMapping: { numeric: 'span' },
        },
      },
    },
  });
}
