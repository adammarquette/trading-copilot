import { describe, expect, it } from 'vitest';

import { RiskLayer, describeSizing, riskLayerName } from './gateDecision';

describe('riskLayerName', () => {
  it.each([
    [RiskLayer.DrawdownFloor, 'Drawdown floor'],
    [RiskLayer.DailyLossLimit, 'Daily loss limit'],
    [RiskLayer.DailyGovernor, 'Daily governor'],
    [RiskLayer.DailyProfitTarget, 'Daily profit target'],
    [RiskLayer.PerTradeRisk, 'Per-trade risk'],
    [RiskLayer.MaxDrawdownPerTrade, 'Max drawdown per trade'],
    [RiskLayer.ManualCap, 'Manual cap'],
    [RiskLayer.SanityCap, 'Sanity cap'],
    [RiskLayer.ConsistencyTarget, 'Consistency target'],
  ])('names layer %i', (layer, expected) => {
    expect(riskLayerName(layer)).toBe(expected);
  });

  it('names the two layers gh#655 omitted, at their real values', () => {
    // The card lists seven layers; the enum has nine, and the extras are NOT at the end -- DailyProfitTarget sits
    // at 3, in the middle. A client that mapped the card's seven positionally would label PerTradeRisk (4) as
    // DailyProfitTarget and shift everything after it: a plausible-looking WRONG reason on a refusal, which is
    // worse than none. These two pin the real values.
    expect(riskLayerName(3)).toBe('Daily profit target');
    expect(riskLayerName(8)).toBe('Consistency target');
  });

  it('never renders a bound decision as unexplained, even for an unknown layer', () => {
    // "Blocked" alone is not an answer (gh#655). If the server grows a tenth layer before this client does, the
    // operator must still get something specific enough to act on -- never an empty string.
    expect(riskLayerName(99)).toContain('99');
    expect(riskLayerName(99)).not.toBe('');
  });
});

describe('describeSizing', () => {
  it('reports an untouched size as sending what was asked', () => {
    const sizing = describeSizing(3, { outcome: 'Allowed', approvedQuantity: 3 });

    expect(sizing.resized).toBe(false);
    expect(sizing.willSend).toBe(3);
  });

  it('flags a resize, carrying BOTH sizes', () => {
    // The single worst thing this surface could do is send a size the operator did not choose without saying so
    // (ADR-0007). Both numbers travel together so the ticket cannot render one without the other.
    const sizing = describeSizing(5, { outcome: 'Resized', approvedQuantity: 2 });

    expect(sizing.resized).toBe(true);
    expect(sizing.requested).toBe(5);
    expect(sizing.willSend).toBe(2);
  });

  it('treats a zero approval as no trade, not as an allowed order of nothing', () => {
    // The gate's "no trade" is a first-class outcome (gh#655), reached when the layers leave room for zero
    // contracts. Rendering it as a normal ticket with size 0 would invite the operator to press send.
    const sizing = describeSizing(4, { outcome: 'Resized', approvedQuantity: 0 });

    expect(sizing.noTrade).toBe(true);
    expect(sizing.sendable).toBe(false);
  });

  it('refuses to send a blocked decision', () => {
    const sizing = describeSizing(4, { outcome: 'Blocked', approvedQuantity: 0 });

    expect(sizing.sendable).toBe(false);
    expect(sizing.noTrade).toBe(true);
  });

  it('detects a resize even when the server labels the outcome Allowed', () => {
    // Defence in depth, and the reason this compares NUMBERS rather than trusting the outcome label: if the two
    // quantities disagree, the operator is told -- whatever the outcome says. Trusting the label alone would let
    // a server-side labelling slip transmit a size the operator never saw.
    const sizing = describeSizing(5, { outcome: 'Allowed', approvedQuantity: 3 });

    expect(sizing.resized).toBe(true);
    expect(sizing.willSend).toBe(3);
  });

  it('is sendable only when the gate approved a positive size', () => {
    expect(describeSizing(2, { outcome: 'Allowed', approvedQuantity: 2 }).sendable).toBe(true);
    expect(describeSizing(2, { outcome: 'Resized', approvedQuantity: 1 }).sendable).toBe(true);
  });
});
