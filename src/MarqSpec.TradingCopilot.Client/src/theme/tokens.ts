/**
 * The design tokens of ADR-0005 -- dark-first Material 3, cool near-black neutrals biased toward the
 * blue accent, with the trading semantics held apart from that accent.
 *
 * This module is the single source. Every colour, radius, elevation and typeface the SPA paints comes
 * from here, through the MUI themes in `createAppTheme.ts`. The failure mode it exists to prevent is a
 * feature component reaching for a literal `#4FBF7B` (or, worse, the brand blue) to colour a P&L cell:
 * once that happens the light theme stops being a token swap and ADR-0005's "status never competes
 * with the accent" line quietly stops holding.
 *
 * The values are lifted verbatim from `documentation/design/wireframes.html`, which is the artifact the
 * operator validates UX against. They are not to be re-derived, re-mixed, or "improved" here -- if a
 * colour is wrong it is wrong in the wireframe first.
 */

/** Dark or light. Dark is the product default (ADR-0005); light is a token swap, not a redesign. */
export type ThemeMode = 'dark' | 'light';

/** Background plus Material's four tonal elevation surfaces. */
export interface SurfaceTokens {
  readonly background: string;
  readonly level1: string;
  readonly level2: string;
  readonly level3: string;
  readonly level4: string;
}

/** On-colours, by emphasis. `dim` is for labels that must not compete with the number beside them. */
export interface ContentTokens {
  readonly high: string;
  readonly medium: string;
  readonly dim: string;
}

/** Borders. `soft` is the hairline between panes; `strong` frames a whole surface. */
export interface OutlineTokens {
  readonly strong: string;
  readonly soft: string;
}

/** The brand accent -- deliberately a blue, and deliberately never a trading semantic. */
export interface AccentTokens {
  readonly primary: string;
  readonly primaryContainer: string;
  readonly onPrimary: string;
}

/**
 * The trading semantics: long/profit, short/loss, time-to-flat, and critical/kill. ADR-0005 keeps these
 * on their own scale so live status reads at a glance instead of blending into brand chrome.
 */
export interface TradingTokens {
  readonly long: string;
  readonly longContainer: string;
  readonly short: string;
  readonly shortContainer: string;
  readonly warn: string;
  readonly warnContainer: string;
  readonly critical: string;
  readonly criticalContainer: string;
}

/** The three shadow steps. They differ between modes because a dark-ground shadow has to be blacker. */
export interface ElevationTokens {
  readonly e1: string;
  readonly e2: string;
  readonly e3: string;
}

/** Everything that changes when the mode changes. Both modes must fill every field -- see the tests. */
export interface ColorTokens {
  readonly surface: SurfaceTokens;
  readonly content: ContentTokens;
  readonly outline: OutlineTokens;
  readonly accent: AccentTokens;
  readonly trading: TradingTokens;
  readonly elevation: ElevationTokens;
}

const dark: ColorTokens = {
  surface: {
    background: '#101318',
    level1: '#161A20',
    level2: '#1C212A',
    level3: '#222834',
    level4: '#29313F',
  },
  content: { high: '#E5E8EE', medium: '#A6AEBB', dim: '#6C7482' },
  outline: { strong: '#333B48', soft: '#242A34' },
  accent: { primary: '#9DC3FF', primaryContainer: '#223A5C', onPrimary: '#0A2745' },
  trading: {
    long: '#4FBF7B',
    longContainer: '#123021',
    short: '#F0655C',
    shortContainer: '#3A1714',
    warn: '#F2B450',
    warnContainer: '#33270B',
    critical: '#FF5A4E',
    criticalContainer: '#40130F',
  },
  elevation: {
    e1: '0 1px 2px rgba(0,0,0,.5)',
    e2: '0 4px 12px rgba(0,0,0,.5)',
    e3: '0 10px 28px rgba(0,0,0,.55)',
  },
};

const light: ColorTokens = {
  surface: {
    background: '#E9EDF4',
    level1: '#FFFFFF',
    level2: '#F1F4F9',
    level3: '#E3E9F2',
    level4: '#D4DCE8',
  },
  content: { high: '#111826', medium: '#48536A', dim: '#79839A' },
  outline: { strong: '#C3CBD9', soft: '#E1E6EF' },
  accent: { primary: '#2D6BD6', primaryContainer: '#D8E4FB', onPrimary: '#FFFFFF' },
  trading: {
    long: '#1E9A56',
    longContainer: '#D4F0DE',
    short: '#D8443A',
    shortContainer: '#FBDAD6',
    warn: '#B07414',
    warnContainer: '#FBEDC9',
    critical: '#D6352A',
    criticalContainer: '#FBD4CF',
  },
  elevation: {
    e1: '0 1px 2px rgba(17,24,38,.10)',
    e2: '0 4px 12px rgba(17,24,38,.10)',
    e3: '0 10px 28px rgba(17,24,38,.12)',
  },
};

export const colorTokens: Readonly<Record<ThemeMode, ColorTokens>> = { dark, light };

/** Corner radii, in px. Shared by both modes -- shape is not a mode concern. */
export const radii = { small: 8, medium: 12, large: 16 } as const;

/**
 * Roboto / Roboto Mono per ADR-0005, self-hosted through @fontsource (see `main.tsx`) rather than a
 * font CDN: this app is self-hosted and must not depend on reaching a third-party host to render.
 */
export const typefaces = {
  sans: 'Roboto, "Segoe UI", system-ui, -apple-system, "Helvetica Neue", sans-serif',
  mono: '"Roboto Mono", ui-monospace, "Cascadia Code", Consolas, monospace',
} as const;

/**
 * Material 3 window size classes, as min-widths in px.
 *
 * These are Material's numbers, and ADR-0005 is normative on them: compact < 600, medium 600-839,
 * expanded >= 840. They deliberately do **not** reuse MUI's `sm`/`md` breakpoint scale -- MUI's `md` is
 * 900, so redefining it to 840 would silently move every MUI component that keys off it, and leaving it
 * at 900 while calling it "expanded" would put the layout switch 60px from where the ADR says it is.
 * The window size class is its own axis; read it through `useWindowSizeClass`, not through `sx`
 * breakpoint objects.
 */
export const windowSizeClassMinWidth = { compact: 0, medium: 600, expanded: 840 } as const;

/** The three Material 3 window size classes, widest-last. */
export type WindowSizeClass = keyof typeof windowSizeClassMinWidth;

/**
 * The same tokens as CSS custom properties, so surfaces MUI does not own can read them.
 *
 * ADR-0005 calls this out as a consequence: Lightweight Charts (ADR-0004) is not a Material component
 * and has to be themed by hand, and the wireframe already drives it off `--primary` / `--long` / etc.
 * Emitting the variables from the same object the MUI theme is built from means the chart and the
 * chrome cannot drift apart.
 */
export function cssVariables(tokens: ColorTokens): Record<string, string> {
  return {
    '--tc-bg': tokens.surface.background,
    '--tc-surface-1': tokens.surface.level1,
    '--tc-surface-2': tokens.surface.level2,
    '--tc-surface-3': tokens.surface.level3,
    '--tc-surface-4': tokens.surface.level4,
    '--tc-on-hi': tokens.content.high,
    '--tc-on-med': tokens.content.medium,
    '--tc-on-dim': tokens.content.dim,
    '--tc-outline': tokens.outline.strong,
    '--tc-outline-soft': tokens.outline.soft,
    '--tc-primary': tokens.accent.primary,
    '--tc-primary-ctr': tokens.accent.primaryContainer,
    '--tc-on-primary': tokens.accent.onPrimary,
    '--tc-long': tokens.trading.long,
    '--tc-long-ctr': tokens.trading.longContainer,
    '--tc-short': tokens.trading.short,
    '--tc-short-ctr': tokens.trading.shortContainer,
    '--tc-warn': tokens.trading.warn,
    '--tc-warn-ctr': tokens.trading.warnContainer,
    '--tc-critical': tokens.trading.critical,
    '--tc-critical-ctr': tokens.trading.criticalContainer,
    '--tc-e1': tokens.elevation.e1,
    '--tc-e2': tokens.elevation.e2,
    '--tc-e3': tokens.elevation.e3,
    '--tc-r-s': `${radii.small}px`,
    '--tc-r-m': `${radii.medium}px`,
    '--tc-r-l': `${radii.large}px`,
    '--tc-font': typefaces.sans,
    '--tc-mono': typefaces.mono,
  };
}
