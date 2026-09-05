import { describe, expect, it } from 'vitest';

import type { DailyRealizedPnL } from '../api/journal';
import {
  centralDay,
  dayLabel,
  equityCurve,
  monthGrid,
  monthLabel,
  monthOf,
  monthStats,
  monthWindow,
  shadeIntensity,
  shiftMonth,
} from './month';

const DAYS: readonly DailyRealizedPnL[] = [
  { date: '2026-09-01', realizedPnL: 1250, tradeCount: 3 },
  { date: '2026-09-02', realizedPnL: -400, tradeCount: 2 },
  { date: '2026-09-03', realizedPnL: 0, tradeCount: 1 },
  { date: '2026-09-04', realizedPnL: '150.50', tradeCount: 1 },
];

describe('centralDay', () => {
  it('reads the Central trading day, not the browser\u2019s', () => {
    // The server groups by the Central calendar day. An operator in Europe at 02:00 local on the 5th is
    // still on the 4th in Chicago, and defaulting the calendar to their own date would open the journal on
    // a day the server reports nothing for.
    expect(centralDay(new Date('2026-09-05T02:00:00Z'))).toBe('2026-09-04');
  });

  it('rolls to the next Central day only once Chicago midnight passes', () => {
    // 2026-09-05 05:00Z is 00:00 CDT.
    expect(centralDay(new Date('2026-09-05T04:59:00Z'))).toBe('2026-09-04');
    expect(centralDay(new Date('2026-09-05T05:00:00Z'))).toBe('2026-09-05');
  });

  it('tracks the standard-time offset too, not a hard-coded one', () => {
    // In January, Chicago is UTC-6: 06:00Z is midnight.
    expect(centralDay(new Date('2027-01-15T05:59:00Z'))).toBe('2027-01-14');
    expect(centralDay(new Date('2027-01-15T06:00:00Z'))).toBe('2027-01-15');
  });
});

describe('monthOf / shiftMonth / monthWindow', () => {
  it('takes the month a day belongs to', () => {
    expect(monthOf('2026-09-04')).toBe('2026-09');
  });

  it('steps months without rolling the year wrong', () => {
    expect(shiftMonth('2026-09', -1)).toBe('2026-08');
    expect(shiftMonth('2026-01', -1)).toBe('2025-12');
    expect(shiftMonth('2026-12', 1)).toBe('2027-01');
  });

  it('spans the whole month, ends included', () => {
    expect(monthWindow('2026-09')).toStrictEqual({ from: '2026-09-01', to: '2026-09-30' });
    expect(monthWindow('2026-02')).toStrictEqual({ from: '2026-02-01', to: '2026-02-28' });
    // A leap February is 29 days -- an off-by-one here silently drops the last trading day of the month.
    expect(monthWindow('2028-02')).toStrictEqual({ from: '2028-02-01', to: '2028-02-29' });
  });

  it('labels a month and a day without shifting either into the browser\u2019s zone', () => {
    // Formatting a `YYYY-MM-DD` through a local-time Date is the classic off-by-one: west of UTC it prints
    // the day before.
    expect(monthLabel('2026-09')).toBe('September 2026');
    expect(dayLabel('2026-09-01')).toBe('Tuesday, September 1');
  });
});

describe('monthStats', () => {
  it('sums the month net across every traded day', () => {
    expect(monthStats(DAYS).net).toBeCloseTo(1000.5, 6);
  });

  it('counts green and red days, and a flat day as neither', () => {
    // A day that closed exactly flat is not a winning day and not a losing one. Rounding it into either
    // makes the green/red split a lie the operator reviews their discipline against.
    const stats = monthStats(DAYS);
    expect(stats.greenDays).toBe(2);
    expect(stats.redDays).toBe(1);
    expect(stats.flatDays).toBe(1);
  });

  it('names the best and worst days', () => {
    const stats = monthStats(DAYS);
    expect(stats.best?.date).toBe('2026-09-01');
    expect(stats.worst?.date).toBe('2026-09-02');
  });

  it('averages over traded days, not calendar days', () => {
    // Dividing by 30 would report an average nobody traded.
    expect(monthStats(DAYS).averagePerDay).toBeCloseTo(1000.5 / 4, 6);
  });

  it('totals the month\u2019s trades', () => {
    expect(monthStats(DAYS).tradeCount).toBe(7);
  });

  it('reports no average at all for a month with no traded days, never zero', () => {
    // `$0.00 average` claims the operator traded to breakeven. They did not trade.
    const stats = monthStats([]);
    expect(stats.averagePerDay).toBeNull();
    expect(stats.best).toBeNull();
    expect(stats.worst).toBeNull();
    expect(stats.net).toBe(0);
  });
});

describe('equityCurve', () => {
  it('accumulates in date order regardless of the order the rows arrive in', () => {
    const shuffled = [DAYS[1]!, DAYS[3]!, DAYS[0]!, DAYS[2]!];

    expect(equityCurve(shuffled).map((point) => point.date)).toStrictEqual([
      '2026-09-01',
      '2026-09-02',
      '2026-09-03',
      '2026-09-04',
    ]);
    expect(equityCurve(shuffled).map((point) => point.cumulative)).toStrictEqual([
      1250, 850, 850, 1000.5,
    ]);
  });

  it('is empty for a month with nothing realized', () => {
    expect(equityCurve([])).toStrictEqual([]);
  });
});

describe('monthGrid', () => {
  it('lays the month out Sunday-first with leading blanks for the offset', () => {
    // 2026-09-01 is a Tuesday, so Sunday and Monday of the first week are blank.
    const weeks = monthGrid('2026-09', DAYS);

    expect(weeks[0]?.length).toBe(7);
    expect(weeks[0]?.[0]).toBeNull();
    expect(weeks[0]?.[1]).toBeNull();
    expect(weeks[0]?.[2]?.date).toBe('2026-09-01');
  });

  it('holds every day of the month exactly once', () => {
    const cells = monthGrid('2026-09', DAYS)
      .flat()
      .filter((cell) => cell !== null);

    expect(cells).toHaveLength(30);
    expect(new Set(cells.map((cell) => cell.date)).size).toBe(30);
  });

  it('attaches the realized row to its own day', () => {
    const cells = monthGrid('2026-09', DAYS).flat();
    const first = cells.find((cell) => cell?.date === '2026-09-01');

    expect(first?.realizedPnL).toBe(1250);
    expect(first?.tradeCount).toBe(3);
  });

  it('leaves an untraded day untraded rather than showing it as a flat $0 day', () => {
    // The endpoint returns no entry for a quiet day. Filling one in with a zero would paint the calendar
    // with days the operator never traded, which reads as "I broke even" instead of "I stood aside".
    const cells = monthGrid('2026-09', DAYS).flat();
    const quiet = cells.find((cell) => cell?.date === '2026-09-10');

    expect(quiet?.realizedPnL).toBeNull();
    expect(quiet?.tradeCount).toBe(0);
  });

  it('ignores a row outside the month rather than drawing it somewhere wrong', () => {
    const cells = monthGrid('2026-09', [
      ...DAYS,
      { date: '2026-08-31', realizedPnL: 999, tradeCount: 1 },
    ])
      .flat()
      .filter((cell) => cell !== null);

    expect(cells.some((cell) => cell.realizedPnL === 999)).toBe(false);
  });
});

describe('shadeIntensity', () => {
  it('scales magnitude against the month\u2019s largest absolute day', () => {
    expect(shadeIntensity(1000, 1000)).toBe(1);
    expect(shadeIntensity(-500, 1000)).toBe(0.5);
  });

  it('never divides by zero on a month whose every day was flat', () => {
    expect(shadeIntensity(0, 0)).toBe(0);
  });
});
