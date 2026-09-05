/**
 * Reads a rendered colour independently of the pure function that chose it (gh#1115).
 *
 * `toneOf` in `journal/format.ts` is unit-tested on its own — positive / negative / zero / null are all
 * covered there. But a component test that then asserts a rendered tone by calling `toneOf` again is
 * asking the thing under test what to expect: it cannot fail on a *wiring* defect (a hardcoded
 * `tone="positive"` on a tile, a swapped long/short branch, a scratch trade painted green) because the
 * expectation and the implementation are the same function. `toneOf`'s own unit tests stay green through
 * exactly that class of bug.
 *
 * The fix is to read what actually got **painted** and compare it to the theme's own colour tokens —
 * never to the component or the function under test. Settling *how* (gh#1115), so the next surface that
 * colours a number has a pattern to copy instead of re-deciding:
 *
 * - `getComputedStyle(el).color` resolves MUI/emotion's emitted class rules under jsdom to a real
 *   `rgb(r, g, b)` string. This was verified empirically against this project's Vitest/jsdom setup before
 *   relying on it — it is not assumed to work just because it would on a real browser. No `data-tone`
 *   escape hatch is needed as a result.
 * - Compare that string to `hexToRgb(colorTokens[mode].trading.long)` (etc.) below, **never** to a value
 *   read back off the component's own props or off `toneOf`.
 * - Always pass `mode` explicitly to `renderWithProviders` in a test that asserts colour. Left off, the
 *   provider reads `localStorage`, which a colour-blind test earlier in the same run may have already
 *   written to (`ThemeModeProvider` persists the mode on every render) — an implicit default makes the
 *   assertion depend on execution order instead of on the component.
 */
export function hexToRgb(hex: string): string {
  const value = hex.replace('#', '');
  const r = parseInt(value.slice(0, 2), 16);
  const g = parseInt(value.slice(2, 4), 16);
  const b = parseInt(value.slice(4, 6), 16);
  return `rgb(${String(r)}, ${String(g)}, ${String(b)})`;
}
