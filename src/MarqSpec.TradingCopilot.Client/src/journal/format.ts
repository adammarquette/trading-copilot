import { toNumber } from '../settings/format';

/**
 * How the journal renders money (gh#659). Fixed `en-US`, matching the rest of the app — a locale rendering
 * `5.230,25` beside a chart reading `5230.25` is a misread waiting to happen.
 */
const SIGNED_USD = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
  // `exceptZero`, not `always`: a profit and a loss are each worth a sign the operator can scan without
  // relying on colour, but a flat result is neither, and `+$0.00` would claim it was a win.
  signDisplay: 'exceptZero',
});

/**
 * A realized figure with its sign — `+$1,250.00` / `-$400.00` / `$0.00`, and an em dash for an absent value.
 * Never zero for absence: `$0.00` says "traded to breakeven", which is a claim, where `—` says "nothing here".
 */
export function formatSignedUsd(value: number | string | null): string {
  const amount = toNumber(value);
  return amount === null ? '—' : SIGNED_USD.format(amount);
}

/** Which way a realized figure leans, for the colour that carries it at a glance. */
export type Tone = 'positive' | 'negative';

/**
 * The tone a realized figure has earned — from the figure, never from the tile it sits in (gh#659 review).
 *
 * A tile whose tone is fixed by its label asserts a sign its value may not have: `Best day` in a month where
 * every day lost renders a loss in the long colour, and a scratch trade painted green claims a win it did not
 * make. Colour is the glance on this surface, so a wrong glance is a wrong reading even when the digits and the
 * sign beside them are right. Flat is `undefined` — neither side, and `$0.00` should not borrow a colour.
 */
export function toneOf(value: number | string | null): Tone | undefined {
  const amount = toNumber(value);
  if (amount === null || amount === 0) {
    return undefined;
  }
  return amount > 0 ? 'positive' : 'negative';
}

/** `1 trade` / `3 trades` — pluralized, because "1 trades" in a review surface reads as a bug. */
export function formatTradeCount(count: number): string {
  return `${String(count)} ${count === 1 ? 'trade' : 'trades'}`;
}

/**
 * A price as the journal shows it: the wire's decimal, trailing zeros trimmed, so `5241.00` reads `5241` the
 * way the operator wrote it on the ticket. `—` when absent — a price is never guessed.
 */
export function formatPrice(value: number | string | null): string {
  const price = toNumber(value);
  return price === null ? '—' : String(price);
}
