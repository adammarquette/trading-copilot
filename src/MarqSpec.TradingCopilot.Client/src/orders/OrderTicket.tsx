import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import ButtonGroup from '@mui/material/ButtonGroup';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import { useOptionalAccounts } from '../accounts/AccountProvider';
import { TradingMode } from '../api/accounts';
import { DefaultEntryAction, getRiskProfile } from '../api/risk';
import {
  ConditionalCrossDirection,
  type ConditionalOrderResponse,
  type CreateConditionalOrderRequest,
  OrderType,
  SELECTABLE_ORDER_TYPES,
  type SendOrderRequest,
  type StagedOrderResponse,
  armOrder,
  cancelConditionalOrder,
  cancelOrder,
  createConditionalOrder,
  editStagedOrder,
  sendAsIsOrder,
  takeStagedOrder,
} from '../api/orders';
import { ConditionalPending } from './ConditionalPending';
import { SELECTABLE_DIRECTIONS, directionLabel, isTriggerFireable } from './conditional';
import { GateDecisionPanel } from './GateDecisionPanel';
import { describeSizing } from './gateDecision';

/**
 * The order ticket (gh#655, R-11 / R-12, ADR-0007) — the surface that sends real orders.
 *
 * **Arm → review → send stays three steps, and the middle one is the point.** Arming stages the order and returns
 * the gate's decision *without* reaching the venue; sending is a separate, explicit action against the staged row.
 * That opt-in posture is R-11's, and collapsing it into one click for convenience would make the gate's decision
 * something the operator skips past rather than reads.
 *
 * **Send transmits the staged order, never the proposal.** The staged row already carries the gate-approved size;
 * re-sending the proposal would transmit the *requested* quantity and make the gate advisory — the failure
 * ADR-0007 names directly. `GateDecisionPanel` owns whether a decision is sendable at all, so the send control
 * follows the gate's verdict rather than the operator's intent.
 *
 * **`TrailingStop` is never offered.** The send path refuses it outright and this neutral ticket carries no trail
 * distance, so a control offering it would always fail — absent beats always-failing. That exclusion lives in
 * `SELECTABLE_ORDER_TYPES` rather than here, so it cannot drift.
 *
 * A refusal — of the arm or of the send — is rendered, not swallowed: a refused send leaves the ticket **armed**,
 * because the staged row still exists server-side and the operator needs the reason in front of them to decide
 * whether to amend or cancel.
 */

/** The entry the operator is proposing, plus the account it belongs to. */
export interface OrderProposal extends SendOrderRequest {
  readonly accountId: string;
}

const TYPE_LABELS: Readonly<Record<number, string>> = {
  [OrderType.Market]: 'Market',
  [OrderType.Limit]: 'Limit',
  [OrderType.Stop]: 'Stop',
  [OrderType.StopLimit]: 'Stop limit',
};

export function OrderTicket({ proposal }: { readonly proposal: OrderProposal }) {
  const [staged, setStaged] = useState<StagedOrderResponse | null>(null);
  const [sent, setSent] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [type, setType] = useState<OrderType>(proposal.type);
  const [pending, setPending] = useState(false);

  /**
   * The amend step between arm and send (gh#828, R-11 / R-12, ADR-0007). An operator who armed the wrong size had
   * to cancel and re-arm; this edits the staged row in place instead.
   *
   * `current` is the proposal **as the server now holds it** — the armed one until an edit lands, then the edited
   * one. Everything the surface says about "what you asked for" reads from here rather than from `proposal`,
   * because an edit re-gates and the gate's answer must be compared against the number the operator actually
   * asked for last. Comparing it to the originally armed size would report a resize that did not happen, and a
   * false resize on this surface teaches the operator to ignore the real one.
   */
  const [current, setCurrent] = useState<OrderProposal>(proposal);
  const [editing, setEditing] = useState(false);
  const [quantityDraft, setQuantityDraft] = useState('');
  const [entryDraft, setEntryDraft] = useState('');
  const editedQuantity = Number(quantityDraft);
  const editedEntry = Number(entryDraft);
  // Fail closed, exactly as the conditional's cancel band does: a blank or non-numeric field would serialize to
  // NaN -> null and silently amend the ticket to something the operator never typed.
  const editValid =
    Number.isFinite(editedQuantity) && editedQuantity > 0 && Number.isFinite(editedEntry);

  /**
   * The entry-action split button (gh#828, gh#218): the operator's declared default acts on click, the other
   * action sits in the menu. Two rules decide what it may offer, and both fail toward review-first.
   *
   * **The roster must vouch for the account.** Resolved from the OPTIONAL context, so a ticket mounted without a
   * provider degrades to plain arm → review → send instead of throwing: the fast path is a convenience, and a
   * menu whose entries cannot be vouched for is worse than no menu.
   *
   * **Defaulting to `SendAsIs` is practice-only (gh#218).** The preference can only be *declared* on a practice
   * account, but an account's mode can change under a stored one — so the mode is re-checked here at render
   * rather than trusted from when it was written. On anything but practice it is demoted out of the primary
   * click: "skip the review" must never become the one-click default on real money. It stays reachable from the
   * menu, where choosing it is an explicit gesture, and every gate still runs on it either way.
   */
  const accounts = useOptionalAccounts();
  const accountMode =
    accounts?.status === 'ready'
      ? (accounts.accounts.find((candidate) => candidate.id === proposal.accountId)?.mode ?? null)
      : null;
  const [defaultAction, setDefaultAction] = useState<DefaultEntryAction>(
    DefaultEntryAction.ApproveAndArm,
  );
  const [entryMenuAnchor, setEntryMenuAnchor] = useState<HTMLElement | null>(null);
  const mounted = useRef(true);
  const profileToken = useRef(0);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  useEffect(() => {
    if (accountMode === null) {
      return; // nothing to offer, so nothing to read
    }
    const token = (profileToken.current += 1);
    void getRiskProfile(proposal.accountId).then((result) => {
      // A superseded read (the ticket re-targeted) or a resolve after unmount must not land — and an undeclared
      // or unreadable profile resolves to ApproveAndArm, the enum's zero, which is the fail-safe answer.
      if (!mounted.current || token !== profileToken.current) {
        return;
      }
      setDefaultAction(
        result.ok && result.data !== null
          ? result.data.defaultEntryAction
          : DefaultEntryAction.ApproveAndArm,
      );
    });
  }, [proposal.accountId, accountMode]);
  const fastPathOffered = accountMode !== null;
  const sendAsIsPrimary =
    fastPathOffered &&
    defaultAction === DefaultEntryAction.SendAsIs &&
    accountMode === TradingMode.Practice;

  // "Send when conditions met" (gh#655, R-11 / R-12): the opt-in on-trigger mode. Off by default, so the common
  // path stays the three-step arm → review → send. The trigger is drafted as text and parsed on submit; the
  // direction defaults to a breakout (rises to), and the cancel band / expiry are optional.
  const [whenConditionsMet, setWhenConditionsMet] = useState(false);
  const [conditional, setConditional] = useState<ConditionalOrderResponse | null>(null);
  const [triggerDraft, setTriggerDraft] = useState('');
  const [direction, setDirection] = useState<ConditionalCrossDirection>(
    ConditionalCrossDirection.RisesTo,
  );
  const [expiryDraft, setExpiryDraft] = useState('');
  const [cancelDraft, setCancelDraft] = useState('');
  const triggerPrice = Number(triggerDraft);
  // A blank cancel band is fine — it is optional. A non-blank one that is not a finite number is NOT: `Number('5,0')`
  // is `NaN`, which `JSON.stringify` serializes to `null`, silently discarding the operator's stale-cancel intent
  // with no error. Fail closed instead — the create control stays disabled until it is a real number or cleared.
  const cancelBandValid = cancelDraft.trim() === '' || Number.isFinite(Number(cancelDraft));
  const canCreateConditional = isTriggerFireable(triggerPrice, direction) && cancelBandValid;
  /**
   * The re-entrancy guard, a REF rather than the `pending` state above. State updates are not applied until the
   * next render, so two clicks dispatched in the same tick would both read the old value and both fire; a ref is
   * written synchronously and so actually excludes the second. `pending` exists to disable the controls -- that
   * is the visible half, this is the correct half.
   *
   * It matters most on Send: order transmission is not idempotent (ProjectX ADR-0002 -- "a retried timeout can
   * place a second live order"), so a double-click could put the gate-approved size on the account twice.
   */
  const inFlight = useRef(false);

  const begin = useCallback(() => {
    if (inFlight.current) {
      return false;
    }
    inFlight.current = true;
    setPending(true);
    return true;
  }, []);

  const finish = useCallback(() => {
    inFlight.current = false;
    setPending(false);
  }, []);

  // Switching modes clears any stale alert, so a prior "Sent …" success or a refusal from the other flow never
  // lingers beside the mode the operator just moved to. Only reachable pre-flight — the toggle is locked while a
  // request is in flight and once either flow has produced a staged order or a pending conditional.
  const handleToggleMode = useCallback((checked: boolean) => {
    setWhenConditionsMet(checked);
    setRefusal(null);
    setSent(null);
  }, []);

  const handleArm = useCallback(() => {
    if (!begin()) {
      return; // a second arm would stage a SECOND order and orphan whichever row resolved first
    }
    setRefusal(null);
    setSent(null);
    const { accountId, ...order } = proposal;
    void armOrder(accountId, { ...order, type })
      .then((result) => {
        if (!result.ok) {
          setRefusal(refusalText(result));
          return;
        }
        // A fresh arm re-bases what was asked for: any earlier edit belonged to a ticket that no longer exists.
        setCurrent(proposal);
        setStaged(result.data);
      })
      .finally(finish);
  }, [begin, finish, proposal, type]);

  /**
   * The fast path (R-11, gh#218): one action instead of arm → review → send. It stages nothing, so there is no
   * ticket left behind to review or cancel — and it is a real transmission, which is why it takes the same
   * synchronous re-entrancy guard as Send rather than relying on the disabled state.
   */
  const handleSendAsIs = useCallback(() => {
    setEntryMenuAnchor(null);
    if (!begin()) {
      return; // transmission is not idempotent — a second click must not place a second live order
    }
    setRefusal(null);
    setSent(null);
    const { accountId, order } = splitProposal({ ...current, type });
    void sendAsIsOrder(accountId, order)
      .then((result) => {
        if (!result.ok) {
          // The fast path skips the review, never the gate — a refusal here is the gate's answer, and nothing
          // was staged, so there is no armed ticket to fall back to.
          setRefusal(refusalText(result));
          return;
        }
        setSent(`Sent ${result.data.approvedQuantity} — ${result.data.reason}`);
      })
      .finally(finish);
  }, [begin, finish, current, type]);

  const handleArmFromMenu = useCallback(() => {
    setEntryMenuAnchor(null);
    handleArm();
  }, [handleArm]);

  const handleOpenEdit = useCallback(() => {
    setQuantityDraft(String(current.quantity));
    setEntryDraft(String(current.entry));
    setRefusal(null);
    setEditing(true);
  }, [current]);

  const handleDiscardEdit = useCallback(() => {
    setEditing(false);
    setRefusal(null);
  }, []);

  const handleApplyEdit = useCallback(() => {
    // Fail closed on an unusable draft even though Apply is disabled for it — a NaN quantity would reach the
    // server as null and amend the ticket to something nobody typed.
    if (staged === null || !editValid) {
      return;
    }
    const next: OrderProposal = { ...current, quantity: editedQuantity, entry: editedEntry, type };
    if (!begin()) {
      return; // never a second amendment of the same staged row
    }
    setRefusal(null);
    // The account is already on the staged row — an edit addresses the order by id, so only the order travels.
    const { order } = splitProposal(next);
    void editStagedOrder(staged.orderId, order)
      .then((result) => {
        if (!result.ok) {
          // A refused edit changed NOTHING server-side, so the armed decision still describes what a send would
          // transmit. Keep it — and keep the form open with the reason, so the operator can amend and retry.
          setRefusal(refusalText(result));
          return;
        }
        // The edit re-gated: this decision, and this asked-for size, replace the ones the arm produced.
        setCurrent(next);
        setStaged(result.data);
        setEditing(false);
      })
      .finally(finish);
  }, [begin, finish, staged, current, editValid, editedQuantity, editedEntry, type]);

  const handleSend = useCallback(() => {
    if (staged === null || !begin()) {
      return; // never a second transmission of the same staged order
    }
    setRefusal(null);
    void takeStagedOrder(staged.orderId)
      .then((result) => {
        if (!result.ok) {
          // The staged row survives a refused send, so the ticket stays armed and the reason stays on screen.
          setRefusal(refusalText(result));
          return;
        }
        setSent(`Sent ${result.data.approvedQuantity} — ${result.data.reason}`);
        setStaged(null);
      })
      .finally(finish);
  }, [begin, finish, staged]);

  const handleCancel = useCallback(() => {
    if (staged === null || !begin()) {
      return;
    }
    void cancelOrder(staged.orderId)
      .then(() => {
        setStaged(null);
        setRefusal(null);
        // The row is gone; drop any amendment with it, so a re-arm starts from the original proposal.
        setEditing(false);
        setCurrent(proposal);
      })
      .finally(finish);
  }, [begin, finish, staged, proposal]);

  const handleCreateConditional = useCallback(() => {
    // Fail closed on an unfireable trigger or an invalid cancel band even though the control is disabled for them —
    // a conditional that could never fire would rest forever, and a NaN cancel band would silently serialize to null.
    if (!isTriggerFireable(triggerPrice, direction) || !cancelBandValid) {
      return;
    }
    const { accountId, ...order } = proposal;
    // Build the request BEFORE begin(). Every conversion here is synchronous, and a throw (an Invalid Date from a
    // malformed expiry) must happen before the re-entrancy guard is set — otherwise `finish` never runs and the
    // ticket wedges disabled. begin() is the last thing before the transmit-nothing round-trip.
    const request: CreateConditionalOrderRequest = {
      order: { ...order, type },
      triggerPrice,
      triggerDirection: direction,
      cancelDrift: cancelDraft.trim() === '' ? undefined : Number(cancelDraft),
      expiresAt: expiryDraft.trim() === '' ? undefined : new Date(expiryDraft).toISOString(),
    };
    if (!begin()) {
      return; // never a second pending conditional for the same proposal
    }
    setRefusal(null);
    setSent(null);
    void createConditionalOrder(accountId, request)
      .then((result) => {
        if (!result.ok) {
          // A pre-gate refusal (mode / mismatch / wrong-side) means nothing coherent was held — show it, do not
          // pretend a conditional is pending.
          setRefusal(refusalText(result));
          return;
        }
        setConditional(result.data);
      })
      .finally(finish);
  }, [
    begin,
    finish,
    proposal,
    type,
    triggerPrice,
    direction,
    cancelBandValid,
    cancelDraft,
    expiryDraft,
  ]);

  const handleWithdrawConditional = useCallback(() => {
    if (conditional === null || !begin()) {
      return; // never a second DELETE of the same conditional
    }
    setRefusal(null);
    void cancelConditionalOrder(conditional.conditionalOrderId)
      .then((result) => {
        if (!result.ok) {
          // A refused withdrawal (a mid-fire conditional the server will not cancel) leaves the pending panel up —
          // the conditional is still live, so the surface must not imply it is gone. The reason stays on screen.
          setRefusal(refusalText(result));
          return;
        }
        // The row is gone server-side; clear the panel. `whenConditionsMet` stays on, so the trigger form returns
        // and a fresh conditional can be composed without re-toggling the mode.
        setConditional(null);
      })
      .finally(finish);
  }, [begin, finish, conditional]);

  return (
    <Box
      data-testid="order-ticket"
      sx={{ display: 'flex', flexDirection: 'column', gap: 1.5, maxWidth: 460 }}
    >
      {/* Reads the CURRENT proposal, so an applied edit is reflected here rather than leaving the header
          contradicting the decision panel below it. */}
      <Typography variant="subtitle2">
        {current.symbol} — {current.quantity} @ {current.entry}
      </Typography>

      {/* The catastrophic floor rests at the venue on every order (R-5). Stated, never assumed: "insured" is the
          promise this ticket makes, and an operator should be able to read it rather than trust it. */}
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        Stop {proposal.stop} · safety stop {proposal.safetyStop} (always at the venue)
      </Typography>

      <TextField
        select
        size="small"
        label="Type"
        value={type}
        onChange={(event) => setType(Number(event.target.value) as OrderType)}
        slotProps={{ select: { native: true } }}
        disabled={pending || staged !== null || conditional !== null}
      >
        {SELECTABLE_ORDER_TYPES.map((selectable) => (
          <option key={selectable} value={selectable}>
            {TYPE_LABELS[selectable]}
          </option>
        ))}
      </TextField>

      {/* The opt-in on-trigger mode (R-11 / R-12). Locked while a request is in flight AND once either flow has
          produced a staged order or a created conditional — so a mid-round-trip toggle cannot land an arm's result
          in the conditional branch (orphaning the staged row) or an immediate order on top of a pending conditional. */}
      <FormControlLabel
        control={
          <Checkbox
            size="small"
            checked={whenConditionsMet}
            onChange={(event) => handleToggleMode(event.target.checked)}
            disabled={pending || staged !== null || conditional !== null}
          />
        }
        label="Send when conditions met"
      />

      {whenConditionsMet && conditional === null ? (
        <>
          <TextField
            size="small"
            label="Trigger price"
            value={triggerDraft}
            onChange={(event) => setTriggerDraft(event.target.value)}
            inputMode="decimal"
          />
          <TextField
            select
            size="small"
            label="Direction"
            value={direction}
            onChange={(event) =>
              setDirection(Number(event.target.value) as ConditionalCrossDirection)
            }
            slotProps={{ select: { native: true } }}
          >
            {SELECTABLE_DIRECTIONS.map((selectable) => (
              <option key={selectable} value={selectable}>
                {directionLabel(selectable)}
              </option>
            ))}
          </TextField>
          <TextField
            size="small"
            type="datetime-local"
            label="Expiry (optional)"
            value={expiryDraft}
            onChange={(event) => setExpiryDraft(event.target.value)}
            slotProps={{ inputLabel: { shrink: true } }}
          />
          <TextField
            size="small"
            label="Cancel band (optional)"
            value={cancelDraft}
            onChange={(event) => setCancelDraft(event.target.value)}
            inputMode="decimal"
          />
        </>
      ) : null}

      {staged !== null ? (
        <GateDecisionPanel
          requested={current.quantity}
          decision={{
            outcome: staged.outcome,
            approvedQuantity: staged.approvedQuantity,
            bindingLayer: staged.bindingLayer,
            reason: staged.reason,
          }}
        />
      ) : null}

      {conditional !== null ? <ConditionalPending conditional={conditional} /> : null}

      {/* The amendment form (gh#828). Only the two fields an operator actually gets wrong on an armed ticket:
          the size and the entry. The safety stop is deliberately absent — the R-5 floor is not something this
          surface may move — and the type stays locked because the staged row was gated as that type. */}
      {editing ? (
        <>
          <TextField
            size="small"
            label="Quantity"
            value={quantityDraft}
            onChange={(event) => setQuantityDraft(event.target.value)}
            inputMode="numeric"
          />
          <TextField
            size="small"
            label="Entry"
            value={entryDraft}
            onChange={(event) => setEntryDraft(event.target.value)}
            inputMode="decimal"
          />
        </>
      ) : null}

      {refusal !== null ? <Alert severity="error">{refusal}</Alert> : null}
      {sent !== null ? <Alert severity="success">{sent}</Alert> : null}

      <Stack direction="row" spacing={1}>
        {whenConditionsMet ? (
          conditional === null ? (
            <Button
              variant="contained"
              onClick={handleCreateConditional}
              disabled={pending || !canCreateConditional}
            >
              Send on trigger
            </Button>
          ) : (
            // A pending conditional will place a real order when it fires (R-12), so the operator can pull it back
            // before then. It is held off the book, so this is a plain server-side delete — nothing rests to cancel.
            <Button onClick={handleWithdrawConditional} disabled={pending}>
              Withdraw
            </Button>
          )
        ) : staged === null ? (
          fastPathOffered ? (
            <>
              <ButtonGroup variant="contained">
                <Button onClick={sendAsIsPrimary ? handleSendAsIs : handleArm} disabled={pending}>
                  {sendAsIsPrimary ? 'Send as-is' : 'Arm'}
                </Button>
                <Button
                  size="small"
                  aria-label="Entry options"
                  onClick={(event) => setEntryMenuAnchor(event.currentTarget)}
                  disabled={pending}
                >
                  ▾
                </Button>
              </ButtonGroup>
              <Menu
                anchorEl={entryMenuAnchor}
                open={entryMenuAnchor !== null}
                onClose={() => setEntryMenuAnchor(null)}
              >
                {/* Exactly the action that is NOT primary — a menu that repeats the button teaches nothing. */}
                {sendAsIsPrimary ? (
                  <MenuItem onClick={handleArmFromMenu}>Approve &amp; arm</MenuItem>
                ) : (
                  <MenuItem onClick={handleSendAsIs}>Send as-is</MenuItem>
                )}
              </Menu>
            </>
          ) : (
            <Button variant="contained" onClick={handleArm} disabled={pending}>
              Arm
            </Button>
          )
        ) : editing ? (
          // Send is deliberately absent while an edit is open: a typed-but-unapplied size is not what the server
          // holds, so a send here would transmit the PRE-edit row while the operator reads the number they typed.
          <>
            <Button variant="contained" onClick={handleApplyEdit} disabled={pending || !editValid}>
              Apply
            </Button>
            <Button onClick={handleDiscardEdit} disabled={pending}>
              Discard
            </Button>
          </>
        ) : (
          <>
            {describeSizing(current.quantity, staged).sendable ? (
              <Button variant="contained" color="primary" onClick={handleSend} disabled={pending}>
                Send
              </Button>
            ) : null}
            {/* An edit re-gates, so it is offered even when the armed decision is unsendable — amending a
                blocked ticket down to a size the gate allows is the point, not a dead end. */}
            <Button onClick={handleOpenEdit} disabled={pending}>
              Edit
            </Button>
            <Button onClick={handleCancel} disabled={pending}>
              Cancel
            </Button>
          </>
        )}
      </Stack>
    </Box>
  );
}

/**
 * Splits a proposal into the account it belongs to and the order itself. The two travel **separately** on every
 * order path — the account in the URL (arm, conditional) or already on the staged row (edit, take) — never as a
 * field of the order body, so the account an order acts on is never something a request body can disagree about.
 */
function splitProposal(proposal: OrderProposal): {
  readonly accountId: string;
  readonly order: SendOrderRequest;
} {
  const { accountId, ...order } = proposal;
  return { accountId, order };
}

function refusalText(result: {
  readonly kind: string;
  readonly error?: string;
  readonly reason?: string;
}): string {
  return result.kind === 'refused'
    ? (result.reason ?? 'Refused.')
    : (result.error ?? 'The request failed.');
}
