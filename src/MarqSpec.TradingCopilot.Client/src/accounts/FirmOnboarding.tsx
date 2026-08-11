import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Step from '@mui/material/Step';
import StepContent from '@mui/material/StepContent';
import StepLabel from '@mui/material/StepLabel';
import Stepper from '@mui/material/Stepper';
import TextField from '@mui/material/TextField';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import Typography from '@mui/material/Typography';
import { type FormEvent, useState } from 'react';

import type { Account, Connection } from '../api/accounts';
import {
  AccountStage,
  createConnection,
  createFirm,
  declareConventions,
  discoverAccounts,
  type Firm,
  FirmType,
  type StageConvention,
} from '../api/onboarding';
import { ModeChip } from './ModeChip';

/**
 * The firm → conventions → connection → discover onboarding walk (gh#653, R-14/R-17, ADR-0016). A fresh deployment
 * has no accounts and nothing to trade until an operator registers a firm, declares what its stages mean, adds a
 * login and discovers the accounts under it — this is that walk, and its four writes populate the account context
 * the rest of the app scopes to. On finish it calls {@link FirmOnboardingProps.onComplete}, which reloads the
 * roster so every dependent surface re-scopes.
 *
 * Two safety-relevant shapes carry through, not decoration:
 * - **Conventions belong to the firm, never the platform** — an account's practice-vs-live comes from what the
 *   operator declares here, because deriving it from the venue's flag once classified every funded account as
 *   practice. A stage left undeclared resolves to Undeclared (tradeable nowhere), so the walk always declares
 *   Practice as not-at-risk rather than leaving discovered practice accounts stranded.
 * - **The credential key is a name, not a secret** — it names the server-side env entry the login lives under, so
 *   no credential is ever entered into or held by the browser (ADR-0015).
 *
 * Scope: **prop firms only** for now (gh#780). A brokerage's accounts cannot yet resolve to a mode — the per-stage
 * convention model has no stage to classify them (a brokerage account resolves to `AccountStage.Unknown`, which
 * cannot be declared) and no brokerage platform is wired — so the type toggle offers Brokerage as *not yet
 * supported* rather than completing a walk that would strand every discovered account as `Undeclared`.
 */
export interface FirmOnboardingProps {
  /** Called when the operator finishes — the surface reloads the roster so every dependent view re-scopes. */
  readonly onComplete: () => void;
}

/** The ordered steps of the walk. */
type Phase = 'firm' | 'conventions' | 'connection' | 'discover';

type ApiFailure =
  | { readonly kind: 'refused'; readonly reason: string }
  | { readonly kind: 'failed'; readonly error: string };

const STEP_LABELS: Record<Phase, string> = {
  firm: 'Firm',
  conventions: 'What each stage means',
  connection: 'Connection',
  discover: 'Discover accounts',
};

/** The only platform with a wired adapter today (ADR-0016); others ship as code, so they are not offered here. */
const WIRED_PLATFORM = 'projectx';

export function FirmOnboarding({ onComplete }: FirmOnboardingProps) {
  const [firm, setFirm] = useState<Firm | null>(null);
  const [connection, setConnection] = useState<Connection | null>(null);
  const [discovered, setDiscovered] = useState<readonly Account[] | null>(null);

  const [phase, setPhase] = useState<Phase>('firm');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Step 1 — the firm.
  const [firmName, setFirmName] = useState('');
  const [firmType, setFirmType] = useState<FirmType>(FirmType.PropFirm);
  // Step 2 — conventions. Practice is always declared not-at-risk (see the class note); only the staked stages
  // toggle. The wireframe's defaults: an evaluation is practice money, a funded account is at risk.
  const [evaluationAtRisk, setEvaluationAtRisk] = useState(false);
  const [fundedAtRisk, setFundedAtRisk] = useState(true);
  // Step 3 — the login. The key NAMES a server-side env entry; it is never the secret.
  const [credentialKey, setCredentialKey] = useState('');

  // Prop-firm-only for now (gh#780): a brokerage account has no mode-resolution path yet, so Brokerage is offered
  // as not-yet-supported and can never be selected — the walk is always these four steps.
  const steps: readonly Phase[] = ['firm', 'conventions', 'connection', 'discover'];
  const activeStep = Math.max(0, steps.indexOf(phase));

  const showFailure = (result: ApiFailure) =>
    setError(result.kind === 'refused' ? result.reason : result.error);

  async function submitFirm(event: FormEvent) {
    event.preventDefault();
    if (firmName.trim() === '' || busy) {
      return;
    }
    setBusy(true);
    setError(null);
    const result = await createFirm({ name: firmName.trim(), type: firmType });
    setBusy(false);
    if (!result.ok) {
      showFailure(result);
      return;
    }
    setFirm(result.data);
    setPhase('conventions');
  }

  async function submitConventions(event: FormEvent) {
    event.preventDefault();
    if (firm === null || busy) {
      return;
    }
    const conventions: StageConvention[] = [
      { stage: AccountStage.Practice, capitalAtRisk: false },
      { stage: AccountStage.Evaluation, capitalAtRisk: evaluationAtRisk },
      { stage: AccountStage.Funded, capitalAtRisk: fundedAtRisk },
    ];
    setBusy(true);
    setError(null);
    const result = await declareConventions(firm.id, conventions);
    setBusy(false);
    if (!result.ok) {
      showFailure(result);
      return;
    }
    setPhase('connection');
  }

  async function submitConnection(event: FormEvent) {
    event.preventDefault();
    if (firm === null || credentialKey.trim() === '' || busy) {
      return;
    }
    setBusy(true);
    setError(null);
    const result = await createConnection({
      firmId: firm.id,
      platform: WIRED_PLATFORM,
      credentialKey: credentialKey.trim(),
    });
    setBusy(false);
    if (!result.ok) {
      showFailure(result);
      return;
    }
    setConnection(result.data);
    setPhase('discover');
  }

  async function discover() {
    if (connection === null || busy) {
      return;
    }
    setBusy(true);
    setError(null);
    const result = await discoverAccounts(connection.id);
    setBusy(false);
    if (!result.ok) {
      showFailure(result);
      return;
    }
    setDiscovered(result.data);
  }

  return (
    <Box data-testid="firm-onboarding" sx={{ maxWidth: 560 }}>
      <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>
        Set up your first firm
      </Typography>
      <Typography variant="body2" sx={{ color: 'text.secondary', mb: 2 }}>
        A firm, what its stages mean, a login, and the accounts under it — the walk that fills your
        roster. Practice-vs-live comes from the conventions you declare, not the venue; the
        credential key names a server-side entry and is never the secret itself.
      </Typography>

      {error !== null ? (
        <Alert severity="warning" sx={{ mb: 2 }} onClose={() => setError(null)}>
          {error}
        </Alert>
      ) : null}

      <Stepper activeStep={activeStep} orientation="vertical">
        {steps.map((step) => (
          <Step key={step}>
            <StepLabel>{STEP_LABELS[step]}</StepLabel>
            <StepContent TransitionProps={{ unmountOnExit: true, timeout: 0 }}>
              {step === 'firm' ? (
                <Box component="form" noValidate onSubmit={submitFirm}>
                  <TextField
                    label="Firm name"
                    value={firmName}
                    onChange={(event) => setFirmName(event.target.value)}
                    size="small"
                    fullWidth
                    sx={{ mb: 2 }}
                  />
                  <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>
                    Type
                  </Typography>
                  <ToggleButtonGroup
                    exclusive
                    size="small"
                    value={firmType}
                    onChange={(_, value: FirmType | null) => value !== null && setFirmType(value)}
                    sx={{ mb: 1 }}
                  >
                    <ToggleButton value={FirmType.PropFirm}>Prop firm</ToggleButton>
                    <ToggleButton
                      value={FirmType.Brokerage}
                      disabled
                      title="Not yet supported — see gh#780"
                    >
                      Brokerage
                    </ToggleButton>
                  </ToggleButtonGroup>
                  <Typography
                    variant="caption"
                    sx={{ color: 'text.secondary', display: 'block', mb: 2 }}
                  >
                    A prop firm has staged programmes (evaluation, funded), and its accounts&apos;
                    mode comes from the conventions you declare next. Brokerage support isn&apos;t
                    built yet — a brokerage account has no mode-resolution path, so it would be left
                    untradeable (gh#780).
                  </Typography>
                  <Button
                    type="submit"
                    variant="contained"
                    disabled={busy || firmName.trim() === ''}
                  >
                    {busy ? 'Creating…' : 'Create firm'}
                  </Button>
                </Box>
              ) : null}

              {step === 'conventions' ? (
                <Box component="form" noValidate onSubmit={submitConventions}>
                  <Typography variant="body2" sx={{ color: 'text.secondary', mb: 2 }}>
                    What each stage means at {firm?.name} — this sets practice vs live for its
                    accounts. A stage left undeclared can&apos;t be traded anywhere.
                  </Typography>
                  <StageToggle
                    label="Evaluation"
                    hint="fee paid · pass / fail"
                    atRisk={evaluationAtRisk}
                    onChange={setEvaluationAtRisk}
                  />
                  <StageToggle
                    label="Funded"
                    hint="payout · trailing drawdown"
                    atRisk={fundedAtRisk}
                    onChange={setFundedAtRisk}
                  />
                  <Typography
                    variant="caption"
                    sx={{ color: 'text.secondary', display: 'block', mt: 1, mb: 2 }}
                  >
                    Practice accounts are always practice — declared for you.
                  </Typography>
                  <Button type="submit" variant="contained" disabled={busy}>
                    {busy ? 'Saving…' : 'Declare conventions'}
                  </Button>
                </Box>
              ) : null}

              {step === 'connection' ? (
                <Box component="form" noValidate onSubmit={submitConnection}>
                  <Typography variant="body2" sx={{ color: 'text.secondary', mb: 2 }}>
                    One login per firm × platform. The credentials stay server-side.
                  </Typography>
                  <TextField
                    label="Platform"
                    value="ProjectX"
                    size="small"
                    fullWidth
                    disabled
                    sx={{ mb: 2 }}
                    helperText="The only wired adapter today; others (Tradovate, Rithmic) ship as code."
                  />
                  <TextField
                    label="Credential key"
                    value={credentialKey}
                    onChange={(event) => setCredentialKey(event.target.value)}
                    size="small"
                    fullWidth
                    sx={{ mb: 2 }}
                    helperText="Names the server-side env entry that holds the login — not the credentials themselves."
                  />
                  <Button
                    type="submit"
                    variant="contained"
                    disabled={busy || credentialKey.trim() === ''}
                  >
                    {busy ? 'Connecting…' : 'Add connection'}
                  </Button>
                </Box>
              ) : null}

              {step === 'discover' ? (
                <Box>
                  <Typography variant="body2" sx={{ color: 'text.secondary', mb: 2 }}>
                    Discover the accounts under this login. Each account&apos;s mode is resolved
                    from the conventions you declared — an unclassified stage shows as Undeclared
                    and can&apos;t be traded.
                  </Typography>
                  <Button
                    variant="contained"
                    onClick={discover}
                    disabled={busy}
                    sx={{ mb: 2, mr: 1 }}
                  >
                    {busy
                      ? 'Discovering…'
                      : discovered !== null
                        ? 'Discover again'
                        : 'Discover accounts'}
                  </Button>
                  {discovered !== null ? (
                    <Box sx={{ mb: 2 }}>
                      {discovered.length === 0 ? (
                        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                          No accounts found under this login yet.
                        </Typography>
                      ) : (
                        <Stack spacing={1}>
                          {discovered.map((account) => (
                            <Paper
                              key={account.id}
                              variant="outlined"
                              sx={{
                                px: 2,
                                py: 1,
                                display: 'flex',
                                alignItems: 'center',
                                justifyContent: 'space-between',
                              }}
                            >
                              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                                {account.name}
                              </Typography>
                              <ModeChip mode={account.mode} />
                            </Paper>
                          ))}
                        </Stack>
                      )}
                    </Box>
                  ) : null}
                  <Button variant="outlined" onClick={onComplete}>
                    Finish
                  </Button>
                </Box>
              ) : null}
            </StepContent>
          </Step>
        ))}
      </Stepper>
    </Box>
  );
}

interface StageToggleProps {
  readonly label: string;
  readonly hint: string;
  readonly atRisk: boolean;
  readonly onChange: (atRisk: boolean) => void;
}

/** One stage's practice/at-risk choice. Its accessible name carries the stage, so the two rows never collide. */
function StageToggle({ label, hint, atRisk, onChange }: StageToggleProps) {
  return (
    <Box
      sx={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        gap: 2,
        mb: 1.5,
      }}
    >
      <Box>
        <Typography variant="body2" sx={{ fontWeight: 600 }}>
          {label}
        </Typography>
        <Typography variant="caption" sx={{ color: 'text.secondary' }}>
          {hint}
        </Typography>
      </Box>
      <ToggleButtonGroup
        exclusive
        size="small"
        value={atRisk ? 'atRisk' : 'practice'}
        onChange={(_, value: string | null) => value !== null && onChange(value === 'atRisk')}
      >
        <ToggleButton value="practice" aria-label={`${label} — practice`}>
          practice
        </ToggleButton>
        <ToggleButton value="atRisk" aria-label={`${label} — at risk`}>
          at risk
        </ToggleButton>
      </ToggleButtonGroup>
    </Box>
  );
}
