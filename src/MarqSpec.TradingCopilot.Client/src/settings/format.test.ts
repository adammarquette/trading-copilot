import { describe, expect, it } from 'vitest';

import {
  ConsistencyEnforcement,
  DefaultEntryAction,
  FloorSource,
  SizingBasis,
  TrailingMode,
} from '../api/risk';
import {
  consistencyEnforcementLabels,
  defaultEntryActionLabels,
  floorSourceLabels,
  formatContractCap,
  formatFraction,
  formatRatio,
  formatUsd,
  sizingBasisLabels,
  toNumber,
  trailingModeLabels,
} from './format';

describe('settings/format — number normalization', () => {
  it('parses a string-encoded decimal off the wire', () => {
    expect(toNumber('1234.5')).toBe(1234.5);
  });

  it('passes a real number through', () => {
    expect(toNumber(42)).toBe(42);
  });

  it('treats null as absent', () => {
    expect(toNumber(null)).toBeNull();
  });

  it('treats an unparseable value as absent rather than NaN', () => {
    expect(toNumber('not a number')).toBeNull();
  });
});

describe('settings/format — display', () => {
  it('formats money as USD', () => {
    expect(formatUsd(1000)).toBe('$1,000.00');
  });

  it('renders an absent amount as an em dash, never $0', () => {
    expect(formatUsd(null)).toBe('—');
  });

  it('formats a fraction as a percentage', () => {
    expect(formatFraction(0.25)).toBe('25%');
  });

  it('renders an absent fraction as an em dash', () => {
    expect(formatFraction(null)).toBe('—');
  });

  it('formats reward:risk as an R multiple', () => {
    expect(formatRatio(2)).toBe('2.0R');
  });

  it('calls a zero contract cap uncapped, not a bare zero', () => {
    expect(formatContractCap(0)).toBe('Uncapped');
  });

  it('shows a real contract cap as its number', () => {
    expect(formatContractCap(3)).toBe('3');
  });
});

describe('settings/format — enum labels', () => {
  it('labels every FloorSource member', () => {
    expect(floorSourceLabels[FloorSource.FirmImposed]).toBe('Firm-imposed');
    expect(floorSourceLabels[FloorSource.SelfImposed]).toBe('Self-imposed');
  });

  it('labels every TrailingMode member', () => {
    expect(trailingModeLabels[TrailingMode.EndOfDay]).toBe('End of day');
    expect(trailingModeLabels[TrailingMode.Intraday]).toBe('Intraday');
  });

  it('labels every SizingBasis member', () => {
    expect(sizingBasisLabels[SizingBasis.ActualStop]).toBe('Working stop');
    expect(sizingBasisLabels[SizingBasis.SafetyStop]).toBe('Safety stop');
  });

  it('labels every ConsistencyEnforcement member', () => {
    expect(consistencyEnforcementLabels[ConsistencyEnforcement.Advisory]).toBe('Warn only');
    expect(consistencyEnforcementLabels[ConsistencyEnforcement.Block]).toBe('Refuse the order');
  });

  it('labels every DefaultEntryAction member', () => {
    expect(defaultEntryActionLabels[DefaultEntryAction.ApproveAndArm]).toContain('Approve');
    expect(defaultEntryActionLabels[DefaultEntryAction.SendAsIs]).toContain('practice');
  });
});
