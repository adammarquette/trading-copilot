import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Divider from '@mui/material/Divider';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState } from 'react';

import { ModeChip } from '../accounts/ModeChip';
import {
  OrderSide,
  type StagedTicket,
  type Suggestion,
  SuggestionState,
  passSuggestion,
  takeSuggestion,
  SuggestionOrigin,
} from '../api/suggestions';
import {
  formatCitation,
  formatClockTime,
  formatPrice,
  formatRatio,
  formatSignedUsd,
  formatTimeframe,
} from './format';
import { PassDialog } from './PassDialog';
import { useValidity, ValidityCountdown } from './ValidityCountdown';

/**
 * One suggestion, fully specified (R-4) — the surface where the AI proposes and the operator disposes.
 *
 * Three rules shape everything below, and each of them is a thing that has gone wrong in decision-support UIs:
 *
 *  1. **Nothing fully specified hides behind a click.** Direction, timeframe, entry, stop, target, size, the
 *     rationale with its cited signal, confidence and the validity window are all on the face of the card. A
 *     suggestion the operator has to expand is a suggestion they will judge on the half they can see.
 *  2. **Confidence is information, never a call to action.** R-4: it "never moves size, geometry or the gate, and
 *     a zero still issues." So it gets a neutral chip — no colour ramp implying *act on this*, no effect on any
 *     control, and the list never sorts by it. A 0% suggestion is exactly as takeable as a 99% one.
 *  3. **Take arms; it does not send.** `POST /take` stages an editable ticket and stops (R-11b). The operator
 *     reviews and sends it separately, from the order ticket.
 */

export interface SuggestionCardProps {
  readonly suggestion: Suggestion;
  /**
   * The current market price for this instrument, when the host surface has a live one. It is the value the
   * server re-measures against the entry tolerance at take time (R-12) and the fat-finger band's reference at
   * the gate (R-16) — so it is **never** defaulted to the suggestion's own entry, which would make the drift
   * re-check measure zero drift every time. Absent (the live-quote surface is gh#645/#646, not built), the card
   * asks the operator for it rather than inventing one.
   */
  readonly referencePrice?: number | null;
  /** Fired once the pass is durably recorded — the list drops the card from the actionable set on this. */
  readonly onPassed?: (id: string) => void;
  /**
   * Fired once a ticket is staged, carrying the suggestion that armed it (the host needs its requested size and
   * summary to review the staged ticket). The handoff to the order ticket is the host surface's to make (gh#655).
   */
  readonly onArmed?: (suggestion: Suggestion, ticket: StagedTicket) => void;
}

type Act = 'take' | 'pass';

type ActionState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'busy'; readonly act: Act }
  /** The request completed and the answer was no. Carries the server's own words — see the refusal block below. */
  | {
      readonly kind: 'refused';
      readonly act: Act;
      readonly reason: string;
      readonly status: number;
      readonly layer?: string;
    }
  /** The request did not complete, or came back with no answer. Retry is meaningful; a refusal's is not. */
  | { readonly kind: 'failed'; readonly act: Act; readonly error: string }
  | { readonly kind: 'armed'; readonly ticket: StagedTicket }
  | { readonly kind: 'passed' };

const STATE_LABEL: Record<SuggestionState, string> = {
  [SuggestionState.Unknown]: 'Unknown',
  [SuggestionState.Active]: 'Active',
  [SuggestionState.Stale]: 'Stale',
  [SuggestionState.ExpiredVoid]: 'Expired / void',
};

/**
 * Reads the operator-typed reference price strictly. `Number.parseFloat` would happily read `"52ab"` as `52`, and
 * a mistyped price is the one input on this card that reaches a risk calculation.
 */
function parseReference(text: string): number | null {
  const trimmed = text.trim();
  if (!/^\d+(?:\.\d+)?$/.test(trimmed)) {
    return null;
  }
  const value = Number(trimmed);
  return value > 0 ? value : null;
}

/** One price of the geometry, with what it is worth in dollars beneath it. */
function Level({
  name,
  price,
  detail,
  detailColour,
  testId,
}: {
  readonly name: string;
  readonly price: number;
  readonly detail: string;
  readonly detailColour: string;
  readonly testId: string;
}) {
  return (
    <Box data-testid={testId} sx={{ flex: 1, minWidth: 92 }}>
      <Typography
        variant="caption"
        sx={{ display: 'block', color: 'text.disabled', letterSpacing: '.06em', fontSize: 10 }}
      >
        {name}
      </Typography>
      <Typography variant="numeric" sx={{ fontSize: 16, fontWeight: 600, display: 'block' }}>
        {formatPrice(price)}
      </Typography>
      <Typography variant="numeric" sx={{ fontSize: 11, color: detailColour }}>
        {detail}
      </Typography>
    </Box>
  );
}

export function SuggestionCard({
  suggestion,
  referencePrice = null,
  onPassed,
  onArmed,
}: SuggestionCardProps) {
  const [action, setAction] = useState<ActionState>({ kind: 'idle' });
  const [passOpen, setPassOpen] = useState(false);
  const [typedReference, setTypedReference] = useState('');

  const { expired } = useValidity(suggestion.expiresAt);
  const isLong = suggestion.side === OrderSide.Buy;

  // Read the PRODUCER, never the shape of the citation (gh#1134). Zeroed citation fields also mean "the read forgot
  // to include the cited-factor set" -- a server bug -- and a card that inferred "chat" from them would quietly
  // relabel that bug as a feature instead of rendering it wrongly in a way somebody notices.
  const isChatProposal = suggestion.origin === SuggestionOrigin.Chat;
  const busy = action.kind === 'busy';
  const settled = action.kind === 'armed' || action.kind === 'passed';

  const reference =
    referencePrice !== null && referencePrice > 0 ? referencePrice : parseReference(typedReference);

  /**
   * Why Take is unavailable — or `null` when it is available. Note what is **not** here: confidence, the reward /
   * risk ratio, and the model's opinion of its own setup. The only things that stop an arm are the absence of the
   * one input the take needs, and a window this card can see has already closed.
   *
   * Expiry gates locally as well as at the server because the operator is looking at a card that says `expired`;
   * offering "Approve & arm" beside that would be offering something we know will be refused. The server's own
   * re-check (R-12) remains authoritative — if its clock disagrees with ours, its refusal is what renders.
   */
  const takeUnavailable = expired
    ? 'The validity window has closed, so this can no longer be armed.'
    : reference === null
      ? 'Enter the current market price — the take is re-checked against it before anything is armed.'
      : null;

  const summary = `${isLong ? 'LONG' : 'SHORT'} ${suggestion.instrument} · ${formatPrice(
    suggestion.entryPrice,
  )}${suggestion.rewardRiskRatio === null ? '' : ` · ${formatRatio(suggestion.rewardRiskRatio)}`}`;

  async function runTake() {
    if (reference === null) {
      return;
    }
    setAction({ kind: 'busy', act: 'take' });
    const result = await takeSuggestion(suggestion.id, reference);
    if (result.ok) {
      setAction({ kind: 'armed', ticket: result.data });
      onArmed?.(suggestion, result.data);
      return;
    }
    setAction(
      result.kind === 'refused'
        ? {
            kind: 'refused',
            act: 'take',
            reason: result.reason,
            status: result.status,
            ...(result.layer === undefined ? {} : { layer: result.layer }),
          }
        : { kind: 'failed', act: 'take', error: result.error },
    );
  }

  async function runPass(reasons: number, note: string) {
    setAction({ kind: 'busy', act: 'pass' });
    const result = await passSuggestion(suggestion.id, reasons, note);
    if (result.ok) {
      setPassOpen(false);
      setAction({ kind: 'passed' });
      // Notified last, because the list removes this card on it — nothing may touch state afterwards.
      onPassed?.(suggestion.id);
      return;
    }
    setPassOpen(false);
    setAction(
      result.kind === 'refused'
        ? { kind: 'refused', act: 'pass', reason: result.reason, status: result.status }
        : { kind: 'failed', act: 'pass', error: result.error },
    );
  }

  return (
    <Paper
      elevation={1}
      data-testid="suggestion-card"
      data-suggestion-id={suggestion.id}
      data-state={STATE_LABEL[suggestion.state]}
      data-expired={expired ? 'true' : 'false'}
      sx={{
        p: 2,
        borderRadius: 2,
        border: '1px solid',
        borderColor: 'divider',
        // An expired card stays legible but visibly steps back: it is journal, not a decision.
        opacity: expired ? 0.72 : 1,
      }}
    >
      <Stack direction="row" flexWrap="wrap" useFlexGap sx={{ gap: 0.75, alignItems: 'center' }}>
        <Chip
          label={`${isLong ? 'LONG' : 'SHORT'} ${suggestion.instrument}`}
          size="small"
          data-testid="suggestion-side"
          data-side={isLong ? 'long' : 'short'}
          sx={(theme) => ({
            // The semantic scale, never `primary`: direction is a trading fact and must not read as brand chrome.
            bgcolor: isLong
              ? theme.palette.trading.longContainer
              : theme.palette.trading.shortContainer,
            color: isLong ? theme.palette.trading.long : theme.palette.trading.short,
            fontWeight: 700,
            letterSpacing: '.03em',
          })}
        />

        {/* A chat proposal is not framed on a bar size at all (gh#1134), so the chip is ABSENT rather than showing
            `0m`. A zero here is the server's null-object default for "no cited factor", and rendering it would
            assert a 0-minute timeframe -- a fact, and a false one, on a surface that commits real money. */}
        {isChatProposal ? null : (
          <Chip
            label={formatTimeframe(suggestion.timeframeMinutes)}
            size="small"
            variant="outlined"
            data-testid="suggestion-timeframe"
            title="The bar size this setup is framed on"
          />
        )}

        {/* Provenance, stated. Neutral outlined chrome like the confidence chip beside it: which producer proposed
            a setup is context for the operator's judgement, never a recommendation to act on it. */}
        {isChatProposal ? (
          <Chip
            label="from chat"
            size="small"
            variant="outlined"
            color="default"
            data-testid="suggestion-origin"
            data-origin="chat"
            title="The co-pilot proposed this in chat, at your request. It is staged only -- nothing is placed until you take it."
          />
        ) : null}

        {/* Display only. Neutral by construction: outlined, default colour, identical at 0% and at 99%. The
            moment this gets a colour ramp it starts reading as "act on this", which R-4 forbids. */}
        <Chip
          label={`conf ${String(suggestion.confidence)}%`}
          size="small"
          variant="outlined"
          color="default"
          data-testid="suggestion-confidence"
          data-confidence={suggestion.confidence}
          title="The model's confidence — information only; it moves no size, geometry or gate"
        />

        <Chip
          label={STATE_LABEL[suggestion.state]}
          size="small"
          variant="outlined"
          data-testid="suggestion-state"
        />

        <ModeChip mode={suggestion.mode} />

        <ValidityCountdown expiresAt={suggestion.expiresAt} />

        <Typography
          variant="numeric"
          sx={{
            ml: 'auto',
            fontSize: 10.5,
            color: 'primary.main',
            bgcolor: 'primary.light',
            px: 1,
            py: 0.25,
            borderRadius: 5,
          }}
        >
          R-4
        </Typography>
      </Stack>

      {/* The one transition worth interrupting a screen-reader for. The countdown itself is not a live region. */}
      <Box role="status" aria-live="polite" sx={{ mt: expired ? 1 : 0 }}>
        {expired ? (
          <Typography variant="body2" data-testid="expired-notice" sx={{ color: 'text.secondary' }}>
            The validity window has closed — this suggestion is no longer takeable.
          </Typography>
        ) : null}
      </Box>

      <Stack
        direction="row"
        flexWrap="wrap"
        useFlexGap
        sx={{ gap: 2, mt: 1.5, alignItems: 'flex-start' }}
      >
        <Level
          name="ENTRY"
          price={suggestion.entryPrice}
          detail="market"
          detailColour="text.disabled"
          testId="suggestion-entry"
        />
        <Level
          name="STOP"
          price={suggestion.stopPrice}
          // A missing dollar figure means the instrument has no configured contract spec. Say so rather than
          // showing a plausible-looking zero — a guessed tick size is worse than an absent number.
          detail={
            suggestion.riskUsd === null
              ? 'no contract spec'
              : formatSignedUsd(-Math.abs(suggestion.riskUsd))
          }
          detailColour={suggestion.riskUsd === null ? 'text.disabled' : 'trading.short'}
          testId="suggestion-stop"
        />
        <Level
          name="TARGET"
          price={suggestion.targetPrice}
          detail={[
            suggestion.rewardUsd === null ? null : formatSignedUsd(Math.abs(suggestion.rewardUsd)),
            suggestion.rewardRiskRatio === null ? null : formatRatio(suggestion.rewardRiskRatio),
          ]
            .filter((part) => part !== null)
            .join(' · ')}
          detailColour={suggestion.rewardUsd === null ? 'text.disabled' : 'trading.long'}
          testId="suggestion-target"
        />
        <Box data-testid="suggestion-size" sx={{ flex: 1, minWidth: 92 }}>
          <Typography
            variant="caption"
            sx={{ display: 'block', color: 'text.disabled', letterSpacing: '.06em', fontSize: 10 }}
          >
            SIZE
          </Typography>
          <Typography variant="numeric" sx={{ fontSize: 16, fontWeight: 600, display: 'block' }}>
            {suggestion.size}
          </Typography>
          <Typography variant="numeric" sx={{ fontSize: 11, color: 'text.disabled' }}>
            {suggestion.size === 1 ? 'contract' : 'contracts'}
          </Typography>
        </Box>
      </Stack>

      <Divider sx={{ my: 1.5 }} />

      {/* Untrusted display data: rendered as text, which JSX escapes. It is never markup and never re-enters a
          prompt as instruction. */}
      <Typography variant="body2" data-testid="suggestion-rationale" sx={{ color: 'text.primary' }}>
        {suggestion.rationale}
      </Typography>
      {/* Only a scan-issued setup HAS a cited signal. A chat proposal's citation fields are the server's
          null-object defaults, and `cited signal · (0) · 0m` reads as a MALFORMED scan suggestion rather than an
          honest proposal from another producer -- so it says what it actually is instead (gh#1134). */}
      <Typography
        variant="numeric"
        data-testid="suggestion-citation"
        sx={{ display: 'block', mt: 0.5, fontSize: 11.5, color: 'text.secondary' }}
      >
        {isChatProposal
          ? 'proposed in chat · no cited signal'
          : `cited signal · ${formatCitation(
              suggestion.citedIndicator,
              suggestion.citedPeriod,
              suggestion.citedResolutionMinutes,
            )}`}
      </Typography>

      <Typography
        variant="caption"
        data-testid="suggestion-meta"
        sx={{ display: 'block', mt: 1, color: 'text.disabled' }}
      >
        {`issued ${formatClockTime(suggestion.createdAt)} · v${String(suggestion.version)}`}
        {suggestion.supersedesId === null ? '' : ' · supersedes an earlier version'}
        {suggestion.stateChangedAt === null
          ? ''
          : ` · ${STATE_LABEL[suggestion.state].toLowerCase()} at ${formatClockTime(suggestion.stateChangedAt)}`}
      </Typography>

      {action.kind === 'refused' ? (
        <Alert
          severity="warning"
          data-testid={`${action.act}-refusal`}
          data-status={action.status}
          sx={{ mt: 1.5 }}
        >
          {/*
            THE REFUSAL, IN THE SERVER'S OWN WORDS. `POST /take` re-validates at take time (R-12) — still Active,
            in-window, un-dispositioned, spec-resolvable, un-drifted — and each of those answers with its own
            sentence. "The market has drifted past this suggestion's entry tolerance" and "This suggestion's
            validity window has passed" are different facts about different problems, and the operator's next move
            differs accordingly. So the reason is rendered verbatim and is never summarised, mapped to a code, or
            replaced by a generic failure. The title says only which act was refused; the body says why.
          */}
          <AlertTitle sx={{ mb: 0.25 }}>
            {action.act === 'take' ? 'Not armed' : 'Not passed'}
          </AlertTitle>
          {action.reason}
          {action.layer === undefined ? null : (
            <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
              bound by {action.layer}
            </Typography>
          )}
        </Alert>
      ) : null}

      {action.kind === 'failed' ? (
        // Distinct from a refusal on purpose: this one did not complete, so retrying is meaningful.
        <Alert severity="error" data-testid={`${action.act}-failure`} sx={{ mt: 1.5 }}>
          <AlertTitle sx={{ mb: 0.25 }}>Could not reach the co-pilot</AlertTitle>
          {action.error} Nothing was {action.act === 'take' ? 'armed' : 'recorded'}; try again.
        </Alert>
      ) : null}

      {action.kind === 'armed' ? (
        <Alert severity="info" data-testid="armed-ticket" sx={{ mt: 1.5 }}>
          <AlertTitle sx={{ mb: 0.25 }}>Armed — not sent</AlertTitle>
          {`Ticket staged for ${String(action.ticket.approvedQuantity)} of ${String(suggestion.size)} contract${
            suggestion.size === 1 ? '' : 's'
          }. Review and send it from the order ticket — arming and sending are separate steps.`}
          <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
            gate: {action.ticket.outcome} · {action.ticket.reason}
          </Typography>
        </Alert>
      ) : null}

      {action.kind === 'passed' ? (
        <Alert severity="success" data-testid="passed-notice" sx={{ mt: 1.5 }}>
          Passed. It stays in the journal by id and feeds the feedback loop.
        </Alert>
      ) : null}

      {settled ? null : (
        <Stack
          direction="row"
          flexWrap="wrap"
          useFlexGap
          sx={{ gap: 1, mt: 2, alignItems: 'flex-start' }}
        >
          {referencePrice === null ? (
            <TextField
              // Asked for, never assumed. The live-quote surface is not built (gh#645/#646); until it is, the
              // operator supplies the price they can see, exactly as every other order path takes it from the
              // caller. Pre-filling this with the entry would silently disable the R-12 drift re-check.
              label="Market price now"
              size="small"
              required
              value={typedReference}
              onChange={(event) => {
                setTypedReference(event.target.value);
              }}
              slotProps={{
                htmlInput: {
                  inputMode: 'decimal',
                  'data-testid': 'reference-price',
                  style: { fontFamily: 'var(--tc-mono)' },
                },
              }}
              sx={{ width: 168 }}
            />
          ) : (
            <Box data-testid="reference-price-live" sx={{ minWidth: 120 }}>
              <Typography
                variant="caption"
                sx={{
                  display: 'block',
                  color: 'text.disabled',
                  letterSpacing: '.06em',
                  fontSize: 10,
                }}
              >
                MARKET NOW
              </Typography>
              <Typography variant="numeric" sx={{ fontSize: 16, fontWeight: 600 }}>
                {formatPrice(referencePrice)}
              </Typography>
            </Box>
          )}

          <Stack sx={{ gap: 0.5 }}>
            <Stack direction="row" sx={{ gap: 1 }}>
              <Button
                variant="contained"
                onClick={() => {
                  void runTake();
                }}
                disabled={busy || takeUnavailable !== null}
                data-testid="take-button"
              >
                Approve &amp; arm
              </Button>
              <Button
                variant="outlined"
                color="inherit"
                onClick={() => {
                  setPassOpen(true);
                }}
                // A pass is accepted on any lifecycle state — an expired setup's note is still worth keeping, and
                // it is exactly the input the R-9 loop reads. So this is never disabled by the clock.
                disabled={busy}
                data-testid="pass-button"
              >
                Pass
              </Button>
            </Stack>
            {takeUnavailable === null ? null : (
              <Typography
                variant="caption"
                data-testid="take-unavailable"
                sx={{ color: 'text.secondary', maxWidth: '44ch' }}
              >
                {takeUnavailable}
              </Typography>
            )}
          </Stack>
        </Stack>
      )}

      <PassDialog
        open={passOpen}
        summary={summary}
        busy={busy}
        onCancel={() => {
          setPassOpen(false);
        }}
        onPass={(reasons, note) => {
          void runPass(reasons, note);
        }}
      />
    </Paper>
  );
}
