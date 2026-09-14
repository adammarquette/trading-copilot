import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { GateDecisionPanel } from './GateDecisionPanel';
import { RiskLayer } from './gateDecision';

function panel() {
  return screen.getByTestId('gate-decision');
}

function shown() {
  return panel().textContent ?? '';
}

afterEach(cleanup);

describe('GateDecisionPanel', () => {
  it('shows only the one size when the gate approved what was asked', () => {
    render(
      <GateDecisionPanel
        requested={3}
        decision={{
          outcome: 'Allowed',
          approvedQuantity: 3,
          bindingLayer: null,
          reason: 'Within every layer.',
        }}
      />,
    );

    expect(shown()).toContain('3');
    expect(shown().toLowerCase()).not.toContain('resized');
  });

  it('shows BOTH sizes on a resize, so it cannot be missed', () => {
    // The single worst thing this surface could do is transmit a size the operator did not choose without saying
    // so (ADR-0007). Both numbers are present before the send control, never one.
    render(
      <GateDecisionPanel
        requested={5}
        decision={{
          outcome: 'Resized',
          approvedQuantity: 2,
          bindingLayer: RiskLayer.DailyGovernor,
          reason: 'The daily governor leaves room for 2.',
        }}
      />,
    );

    expect(shown()).toContain('5');
    expect(shown()).toContain('2');
  });

  it('names the binding layer rather than printing its integer', () => {
    // "Blocked" alone is not an answer; "blocked by Daily governor" is. The wire carries the layer as a NUMBER,
    // so a surface that printed the field would show the operator `2`.
    render(
      <GateDecisionPanel
        requested={5}
        decision={{
          outcome: 'Resized',
          approvedQuantity: 2,
          bindingLayer: RiskLayer.DailyGovernor,
          reason: 'The daily governor leaves room for 2.',
        }}
      />,
    );

    expect(shown()).toContain('Daily governor');
    expect(shown()).not.toMatch(/\blayer 2\b/i);
  });

  it('always shows the gate’s reason, which is always populated', () => {
    render(
      <GateDecisionPanel
        requested={5}
        decision={{
          outcome: 'Blocked',
          approvedQuantity: 0,
          bindingLayer: RiskLayer.DrawdownFloor,
          reason: 'The drawdown floor leaves no room today.',
        }}
      />,
    );

    expect(shown()).toContain('The drawdown floor leaves no room today.');
    expect(shown()).toContain('Drawdown floor');
  });

  it('renders no trade as its own outcome, not as an order of zero', () => {
    // The gate's "no trade" is first-class (gh#655) -- reached when the layers leave room for zero contracts.
    // Rendering a normal ticket showing size 0 would invite the operator to press send.
    render(
      <GateDecisionPanel
        requested={4}
        decision={{
          outcome: 'Resized',
          approvedQuantity: 0,
          bindingLayer: RiskLayer.PerTradeRisk,
          reason: 'Per-trade risk allows 0 contracts at this stop distance.',
        }}
      />,
    );

    expect(shown().toLowerCase()).toContain('no trade');
    expect(panel().getAttribute('data-sendable')).toBe('false');
  });

  it('marks a blocked decision unsendable', () => {
    render(
      <GateDecisionPanel
        requested={4}
        decision={{
          outcome: 'Blocked',
          approvedQuantity: 0,
          bindingLayer: RiskLayer.SanityCap,
          reason: 'Above the sanity cap.',
        }}
      />,
    );

    expect(panel().getAttribute('data-sendable')).toBe('false');
  });

  it('reads a pre-gate refusal from the OUTCOME, with no layer to name', () => {
    // A pre-gate refusal sized nothing, so it carries no decision -- the surface must not infer a reason from the
    // null layer or the zero quantity, and must not imply the gate weighed anything.
    render(
      <GateDecisionPanel
        requested={4}
        decision={{
          outcome: 'RefusedByKillSwitch',
          approvedQuantity: 0,
          bindingLayer: null,
          reason: 'The kill switch is engaged.',
        }}
      />,
    );

    expect(shown()).toContain('The kill switch is engaged.');
    expect(shown().toLowerCase()).not.toContain('risk layer');
    expect(panel().getAttribute('data-sendable')).toBe('false');
  });

  it.each([
    ['RefusedByMode', 'mode'],
    ['RefusedByAccountState', 'account'],
    ['RefusedByMismatch', 'does not match'],
    ['RefusedByUnsupportedType', 'type'],
    ['RefusedByUnprotectableStop', 'stop'],
    ['RefusedByInvalidTarget', 'target'],
  ])('gives %s its own message rather than a generic refusal', (outcome, expected) => {
    // Each refusal is distinct (gh#655), and the last two are ones the card itself does not list -- they exist on
    // ExecutionOutcome and would otherwise fall through to something uninformative.
    render(
      <GateDecisionPanel
        requested={4}
        decision={{ outcome, approvedQuantity: 0, bindingLayer: null, reason: 'server reason' }}
      />,
    );

    expect(shown().toLowerCase()).toContain(expected);
  });

  it('is sendable when the gate approved a positive size', () => {
    render(
      <GateDecisionPanel
        requested={2}
        decision={{ outcome: 'Allowed', approvedQuantity: 2, bindingLayer: null, reason: 'ok' }}
      />,
    );

    expect(panel().getAttribute('data-sendable')).toBe('true');
  });
});
