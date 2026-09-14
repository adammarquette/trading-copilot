import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import FormControlLabel from '@mui/material/FormControlLabel';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import Switch from '@mui/material/Switch';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { type FormEvent, type ReactNode, useCallback, useEffect, useRef, useState } from 'react';

import {
  ConsistencyEnforcement,
  DefaultEntryAction,
  declareRiskProfile,
  FloorSource,
  type RiskProfile,
  SizingBasis,
  TrailingMode,
} from '../api/risk';
import {
  consistencyEnforcementLabels,
  defaultEntryActionLabels,
  floorSourceLabels,
  sizingBasisLabels,
  trailingModeLabels,
} from './format';
import {
  type RiskFormErrors,
  type RiskFormState,
  toFormState,
  toRequest,
  validate,
} from './riskFormModel';

/**
 * Declares (or re-declares) an account's whole risk profile (R-5). Every field is edited and sent together because
 * the server stores the profile whole; the form loads the current values (or neutral defaults for an undeclared
 * account), validates what it can see, and surfaces the gate's own refusal when the server rejects the rest.
 *
 * Nothing here is enforced client-side. Saving declares the values the server-side gate will size and refuse
 * against — it does not itself stop a trade.
 */
export interface RiskProfileFormProps {
  readonly accountId: string;
  readonly initial: RiskProfile | null;
  readonly onSaved: () => void;
  readonly onCancel: () => void;
}

type SubmitState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'saving' }
  | { readonly kind: 'refused'; readonly message: string }
  | { readonly kind: 'failed'; readonly message: string };

function Group({ title, children }: { readonly title: string; readonly children: ReactNode }) {
  return (
    <Box component="fieldset" sx={{ border: 0, p: 0, m: 0 }}>
      <Typography component="legend" variant="overline" sx={{ color: 'text.secondary' }}>
        {title}
      </Typography>
      <Stack spacing={2} sx={{ mt: 1 }}>
        {children}
      </Stack>
    </Box>
  );
}

export function RiskProfileForm({ accountId, initial, onSaved, onCancel }: RiskProfileFormProps) {
  const [form, setForm] = useState<RiskFormState>(() => toFormState(initial));
  const [errors, setErrors] = useState<RiskFormErrors>({});
  const [submit, setSubmit] = useState<SubmitState>({ kind: 'idle' });
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const set = useCallback(<K extends keyof RiskFormState>(field: K, value: RiskFormState[K]) => {
    setForm((current) => ({ ...current, [field]: value }));
  }, []);

  const num = (field: keyof RiskFormState, label: string, required = false) => (
    <TextField
      label={label}
      value={String(form[field])}
      onChange={(event) => set(field, event.target.value as RiskFormState[typeof field])}
      error={errors[field] !== undefined}
      helperText={errors[field]}
      required={required}
      type="number"
      size="small"
      fullWidth
    />
  );

  const showConfirm = form.defaultEntryAction === DefaultEntryAction.SendAsIs;

  const handleSubmit = useCallback(
    (event: FormEvent) => {
      event.preventDefault();
      const found = validate(form);
      setErrors(found);
      if (Object.keys(found).length > 0) {
        return;
      }
      setSubmit({ kind: 'saving' });
      void declareRiskProfile(accountId, toRequest(form)).then((result) => {
        if (!mounted.current) {
          return;
        }
        if (result.ok) {
          onSaved();
          return;
        }
        setSubmit(
          result.kind === 'refused'
            ? { kind: 'refused', message: result.reason }
            : { kind: 'failed', message: result.error },
        );
      });
    },
    [accountId, form, onSaved],
  );

  return (
    <Box component="form" aria-label="Risk profile" noValidate onSubmit={handleSubmit}>
      <Stack spacing={3}>
        <Group title="Daily discipline">
          {num('dailyDrawdownGovernor', 'Personal daily governor', true)}
          {num('dailyProfitTarget', 'Daily profit target')}
          <FormControlLabel
            control={
              <Switch
                checked={form.stopForDayAtProfitTarget}
                onChange={(event) => set('stopForDayAtProfitTarget', event.target.checked)}
              />
            }
            label="Stop for the day when the target is reached"
          />
          {num('maxBestDayFraction', 'Consistency target (best day ÷ total, 0–1)')}
          <TextField
            select
            label="If the consistency target is breached"
            value={form.consistencyEnforcement}
            onChange={(event) =>
              set('consistencyEnforcement', Number(event.target.value) as ConsistencyEnforcement)
            }
            size="small"
            fullWidth
          >
            {Object.values(ConsistencyEnforcement).map((value) => (
              <MenuItem key={value} value={value}>
                {consistencyEnforcementLabels[value]}
              </MenuItem>
            ))}
          </TextField>
        </Group>

        <Group title="Loss floor">
          {num('startingBalance', 'Starting balance', true)}
          {num('dailyLossLimit', 'Firm daily loss limit')}
          <TextField
            select
            label="Floor source"
            value={form.floorSource}
            onChange={(event) => set('floorSource', Number(event.target.value) as FloorSource)}
            size="small"
            fullWidth
          >
            {Object.values(FloorSource).map((value) => (
              <MenuItem key={value} value={value}>
                {floorSourceLabels[value]}
              </MenuItem>
            ))}
          </TextField>
          <TextField
            select
            label="Trailing"
            value={form.trailingMode}
            onChange={(event) => set('trailingMode', Number(event.target.value) as TrailingMode)}
            size="small"
            fullWidth
          >
            {Object.values(TrailingMode).map((value) => (
              <MenuItem key={value} value={value}>
                {trailingModeLabels[value]}
              </MenuItem>
            ))}
          </TextField>
          {num('trailingAmount', 'Trailing amount', true)}
          {num('locksAt', 'Floor locks at')}
          {num('accountProfitTarget', 'Account profit target')}
        </Group>

        <Group title="Per-trade sizing">
          {num('perTradeRiskFraction', 'Risk per trade (fraction of headroom, 0–1)', true)}
          {num('maxDrawdownPerTrade', 'Max drawdown per trade', true)}
          {num('targetRewardRatio', 'Target reward ratio', true)}
          <TextField
            select
            label="Size against"
            value={form.sizingBasis}
            onChange={(event) => set('sizingBasis', Number(event.target.value) as SizingBasis)}
            size="small"
            fullWidth
          >
            {Object.values(SizingBasis).map((value) => (
              <MenuItem key={value} value={value}>
                {sizingBasisLabels[value]}
              </MenuItem>
            ))}
          </TextField>
          {num('maxContractsPerOrder', 'Max contracts per order (0 = uncapped)', true)}
        </Group>

        <Group title="Entry default">
          <TextField
            select
            label="Default entry action"
            value={form.defaultEntryAction}
            onChange={(event) =>
              set('defaultEntryAction', Number(event.target.value) as DefaultEntryAction)
            }
            size="small"
            fullWidth
          >
            {Object.values(DefaultEntryAction).map((value) => (
              <MenuItem key={value} value={value}>
                {defaultEntryActionLabels[value]}
              </MenuItem>
            ))}
          </TextField>
          {showConfirm ? (
            <FormControlLabel
              control={
                <Switch
                  checked={form.confirmSendAsIsDefault}
                  onChange={(event) => set('confirmSendAsIsDefault', event.target.checked)}
                />
              }
              label="I confirm send-as-is may be the default (practice accounts only)"
            />
          ) : null}
          {showConfirm && errors.confirmSendAsIsDefault !== undefined ? (
            <Typography variant="caption" sx={{ color: 'error.main' }}>
              {errors.confirmSendAsIsDefault}
            </Typography>
          ) : null}
        </Group>

        {submit.kind === 'refused' ? (
          <Alert severity="warning">The gate refused this profile: {submit.message}</Alert>
        ) : null}
        {submit.kind === 'failed' ? (
          <Alert severity="error">Could not save the profile: {submit.message}</Alert>
        ) : null}

        <Stack direction="row" spacing={1} justifyContent="flex-end">
          <Button onClick={onCancel} disabled={submit.kind === 'saving'}>
            Cancel
          </Button>
          <Button type="submit" variant="contained" disabled={submit.kind === 'saving'}>
            {submit.kind === 'saving' ? 'Saving…' : 'Save risk profile'}
          </Button>
        </Stack>
      </Stack>
    </Box>
  );
}
