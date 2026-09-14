import type { DailyRealizedPnL } from '../api/journal';
import { toNumber } from '../settings/format';

/**
 * The journal's date and month arithmetic (gh#659, R-9), kept pure and away from the components so the two
 * ways a P&L calendar silently lies are testable on their own:
 *
 * 1. **The wrong "today".** The endpoint groups by the **Central** calendar day (`DailyRealizedReader`, via
 *    `MarketClock`). An operator in Europe at 02:00 local is still on yesterday in Chicago, and defaulting the
 *    calendar to their own date opens the journal on a day the server reports nothing for.
 * 2. **The local-time round trip.** A `YYYY-MM-DD` off the wire is a *day*, not an instant. `new Date('2026-09-01')`
 *    parses as UTC midnight and then renders in the browser's zone — a day early anywhere west of UTC. Every
 *    label and every grid position here is computed in **UTC** from the string, so the day named is the day
 *    the server meant.
 *
 * The only place the real clock enters is {@link centralDay}, and it takes the instant as an argument.
 */

/** A Central trading day, `YYYY-MM-DD`. */
export type IsoDay = string;

/** A calendar month, `YYYY-MM`. */
export type IsoMonth = string;

const CENTRAL_DAY = new Intl.DateTimeFormat('en-US', {
  timeZone: 'America/Chicago',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

const MONTH_LABEL = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  month: 'long',
  year: 'numeric',
});

const DAY_LABEL = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  weekday: 'long',
  month: 'long',
  day: 'numeric',
});

const SHORT_DAY_LABEL = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  month: 'long',
  day: 'numeric',
});

/**
 * The Central trading day `instant` falls on — the day key the journal endpoints group by.
 *
 * Assembled from `formatToParts` rather than a locale that happens to print ISO order, and the offset comes
 * from the zone database (so it tracks CDT/CST) rather than a constant nobody updates twice a year.
 */
export function centralDay(instant: Date): IsoDay {
  const parts = CENTRAL_DAY.formatToParts(instant);
  const part = (type: Intl.DateTimeFormatPartTypes) =>
    parts.find((candidate) => candidate.type === type)?.value ?? '';
  return `${part('year')}-${part('month')}-${part('day')}`;
}

/** The month a Central day belongs to. */
export function monthOf(day: IsoDay): IsoMonth {
  return day.slice(0, 7);
}

/** Splits `YYYY-MM` into its numeric year and 1-based month. */
function partsOf(month: IsoMonth): { year: number; monthNumber: number } {
  const [year = '0', monthNumber = '1'] = month.split('-');
  return { year: Number(year), monthNumber: Number(monthNumber) };
}

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

/** Steps `delta` months, rolling the year with it. */
export function shiftMonth(month: IsoMonth, delta: number): IsoMonth {
  const { year, monthNumber } = partsOf(month);
  // Date.UTC normalizes an out-of-range month into the neighbouring year, so December → January is arithmetic
  // rather than a special case anyone has to remember.
  const shifted = new Date(Date.UTC(year, monthNumber - 1 + delta, 1));
  return `${shifted.getUTCFullYear()}-${pad(shifted.getUTCMonth() + 1)}`;
}

/** How many days the month holds — day 0 of the next month is the last day of this one. */
function daysInMonth(month: IsoMonth): number {
  const { year, monthNumber } = partsOf(month);
  return new Date(Date.UTC(year, monthNumber, 0)).getUTCDate();
}

/** The inclusive `[from, to]` window covering the whole month — what the calendar read asks for. */
export function monthWindow(month: IsoMonth): { readonly from: IsoDay; readonly to: IsoDay } {
  return { from: `${month}-01`, to: `${month}-${pad(daysInMonth(month))}` };
}

/** The first day of the month, as a Central day. */
export function firstDayOf(month: IsoMonth): IsoDay {
  return `${month}-01`;
}

/** A day parsed as a UTC instant — the only safe way to feed a `YYYY-MM-DD` to `Intl`. */
function asUtc(day: IsoDay): Date {
  return new Date(`${day}T00:00:00Z`);
}

/** `September 2026`. */
export function monthLabel(month: IsoMonth): string {
  return MONTH_LABEL.format(asUtc(firstDayOf(month)));
}

/** `Tuesday, September 1` — the day-detail heading. */
export function dayLabel(day: IsoDay): string {
  return DAY_LABEL.format(asUtc(day));
}

/** `September 1` — the calendar cell's accessible name, where the weekday is already a column header. */
export function shortDayLabel(day: IsoDay): string {
  return SHORT_DAY_LABEL.format(asUtc(day));
}

/** The month's realized totals, as the wireframe's stat strip reads them. */
export interface MonthStats {
  /** The month's signed net across every traded day. */
  readonly net: number;
  readonly greenDays: number;
  readonly redDays: number;
  /** Days that closed exactly flat — neither a win nor a loss, and counted as neither. */
  readonly flatDays: number;
  readonly best: DailyRealizedPnL | null;
  readonly worst: DailyRealizedPnL | null;
  /**
   * Net ÷ **traded** days, or `null` when nothing was traded. Never `0`: `$0.00 average` claims the operator
   * traded to breakeven, and dividing by the calendar's 30 days reports an average nobody traded.
   */
  readonly averagePerDay: number | null;
  readonly tradeCount: number;
}

/** The realized value of a day, normalized off the wire; `0` if it somehow arrives unparseable. */
function realizedOf(day: DailyRealizedPnL): number {
  return toNumber(day.realizedPnL) ?? 0;
}

/**
 * A day's realized P&L in **whole cents**, and the reason every total here is accumulated that way.
 *
 * `realizedPnL` is a server-side `decimal`; JavaScript has only binary floats, and running `+=` over enough
 * of them drifts (`0.1 + 0.2 !== 0.3`). The number that drifts is the month **net** and the equity curve's
 * last point — the figure the operator reconciles against a broker statement — so a total that is a cent off
 * its own visible days reads as a bug in the journal rather than as rounding.
 *
 * Integer cents remove it outright: each day is rounded to the cent it is already displayed at (lossless for
 * any real money value), summed exactly, and converted back once at the end. `Math.round` rather than a
 * truncation, so a value that arrived as `12.339999999999998` lands on the cent it means.
 *
 * This is a *display* sum. It is not, and must not become, a basis for sizing or a risk decision — those live
 * below the model in server-side decimal (R-5).
 */
function centsOf(day: DailyRealizedPnL): number {
  return Math.round(realizedOf(day) * 100);
}

/** Cents back to the dollars the formatters take. One conversion, at the end of an exact integer sum. */
function fromCents(cents: number): number {
  return cents / 100;
}

/** The month's stat strip (R-9), derived from the day rows — never from a second server-side sum. */
export function monthStats(days: readonly DailyRealizedPnL[]): MonthStats {
  let netCents = 0;
  let greenDays = 0;
  let redDays = 0;
  let flatDays = 0;
  let tradeCount = 0;
  let best: DailyRealizedPnL | null = null;
  let worst: DailyRealizedPnL | null = null;

  for (const day of days) {
    const cents = centsOf(day);
    netCents += cents;
    tradeCount += day.tradeCount;
    if (cents > 0) greenDays += 1;
    else if (cents < 0) redDays += 1;
    else flatDays += 1;

    if (best === null || cents > centsOf(best)) best = day;
    if (worst === null || cents < centsOf(worst)) worst = day;
  }

  return {
    net: fromCents(netCents),
    greenDays,
    redDays,
    flatDays,
    best,
    worst,
    // Divides the EXACT cent total, and is deliberately left unrounded: the drift worth removing is the one
    // in the accumulation, and rounding the quotient here as well would round twice before the formatter has
    // rounded once.
    averagePerDay: days.length === 0 ? null : fromCents(netCents) / days.length,
    tradeCount,
  };
}

/** One point on the cumulative-P&L curve. */
export interface EquityPoint {
  readonly date: IsoDay;
  readonly cumulative: number;
}

/**
 * The month's equity curve — cumulative realized P&L, one point per traded day. Sorted here rather than
 * trusting the read's order: an accumulation over rows in the wrong order draws a curve that never happened.
 */
export function equityCurve(days: readonly DailyRealizedPnL[]): readonly EquityPoint[] {
  const ordered = [...days].sort((left, right) => left.date.localeCompare(right.date));
  // Integer cents, for the same reason `monthStats` uses them: the curve's last point IS the month net the
  // stat strip prints beside it, and the two disagreeing by a cent is worse than either being a cent off.
  let runningCents = 0;
  return ordered.map((day) => {
    runningCents += centsOf(day);
    return { date: day.date, cumulative: fromCents(runningCents) };
  });
}

/** One cell of the calendar grid; `null` in a week array is a leading/trailing blank, not a day. */
export interface CalendarCell {
  readonly date: IsoDay;
  readonly dayOfMonth: number;
  /**
   * The day's realized P&L, or `null` when the operator did not trade. `null` is **not** `0`: the endpoint
   * returns no row for a quiet day, and painting one as a flat `$0` reads as "I broke even" rather than
   * "I stood aside".
   */
  readonly realizedPnL: number | null;
  readonly tradeCount: number;
}

/**
 * The month laid out as Sunday-first weeks, matching the wireframe's `Sun … Sat` header. Rows from outside
 * the month are ignored rather than drawn somewhere wrong.
 */
export function monthGrid(
  month: IsoMonth,
  days: readonly DailyRealizedPnL[],
): readonly (readonly (CalendarCell | null)[])[] {
  const byDate = new Map(
    days.filter((day) => monthOf(day.date) === month).map((day) => [day.date, day]),
  );
  const total = daysInMonth(month);
  const leading = asUtc(firstDayOf(month)).getUTCDay();

  const cells: (CalendarCell | null)[] = Array.from({ length: leading }, () => null);
  for (let dayOfMonth = 1; dayOfMonth <= total; dayOfMonth += 1) {
    const date = `${month}-${pad(dayOfMonth)}`;
    const row = byDate.get(date);
    cells.push({
      date,
      dayOfMonth,
      realizedPnL: row === undefined ? null : realizedOf(row),
      tradeCount: row?.tradeCount ?? 0,
    });
  }
  while (cells.length % 7 !== 0) {
    cells.push(null);
  }

  const weeks: (CalendarCell | null)[][] = [];
  for (let index = 0; index < cells.length; index += 7) {
    weeks.push(cells.slice(index, index + 7));
  }
  return weeks;
}

/**
 * How strongly to shade a day, 0–1, against the month's largest absolute day — the wireframe's
 * "magnitude-shaded" calendar. Shading is a *secondary* read: the accessible name on every cell carries the
 * figure itself, so an operator who cannot see the hue loses nothing.
 */
export function shadeIntensity(value: number, maxAbsolute: number): number {
  if (maxAbsolute <= 0) {
    return 0;
  }
  return Math.min(1, Math.abs(value) / maxAbsolute);
}
