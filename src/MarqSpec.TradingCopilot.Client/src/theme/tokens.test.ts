import { describe, expect, it } from 'vitest';

// Vite hands the stylesheet over as text. That keeps the pre-paint colour literals in `index.css`
// under test without dragging Node's fs (and @types/node) into a browser-only project.
import indexCss from '../index.css?raw';
import { colorTokens, cssVariables, type ColorTokens, type ThemeMode } from './tokens';

const MODES: readonly ThemeMode[] = ['dark', 'light'];

/** Every leaf value in a token set, as `group.key` -> value. */
function flatten(tokens: ColorTokens): Record<string, string> {
  const flat: Record<string, string> = {};

  for (const [group, values] of Object.entries(tokens)) {
    for (const [key, value] of Object.entries(values as Record<string, string>)) {
      flat[`${group}.${key}`] = value;
    }
  }

  return flat;
}

describe('colorTokens', () => {
  it.each(MODES)('defines every token in %s mode', (mode) => {
    const flat = flatten(colorTokens[mode]);

    expect(Object.keys(flat).length).toBeGreaterThan(0);
    for (const [key, value] of Object.entries(flat)) {
      expect(value, `${mode}.${key} is empty`).not.toBe('');
    }
  });

  it('gives both modes exactly the same token keys', () => {
    // ADR-0005 calls the light theme "a token swap, not a redesign". A key present in one mode and
    // missing in the other is the first step away from that -- it means one mode has grown a colour the
    // other has to fake.
    expect(Object.keys(flatten(colorTokens.light)).sort()).toStrictEqual(
      Object.keys(flatten(colorTokens.dark)).sort(),
    );
  });

  it.each(MODES)('keeps every semantic trading colour off the accent in %s mode', (mode) => {
    // The regression ADR-0005 warns about, guarded at the source. If a trading semantic and the accent
    // are ever the same value, "status never competes with the brand accent" is no longer true and no
    // component-level review will catch it.
    const { accent, trading } = colorTokens[mode];
    const accentValues = new Set(Object.values(accent).map((value) => value.toUpperCase()));

    for (const [name, value] of Object.entries(trading)) {
      expect(
        accentValues.has(value.toUpperCase()),
        `trading.${name} collides with the accent`,
      ).toBe(false);
    }
  });

  it.each(MODES)(
    'keeps long, short, warn and critical distinct from each other in %s mode',
    (mode) => {
      // Four states the operator has to tell apart at a glance in a moving cockpit. Two that share a
      // value are two states they cannot.
      const roles = ['long', 'short', 'warn', 'critical'] as const;
      const values = roles.map((role) => colorTokens[mode].trading[role].toUpperCase());

      expect(new Set(values).size).toBe(roles.length);
    },
  );
});

describe('cssVariables', () => {
  it.each(MODES)('emits a variable for every colour token in %s mode', (mode) => {
    // The chart panes (ADR-0004) read these, so a token that never reaches CSS is a token the chart
    // cannot be themed with -- and a hand-picked colour is what fills that gap.
    const emitted = new Set(Object.values(cssVariables(colorTokens[mode])));

    for (const [key, value] of Object.entries(flatten(colorTokens[mode]))) {
      expect(emitted.has(value), `${key} never reaches CSS`).toBe(true);
    }
  });

  it('names every variable in the --tc- namespace', () => {
    // A bare `--primary` would collide with anything else on the page that ships custom properties.
    for (const name of Object.keys(cssVariables(colorTokens.dark))) {
      expect(name.startsWith('--tc-')).toBe(true);
    }
  });

  it('swaps every colour when the mode does', () => {
    const dark = cssVariables(colorTokens.dark);
    const light = cssVariables(colorTokens.light);

    // Shape, radii and typefaces are mode-independent by design; every colour must move.
    for (const name of Object.keys(dark)) {
      if (!name.startsWith('--tc-r-') && name !== '--tc-font' && name !== '--tc-mono') {
        expect(light[name], `${name} did not change with the mode`).not.toBe(dark[name]);
      }
    }
  });
});

describe('index.css', () => {
  it('paints the pre-mount canvas in the token background colours', () => {
    // The one duplication of a token value outside this module (CSS cannot import TypeScript). Pinned
    // here so a token change that leaves the stylesheet behind fails a test instead of showing up as a
    // one-frame flash of the wrong colour on load.
    expect(indexCss.toLowerCase()).toContain(colorTokens.dark.surface.background.toLowerCase());
    expect(indexCss.toLowerCase()).toContain(colorTokens.light.surface.background.toLowerCase());
  });
});
