import Box from '@mui/material/Box';
import Divider from '@mui/material/Divider';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';

import type { RiskProfile } from '../api/risk';
import {
  consistencyEnforcementLabels,
  defaultEntryActionLabels,
  floorSourceLabels,
  formatContractCap,
  formatFraction,
  formatRatio,
  formatUsd,
  sizingBasisLabels,
  trailingModeLabels,
} from './format';

/**
 * A read of the account's declared risk profile (R-5), grouped the way the operator thinks about it: the daily
 * discipline first (the governor and the consistency target the wireframe leads with), then the loss floor, the
 * per-trade sizing, and the entry default.
 *
 * Every value here is **declared**, not enforced: it is an input the server-side gate sizes and refuses against.
 * The surface says so out loud (see {@link RiskSettings}) precisely so this screen is never mistaken for the thing
 * that stops a trade — the gate is.
 */
export interface RiskProfileSummaryProps {
  readonly profile: RiskProfile;
}

function Row({ label, value }: { readonly label: string; readonly value: string }) {
  return (
    <Stack direction="row" justifyContent="space-between" spacing={2}>
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {label}
      </Typography>
      <Typography variant="body2" sx={{ fontVariantNumeric: 'tabular-nums', fontWeight: 600 }}>
        {value}
      </Typography>
    </Stack>
  );
}

function Section({ title, children }: { readonly title: string; readonly children: ReactNode }) {
  return (
    <Box>
      <Typography variant="overline" sx={{ color: 'text.secondary' }}>
        {title}
      </Typography>
      <Stack spacing={0.75} sx={{ mt: 0.5 }}>
        {children}
      </Stack>
    </Box>
  );
}

export function RiskProfileSummary({ profile }: RiskProfileSummaryProps) {
  const consistency =
    profile.maxBestDayFraction === null
      ? 'None'
      : `${formatFraction(profile.maxBestDayFraction)} · ${consistencyEnforcementLabels[profile.consistencyEnforcement]}`;

  return (
    <Stack data-testid="risk-summary" spacing={2} divider={<Divider flexItem />}>
      <Section title="Daily discipline">
        <Row label="Personal daily governor" value={formatUsd(profile.dailyDrawdownGovernor)} />
        <Row label="Daily profit target" value={formatUsd(profile.dailyProfitTarget)} />
        <Row
          label="Stop for the day at target"
          value={profile.stopForDayAtProfitTarget ? 'Yes' : 'No'}
        />
        <Row label="Consistency target" value={consistency} />
      </Section>

      <Section title="Loss floor">
        <Row label="Starting balance" value={formatUsd(profile.startingBalance)} />
        <Row label="Firm daily loss limit" value={formatUsd(profile.dailyLossLimit)} />
        <Row label="Floor source" value={floorSourceLabels[profile.floorSource]} />
        <Row label="Trailing" value={trailingModeLabels[profile.trailingMode]} />
        <Row label="Trailing amount" value={formatUsd(profile.trailingAmount)} />
        <Row label="Locks at" value={formatUsd(profile.locksAt)} />
        <Row label="Account profit target" value={formatUsd(profile.accountProfitTarget)} />
      </Section>

      <Section title="Per-trade sizing">
        <Row label="Risk per trade" value={formatFraction(profile.perTradeRiskFraction)} />
        <Row label="Max drawdown per trade" value={formatUsd(profile.maxDrawdownPerTrade)} />
        <Row label="Target reward ratio" value={formatRatio(profile.targetRewardRatio)} />
        <Row label="Sizing basis" value={sizingBasisLabels[profile.sizingBasis]} />
        <Row
          label="Max contracts per order"
          value={formatContractCap(profile.maxContractsPerOrder)}
        />
      </Section>

      <Section title="Entry">
        <Row label="Default action" value={defaultEntryActionLabels[profile.defaultEntryAction]} />
      </Section>
    </Stack>
  );
}
