import { describe, expect, it } from 'vitest';

import {
  ConsistencyEnforcement,
  DefaultEntryAction,
  FloorSource,
  type RiskProfile,
  SizingBasis,
  TrailingMode,
} from '../api/risk';
import {
  EMPTY_RISK_FORM,
  type RiskFormState,
  toFormState,
  toRequest,
  validate,
} from './riskFormModel';

const PROFILE: RiskProfile = {
  accountId: 'a1',
  dailyLossLimit: 1000,
  accountProfitTarget: 3000,
  startingBalance: 50000,
  floorSource: FloorSource.SelfImposed,
  trailingMode: TrailingMode.Intraday,
  trailingAmount: 2000,
  locksAt: 52000,
  perTradeRiskFraction: 0.25,
  targetRewardRatio: 2,
  maxDrawdownPerTrade: 300,
  dailyDrawdownGovernor: 600,
  dailyProfitTarget: 900,
  stopForDayAtProfitTarget: true,
  sizingBasis: SizingBasis.SafetyStop,
  maxContractsPerOrder: 3,
  maxBestDayFraction: 0.5,
  consistencyEnforcement: ConsistencyEnforcement.Block,
  defaultEntryAction: DefaultEntryAction.ApproveAndArm,
};

/** A form state that passes validation, to mutate one field at a time. */
function validForm(overrides: Partial<RiskFormState> = {}): RiskFormState {
  return { ...toFormState(PROFILE), ...overrides };
}

describe('toFormState', () => {
  it('loads a declared profile into editable text, enums and booleans', () => {
    const form = toFormState(PROFILE);

    expect(form.startingBalance).toBe('50000');
    expect(form.dailyDrawdownGovernor).toBe('600');
    expect(form.floorSource).toBe(FloorSource.SelfImposed);
    expect(form.stopForDayAtProfitTarget).toBe(true);
  });

  it('renders an absent optional as an empty field, not a zero', () => {
    const form = toFormState({ ...PROFILE, dailyLossLimit: null, maxBestDayFraction: null });

    expect(form.dailyLossLimit).toBe('');
    expect(form.maxBestDayFraction).toBe('');
  });

  it('parses a string-encoded decimal off the wire', () => {
    const form = toFormState({ ...PROFILE, startingBalance: '49999.5' });

    expect(form.startingBalance).toBe('49999.5');
  });

  it('never pre-fills the send-as-is confirmation — it is re-earned each declaration', () => {
    const form = toFormState({ ...PROFILE, defaultEntryAction: DefaultEntryAction.SendAsIs });

    expect(form.confirmSendAsIsDefault).toBe(false);
  });

  it('starts an undeclared account from neutral defaults', () => {
    expect(toFormState(null)).toEqual(EMPTY_RISK_FORM);
  });
});

describe('validate', () => {
  it('passes a well-formed profile', () => {
    expect(validate(validForm())).toEqual({});
  });

  it('requires the positive fields to be positive', () => {
    const errors = validate(validForm({ startingBalance: '0', dailyDrawdownGovernor: '' }));

    expect(errors.startingBalance).toBeDefined();
    expect(errors.dailyDrawdownGovernor).toBeDefined();
  });

  it('holds the per-trade risk fraction inside (0, 1]', () => {
    expect(validate(validForm({ perTradeRiskFraction: '1.5' })).perTradeRiskFraction).toBeDefined();
    expect(validate(validForm({ perTradeRiskFraction: '0' })).perTradeRiskFraction).toBeDefined();
    expect(validate(validForm({ perTradeRiskFraction: '1' })).perTradeRiskFraction).toBeUndefined();
  });

  it('allows a blank consistency target (none) but rejects one out of range', () => {
    expect(validate(validForm({ maxBestDayFraction: '' })).maxBestDayFraction).toBeUndefined();
    expect(validate(validForm({ maxBestDayFraction: '2' })).maxBestDayFraction).toBeDefined();
  });

  it('requires a whole-number contract cap', () => {
    expect(validate(validForm({ maxContractsPerOrder: '2.5' })).maxContractsPerOrder).toBeDefined();
    expect(validate(validForm({ maxContractsPerOrder: '0' })).maxContractsPerOrder).toBeUndefined();
  });

  it('mirrors the server 422 — send-as-is default needs an explicit confirmation', () => {
    const withoutConfirm = validForm({
      defaultEntryAction: DefaultEntryAction.SendAsIs,
      confirmSendAsIsDefault: false,
    });
    expect(validate(withoutConfirm).confirmSendAsIsDefault).toBeDefined();

    const withConfirm = validForm({
      defaultEntryAction: DefaultEntryAction.SendAsIs,
      confirmSendAsIsDefault: true,
    });
    expect(validate(withConfirm).confirmSendAsIsDefault).toBeUndefined();
  });
});

describe('toRequest', () => {
  it('round-trips a loaded profile back to the same numbers', () => {
    const request = toRequest(toFormState(PROFILE));

    expect(request.startingBalance).toBe(50000);
    expect(request.dailyDrawdownGovernor).toBe(600);
    expect(request.perTradeRiskFraction).toBe(0.25);
    expect(request.maxContractsPerOrder).toBe(3);
  });

  it('sends an empty optional as null, never as zero', () => {
    const request = toRequest(validForm({ dailyLossLimit: '', maxBestDayFraction: '' }));

    expect(request.dailyLossLimit).toBeNull();
    expect(request.maxBestDayFraction).toBeNull();
  });

  it('carries the enums and the confirmation gesture through', () => {
    const request = toRequest(
      validForm({
        floorSource: FloorSource.FirmImposed,
        defaultEntryAction: DefaultEntryAction.SendAsIs,
        confirmSendAsIsDefault: true,
      }),
    );

    expect(request.floorSource).toBe(FloorSource.FirmImposed);
    expect(request.defaultEntryAction).toBe(DefaultEntryAction.SendAsIs);
    expect(request.confirmSendAsIsDefault).toBe(true);
  });
});
