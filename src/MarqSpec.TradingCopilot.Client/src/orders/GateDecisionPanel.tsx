import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';

import { type GateDecisionView, describeSizing, riskLayerName } from './gateDecision';

/**
 * What the risk gate decided, rendered before the send control (gh#655, R-5 / R-11, ADR-0007).
 *
 * The surface exists to make one rule visible: **the size transmitted is always the one the gate approved, never
 * the one asked for.** A silent resize is the worst thing this ticket could do — the operator would believe they
 * sent a size they did not — so a resize shows **both** numbers, and the send control is gated on this panel's
 * own verdict rather than on the operator's intent.
 *
 * Two shapes arrive here and they are not the same thing:
 *
 * - A **gate decision** (`Allowed` / `Resized` / `Blocked`) — the gate sized the order and can name the layer that
 *   bound it. "Blocked" alone is not an answer; "blocked by Daily governor" is.
 * - A **pre-gate refusal** (`RefusedByMode`, `RefusedByKillSwitch`, …) — nothing was ever sized, so there is no
 *   binding layer and a zero quantity means "not evaluated", not "zero contracts allowed". The outcome is read
 *   directly; no reason is inferred from the absent decision.
 */

/** The decision as the ticket holds it — the API shape, with the layer still numeric. */
export interface GateDecisionResult extends GateDecisionView {
  readonly bindingLayer: number | null;
  readonly reason: string;
}

export interface GateDecisionPanelProps {
  /** What the operator asked for — held by the ticket, since the server echoes only what it approved. */
  readonly requested: number;
  readonly decision: GateDecisionResult;
}

/**
 * Each pre-gate refusal, in its own words. Distinct messages on purpose: they are different problems with
 * different fixes, and a shared "order refused" would tell the operator nothing about which.
 *
 * The last two are **not** listed in gh#655 but exist on `ExecutionOutcome`; without them they would fall through
 * to the generic branch, which is the failure this table exists to avoid.
 */
const REFUSALS: Readonly<Record<string, string>> = {
  RefusedByMode: 'Refused: this environment will not trade that account mode.',
  RefusedByAccountState: 'Refused: the account is not in a state that can trade.',
  RefusedByMismatch: 'Refused: the ticket does not match the account or instrument it names.',
  RefusedByUnsupportedType: 'Refused: the venue does not support that order type.',
  RefusedByUnprotectableStop: 'Refused: that stop cannot be protected at the venue.',
  RefusedByInvalidTarget:
    'Refused: the take-profit target is not valid against the entry and stop.',
  RefusedByRisk: 'Refused by the risk gate before sizing.',
  RefusedByKillSwitch: 'Refused: the kill switch is engaged, so no order can be transmitted.',
};

const GATE_OUTCOMES: ReadonlySet<string> = new Set(['Allowed', 'Resized', 'Blocked']);

export function GateDecisionPanel({ requested, decision }: GateDecisionPanelProps) {
  const evaluated = GATE_OUTCOMES.has(decision.outcome);
  const sizing = describeSizing(requested, decision);
  // A pre-gate refusal never reached sizing, so it is never sendable whatever the quantities say.
  const sendable = evaluated && sizing.sendable;

  return (
    <Box
      data-testid="gate-decision"
      data-sendable={sendable ? 'true' : 'false'}
      sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}
    >
      {!evaluated ? (
        <Alert severity="error">
          {/* The outcome is the answer here -- nothing was sized, so there is no layer to name and no quantity
              to interpret. The server's own reason follows it. */}
          <Typography variant="body2">
            {REFUSALS[decision.outcome] ?? 'Refused before the risk gate.'}
          </Typography>
          <Typography variant="body2">{decision.reason}</Typography>
        </Alert>
      ) : sizing.noTrade ? (
        <Alert severity="warning">
          {/* "No trade" is an outcome, not an error and not an order of zero -- rendering a normal ticket showing
              size 0 would invite the operator to press send. */}
          <Typography variant="body2">
            <strong>No trade.</strong> The gate leaves room for zero contracts.
          </Typography>
          <Typography variant="body2">{describeBinding(decision)}</Typography>
          <Typography variant="body2">{decision.reason}</Typography>
        </Alert>
      ) : (
        <>
          {sizing.resized ? (
            <Alert severity="warning">
              <Typography variant="body2">
                <strong>Resized.</strong> You asked for {sizing.requested};{' '}
                <strong>{sizing.willSend}</strong> will be sent.
              </Typography>
              <Typography variant="body2">{describeBinding(decision)}</Typography>
            </Alert>
          ) : (
            <Typography variant="body2">
              Sending <strong>{sizing.willSend}</strong>.
            </Typography>
          )}
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {decision.reason}
          </Typography>
        </>
      )}
    </Box>
  );
}

/** Names the layer that bound the decision. The wire carries an integer, so printing the field would show a number. */
function describeBinding(decision: GateDecisionResult): string {
  return decision.bindingLayer === null
    ? 'No single layer bound this.'
    : `Bound by ${riskLayerName(decision.bindingLayer)}.`;
}
