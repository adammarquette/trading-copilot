/**
 * How the card renders numbers.
 *
 * Fixed `en-US` formatters, not the browser's locale: a futures price is a market convention, not a regional one,
 * and a locale that renders `5.230,25` beside a chart reading `5230.25` is a misread waiting to happen. Grouping
 * is off for prices for the same reason — no quote screen puts a thousands separator in an ES price.
 */

const PRICE = new Intl.NumberFormat('en-US', {
  minimumFractionDigits: 2,
  // Four covers the tightest tick sizes in scope without inventing precision the venue does not quote.
  maximumFractionDigits: 4,
  useGrouping: false,
});

const USD = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

/** A price, as the venue quotes it. */
export function formatPrice(price: number): string {
  return PRICE.format(price);
}

/**
 * Money with an explicit sign, because the sign *is* the meaning: `−$412` at the stop and `+$912` at the target
 * are the two halves of the decision. A true minus sign (U+2212), not a hyphen — it aligns in tabular numerals.
 */
export function formatSignedUsd(amount: number): string {
  const magnitude = USD.format(Math.abs(amount));
  return amount < 0 ? `−${magnitude}` : `+${magnitude}`;
}

/** Reward ÷ risk as the wireframe writes it — `2.2R`. */
export function formatRatio(ratio: number): string {
  return `${ratio.toFixed(1)}R`;
}

/**
 * A bar size as a trader says it: `15m`, `1h`, `4h`, `1d`. Anything that does not divide cleanly stays in
 * minutes rather than becoming `1.5h`, which nobody charts in.
 */
export function formatTimeframe(minutes: number): string {
  if (minutes % 1440 === 0 && minutes >= 1440) {
    return `${String(minutes / 1440)}d`;
  }
  if (minutes % 60 === 0 && minutes >= 60) {
    return `${String(minutes / 60)}h`;
  }
  return `${String(minutes)}m`;
}

/**
 * The cited signal, as one legible citation: `RSI(14) · 15m`. The three fields are copied onto the suggestion at
 * issuance precisely so the citation survives the trigger being edited afterwards, so they are shown together —
 * an indicator without its period and resolution is not a citation, it is a name.
 */
export function formatCitation(
  indicator: string,
  period: number,
  resolutionMinutes: number,
): string {
  return `${indicator}(${String(period)}) · ${formatTimeframe(resolutionMinutes)}`;
}

/** A wall-clock time for a journal-ish line ("issued 09:41:22"). Date-less: these rows live for minutes. */
export function formatClockTime(isoTimestamp: string): string {
  const parsed = new Date(isoTimestamp);
  if (Number.isNaN(parsed.getTime())) {
    // A malformed timestamp is the server's problem, not a reason to blank the card or throw inside a render.
    return '—';
  }
  return parsed.toLocaleTimeString('en-US', { hour12: false });
}
