import type { PriceLevel } from '../api/marketData';

/**
 * The chart's overlay contract (gh#727, ADR-0004) — the reusable primitives an overlay renders onto the candlestick
 * chart from pre-computed data. Increment 1 establishes the <b>price-line</b> primitive, which the price-level overlay
 * draws with; the suggestion-zone overlay (entry / stop / target) maps onto the same primitive, and the live
 * position / order / fill overlay adds a marker primitive beside it — so a fourth overlay is additive, not a rewrite.
 *
 * These are plain data (no charting-library import) so the mapping is unit-tested without a canvas; the
 * {@link MarketChart} translates a {@link PriceLineSpec} into the library's price-line at the one seam that touches it.
 */

/** How a price line is stroked. Kept a small union so the contract owns intent, not the library's enum. */
export type OverlayLineStyle = 'solid' | 'dashed';

/** One horizontal line to overlay at a price — the price-line half of the overlay contract. */
export interface PriceLineSpec {
  readonly price: number;
  readonly color: string;
  /** Shown on the line; empty for the second edge of a band so a zone reads as one label, not two. */
  readonly title: string;
  readonly style: OverlayLineStyle;
}

/** The colours a level overlay draws its two sides with — passed in so the palette stays the caller's concern. */
export interface LevelPalette {
  readonly support: string;
  readonly resistance: string;
}

/**
 * Maps one active price level — a support / resistance <i>band</i> — to its price lines: the top and bottom edges,
 * coloured by side, the top edge carrying the label. A degenerate band (<c>top === bottom</c>) collapses to a single
 * line. Any kind that is not <c>Support</c> is drawn as resistance (the server serves Support / Resistance today; a
 * future kind such as role-reversal, gh#597, still renders rather than throwing).
 */
export function levelToPriceLines(level: PriceLevel, palette: LevelPalette): PriceLineSpec[] {
  const isSupport = level.kind === 'Support';
  const color = isSupport ? palette.support : palette.resistance;
  const title = `${isSupport ? 'S' : 'R'} ${formatTimeframe(level.timeframeMinutes)}`;

  const top: PriceLineSpec = { price: level.top, color, title, style: 'dashed' };
  if (level.bottom === level.top) {
    return [top];
  }
  const bottom: PriceLineSpec = { price: level.bottom, color, title: '', style: 'dashed' };
  return [top, bottom];
}

/** Flattens a set of active levels to the price lines to draw. */
export function levelsToPriceLines(
  levels: readonly PriceLevel[],
  palette: LevelPalette,
): PriceLineSpec[] {
  return levels.flatMap((level) => levelToPriceLines(level, palette));
}

/** A compact timeframe label for a level's line: 60 → "1h", 120 → "2h", 5 → "5m". */
function formatTimeframe(minutes: number): string {
  return minutes % 60 === 0 ? `${minutes / 60}h` : `${minutes}m`;
}

/**
 * A suggestion's geometry to overlay on the chart (gh#727 increment 2): the entry, stop and target of one active
 * suggestion, reduced to the numbers the chart draws. Chart-agnostic on purpose — the workspace maps a `Suggestion`
 * onto this, so {@link levelToPriceLines}'s consumer (`MarketChart`) never imports the suggestion API.
 */
export interface SuggestionZone {
  readonly id: string;
  readonly entry: number;
  readonly stop: number;
  readonly target: number;
}

/** The colours a suggestion overlay draws its three roles with — entry / stop (risk) / target (reward). */
export interface SuggestionPalette {
  readonly entry: string;
  readonly stop: string;
  readonly target: string;
}

/**
 * Maps one suggestion zone to its price lines — entry (solid), stop and target (dashed) — coloured by ROLE, not
 * direction: the stop is always the risk side and the target the reward side, long or short, so the geometry reads
 * the same every time.
 *
 * `ordinal` (1-based), when given, suffixes all three titles (`Entry #2`…) so the operator can tell which zone a
 * line belongs to when several coexist; omit it for a lone zone to keep the labels clean.
 */
export function suggestionToPriceLines(
  zone: SuggestionZone,
  palette: SuggestionPalette,
  ordinal?: number,
): PriceLineSpec[] {
  const suffix = ordinal === undefined ? '' : ` #${ordinal}`;
  return [
    { price: zone.entry, color: palette.entry, title: `Entry${suffix}`, style: 'solid' },
    { price: zone.stop, color: palette.stop, title: `Stop${suffix}`, style: 'dashed' },
    { price: zone.target, color: palette.target, title: `Target${suffix}`, style: 'dashed' },
  ];
}

/**
 * Flattens a set of suggestion zones to the price lines to draw. Supersession is keyed on
 * (trigger, instrument, side), not the symbol (data dictionary §6), so several Active suggestions can share one
 * instrument — and then six identically titled lines leave the operator unable to tell which stop protects which
 * entry. So when more than one zone is present each zone's three lines carry a shared ordinal (`Entry #1` / `Stop #1`
 * / `Target #1`, `Entry #2` / …), tying the trio together; a lone zone stays unsuffixed (gh#727 review).
 */
export function suggestionsToPriceLines(
  zones: readonly SuggestionZone[],
  palette: SuggestionPalette,
): PriceLineSpec[] {
  const disambiguate = zones.length > 1;
  return zones.flatMap((zone, index) =>
    suggestionToPriceLines(zone, palette, disambiguate ? index + 1 : undefined),
  );
}
