import {
  ConsistencyEnforcement,
  DefaultEntryAction,
  FloorSource,
  SizingBasis,
  TrailingMode,
} from '../api/risk';

/**
 * How the settings surface renders the declared risk profile and its headroom.
 *
 * Fixed `en-US` money formatting, matching the workspace's number convention (a locale that renders `5.230,25`
 * beside a chart reading `5230.25` is a misread waiting to happen). A `decimal` can arrive string-encoded, so every
 * display helper normalizes the wire value first rather than trusting its runtime type.
 */

const USD = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

const PERCENT = new Intl.NumberFormat('en-US', {
  style: 'percent',
  minimumFractionDigits: 0,
  maximumFractionDigits: 1,
});

/** A `decimal` off the wire (which may be string-encoded), or `null`, as a number — the one place the type is trusted. */
export function toNumber(value: number | string | null): number | null {
  if (value === null) {
    return null;
  }
  const parsed = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

/** Money, or an em dash when the value is absent — an undeclared limit reads as "none", never as `$0`. */
export function formatUsd(value: number | string | null): string {
  const amount = toNumber(value);
  return amount === null ? '—' : USD.format(amount);
}

/** A fraction in [0, 1] as a percentage, or an em dash when absent. */
export function formatFraction(value: number | string | null): string {
  const fraction = toNumber(value);
  return fraction === null ? '—' : PERCENT.format(fraction);
}

/** Reward ÷ risk as the wireframe writes it — `2.0R` — or an em dash when absent. */
export function formatRatio(value: number | string | null): string {
  const ratio = toNumber(value);
  return ratio === null ? '—' : `${ratio.toFixed(1)}R`;
}

/** A whole-number contract cap; 0 is the uncapped case and says so rather than showing a bare `0`. */
export function formatContractCap(value: number | string | null): string {
  const cap = toNumber(value);
  if (cap === null) {
    return '—';
  }
  return cap === 0 ? 'Uncapped' : String(cap);
}

export const floorSourceLabels: Record<FloorSource, string> = {
  [FloorSource.FirmImposed]: 'Firm-imposed',
  [FloorSource.SelfImposed]: 'Self-imposed',
};

export const trailingModeLabels: Record<TrailingMode, string> = {
  [TrailingMode.EndOfDay]: 'End of day',
  [TrailingMode.Intraday]: 'Intraday',
};

export const sizingBasisLabels: Record<SizingBasis, string> = {
  [SizingBasis.ActualStop]: 'Working stop',
  [SizingBasis.SafetyStop]: 'Safety stop',
};

export const consistencyEnforcementLabels: Record<ConsistencyEnforcement, string> = {
  [ConsistencyEnforcement.Advisory]: 'Warn only',
  [ConsistencyEnforcement.Block]: 'Refuse the order',
};

export const defaultEntryActionLabels: Record<DefaultEntryAction, string> = {
  [DefaultEntryAction.ApproveAndArm]: 'Approve & arm (review first)',
  [DefaultEntryAction.SendAsIs]: 'Send as-is (practice only)',
};
