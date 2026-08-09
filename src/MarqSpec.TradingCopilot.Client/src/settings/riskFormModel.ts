import {
  ConsistencyEnforcement,
  DefaultEntryAction,
  type DeclareRiskProfileRequest,
  FloorSource,
  type RiskProfile,
  SizingBasis,
  TrailingMode,
} from '../api/risk';

/**
 * The editable shape behind the risk declaration form. Numeric fields are held as **strings** — an `<input>`'s
 * value is a string, an empty one means "not set", and parsing at the boundary (not on every keystroke) is what
 * lets a half-typed `-` or `.` exist without the field fighting the operator. Enums and booleans are held as their
 * real types because their controls produce them directly.
 *
 * The whole profile is edited and sent at once because the server stores it whole (a redeclaration overwrites every
 * field, never merges), so the form always round-trips every field — even the ones the operator did not touch.
 */
export interface RiskFormState {
  dailyLossLimit: string;
  accountProfitTarget: string;
  startingBalance: string;
  floorSource: FloorSource;
  trailingMode: TrailingMode;
  trailingAmount: string;
  locksAt: string;
  perTradeRiskFraction: string;
  targetRewardRatio: string;
  maxDrawdownPerTrade: string;
  dailyDrawdownGovernor: string;
  dailyProfitTarget: string;
  stopForDayAtProfitTarget: boolean;
  sizingBasis: SizingBasis;
  maxContractsPerOrder: string;
  maxBestDayFraction: string;
  consistencyEnforcement: ConsistencyEnforcement;
  defaultEntryAction: DefaultEntryAction;
  confirmSendAsIsDefault: boolean;
}

/** The starting point for an account that has no profile yet — sensible neutral defaults, nothing pre-filled to a limit. */
export const EMPTY_RISK_FORM: RiskFormState = {
  dailyLossLimit: '',
  accountProfitTarget: '',
  startingBalance: '',
  floorSource: FloorSource.FirmImposed,
  trailingMode: TrailingMode.EndOfDay,
  trailingAmount: '',
  locksAt: '',
  perTradeRiskFraction: '',
  targetRewardRatio: '',
  maxDrawdownPerTrade: '',
  dailyDrawdownGovernor: '',
  dailyProfitTarget: '',
  stopForDayAtProfitTarget: false,
  sizingBasis: SizingBasis.ActualStop,
  maxContractsPerOrder: '0',
  maxBestDayFraction: '',
  consistencyEnforcement: ConsistencyEnforcement.Advisory,
  defaultEntryAction: DefaultEntryAction.ApproveAndArm,
  confirmSendAsIsDefault: false,
};

/** A wire value (possibly string-encoded, possibly null) as the text an input shows — `null` becomes an empty field. */
function fieldText(value: number | string | null): string {
  return value === null ? '' : String(value);
}

/** Loads a declared profile (or the neutral defaults) into editable form state. */
export function toFormState(profile: RiskProfile | null): RiskFormState {
  if (profile === null) {
    return { ...EMPTY_RISK_FORM };
  }
  return {
    dailyLossLimit: fieldText(profile.dailyLossLimit),
    accountProfitTarget: fieldText(profile.accountProfitTarget),
    startingBalance: fieldText(profile.startingBalance),
    floorSource: profile.floorSource,
    trailingMode: profile.trailingMode,
    trailingAmount: fieldText(profile.trailingAmount),
    locksAt: fieldText(profile.locksAt),
    perTradeRiskFraction: fieldText(profile.perTradeRiskFraction),
    targetRewardRatio: fieldText(profile.targetRewardRatio),
    maxDrawdownPerTrade: fieldText(profile.maxDrawdownPerTrade),
    dailyDrawdownGovernor: fieldText(profile.dailyDrawdownGovernor),
    dailyProfitTarget: fieldText(profile.dailyProfitTarget),
    stopForDayAtProfitTarget: profile.stopForDayAtProfitTarget,
    sizingBasis: profile.sizingBasis,
    maxContractsPerOrder: fieldText(profile.maxContractsPerOrder),
    maxBestDayFraction: fieldText(profile.maxBestDayFraction),
    consistencyEnforcement: profile.consistencyEnforcement,
    defaultEntryAction: profile.defaultEntryAction,
    // Never pre-filled: it is a per-declaration gesture, not a stored value, so an edit re-earns it.
    confirmSendAsIsDefault: false,
  };
}

/** An optional numeric field: blank means "not set" (null); a non-numeric string is `NaN` so validation can catch it. */
function optionalNumber(text: string): number | null {
  const trimmed = text.trim();
  if (trimmed === '') {
    return null;
  }
  return Number(trimmed);
}

export type RiskFormErrors = Partial<Record<keyof RiskFormState, string>>;

/**
 * Client-side pre-validation — enough to stop an obviously invalid declaration at the form, never a copy of the
 * server's authority. The gate validates the whole profile and its refusal is what the operator ultimately sees;
 * this only catches the empties, non-numbers and out-of-range values the form itself can see, so a round-trip is
 * not spent to learn a field was left blank. The `SendAsIs`-needs-confirmation rule mirrors the server's 422 so the
 * operator learns it here rather than after a refused send.
 */
export function validate(form: RiskFormState): RiskFormErrors {
  const errors: RiskFormErrors = {};

  const requirePositive = (field: keyof RiskFormState, label: string) => {
    const value = optionalNumber(String(form[field]));
    if (value === null || !Number.isFinite(value) || value <= 0) {
      errors[field] = `${label} must be a positive number.`;
    }
  };

  requirePositive('startingBalance', 'Starting balance');
  requirePositive('trailingAmount', 'Trailing amount');
  requirePositive('targetRewardRatio', 'Target reward ratio');
  requirePositive('maxDrawdownPerTrade', 'Max drawdown per trade');
  requirePositive('dailyDrawdownGovernor', 'Daily governor');

  const fraction = optionalNumber(form.perTradeRiskFraction);
  if (fraction === null || !Number.isFinite(fraction) || fraction <= 0 || fraction > 1) {
    errors.perTradeRiskFraction = 'Per-trade risk fraction must be in (0, 1].';
  }

  const bestDay = optionalNumber(form.maxBestDayFraction);
  if (bestDay !== null && (!Number.isFinite(bestDay) || bestDay <= 0 || bestDay > 1)) {
    errors.maxBestDayFraction = 'Consistency target must be in (0, 1], or blank for none.';
  }

  const contracts = optionalNumber(form.maxContractsPerOrder);
  if (contracts === null || !Number.isInteger(contracts) || contracts < 0) {
    errors.maxContractsPerOrder = 'Max contracts per order must be a whole number (0 = uncapped).';
  }

  for (const field of [
    'dailyLossLimit',
    'accountProfitTarget',
    'locksAt',
    'dailyProfitTarget',
  ] as const) {
    const value = optionalNumber(form[field]);
    if (value !== null && !Number.isFinite(value)) {
      errors[field] = 'Enter a number, or leave blank for none.';
    }
  }

  if (form.defaultEntryAction === DefaultEntryAction.SendAsIs && !form.confirmSendAsIsDefault) {
    errors.confirmSendAsIsDefault = 'Confirm the send-as-is default explicitly to set it.';
  }

  return errors;
}

/** Builds the whole-profile request from form state. Call only once {@link validate} returns no errors. */
export function toRequest(form: RiskFormState): DeclareRiskProfileRequest {
  return {
    dailyLossLimit: optionalNumber(form.dailyLossLimit),
    accountProfitTarget: optionalNumber(form.accountProfitTarget),
    startingBalance: Number(form.startingBalance),
    floorSource: form.floorSource,
    trailingMode: form.trailingMode,
    trailingAmount: Number(form.trailingAmount),
    locksAt: optionalNumber(form.locksAt),
    perTradeRiskFraction: Number(form.perTradeRiskFraction),
    targetRewardRatio: Number(form.targetRewardRatio),
    maxDrawdownPerTrade: Number(form.maxDrawdownPerTrade),
    dailyDrawdownGovernor: Number(form.dailyDrawdownGovernor),
    dailyProfitTarget: optionalNumber(form.dailyProfitTarget),
    stopForDayAtProfitTarget: form.stopForDayAtProfitTarget,
    sizingBasis: form.sizingBasis,
    maxContractsPerOrder: Number(form.maxContractsPerOrder),
    maxBestDayFraction: optionalNumber(form.maxBestDayFraction),
    consistencyEnforcement: form.consistencyEnforcement,
    defaultEntryAction: form.defaultEntryAction,
    confirmSendAsIsDefault: form.confirmSendAsIsDefault,
  };
}
