import Box from '@mui/material/Box';

import { ChartWorkspace, DEFAULT_INSTRUMENT } from '../chart/ChartWorkspace';
import type { Destination } from '../navigation/destinations';
import { useOptionalAccounts } from '../accounts/AccountProvider';
import { Blotter } from '../blotter/Blotter';
import { useLinkedInstrument } from '../panels/linkedInstrument';
import { PanelDetachButton } from '../panels/PanelDetachButton';
import { usePanelLayout } from '../panels/usePanelLayout';
import { SuggestionsPanel } from '../suggestions/SuggestionsPanel';

export interface WorkspaceSurfaceProps {
  readonly destination: Destination;
}

/**
 * The chart-central workspace (gh#725, R-10, ADR-0004): the candlestick chart is the primary surface, with the
 * suggestion panel (gh#654) beside it. This owns the shell's surface contract (the single `data-surface`) and the
 * pop-out layout, and drives the charted instrument through the cross-window **linked-instrument channel** (gh#1015)
 * so a detached chart on another monitor follows the symbol selected here — neither window authoritative. The chart
 * column itself is the shared {@link ChartWorkspace}, rendered here docked and (identically) in the detached chart
 * pop-out. The live blotter is its own card (gh#656).
 */
export function WorkspaceSurface({ destination }: WorkspaceSurfaceProps): React.JSX.Element {
  // The pop-out layout (gh#651): mounted ONCE here and threaded to each panel's dock toggle, so the detached set is
  // shared and persisted — a per-button copy would each hold a separate, diverging set.
  const panelLayout = usePanelLayout();
  // The charted instrument, mirrored across same-origin windows (gh#1015): selecting one here moves a detached chart,
  // and a detached chart's selection moves this one — a local mirror + broadcast, not an authority for the other.
  const [instrument, setInstrument] = useLinkedInstrument(DEFAULT_INSTRUMENT);

  // Optional rather than required: this surface can render before the provider resolves, and "no account yet" is
  // NOT "no positions" -- the blotter is withheld rather than mounted against a guess (gh#656).
  const accounts = useOptionalAccounts();
  const activeAccountId = accounts?.status === 'ready' ? accounts.activeAccount.id : null;

  return (
    <Box
      data-testid="surface"
      data-surface={destination.id}
      sx={{ height: '100%', display: 'flex', flexDirection: { xs: 'column', md: 'row' } }}
    >
      <Box
        sx={{
          flex: { md: 2 },
          minWidth: 0,
          minHeight: { xs: 320, md: 0 },
          display: 'flex',
          flexDirection: 'column',
        }}
      >
        {/* Pop the chart onto a second monitor (gh#1015): the same chart column ({@link ChartWorkspace}), its own
            window + realtime connection, carrying the safety strip and FOLLOWING the instrument selected here over
            the linked-instrument channel. A view convenience — the docked copy here is unchanged. */}
        <Box sx={{ display: 'flex', justifyContent: 'flex-end', px: 0.5, pt: 0.5 }}>
          <PanelDetachButton
            panelId="chart"
            label="Chart"
            detached={panelLayout.isDetached('chart')}
            onDetach={panelLayout.detach}
            onReattach={panelLayout.reattach}
          />
        </Box>
        <ChartWorkspace instrument={instrument} onInstrument={setInstrument} />
      </Box>

      <Box
        sx={{
          flex: { md: 1 },
          minWidth: { md: 320 },
          maxWidth: { md: 440 },
          borderColor: 'divider',
          borderStyle: 'solid',
          borderWidth: { xs: '1px 0 0 0', md: '0 0 0 1px' },
        }}
      >
        {/* Pop the suggestion feed onto a second monitor (gh#651): the same panel, its own window and realtime
            connection, carrying the safety strip. A view convenience — the docked copy here is unchanged. */}
        <Box sx={{ display: 'flex', justifyContent: 'flex-end', px: 0.5, pt: 0.5 }}>
          <PanelDetachButton
            panelId="suggestions"
            label="Suggestions"
            detached={panelLayout.isDetached('suggestions')}
            onDetach={panelLayout.detach}
            onReattach={panelLayout.reattach}
          />
        </Box>
        <SuggestionsPanel />
        {/* The live blotter (gh#656) — venue truth for positions and resting orders, beside the decision
            surface rather than a separate screen: managing what is live is part of the same loop as deciding
            what to take. Withheld entirely until an account resolves, because an empty blotter would assert
            something about venue truth that has not been read. */}
        {activeAccountId !== null ? (
          <Box
            sx={{ borderColor: 'divider', borderStyle: 'solid', borderWidth: '1px 0 0 0', p: 1.5 }}
          >
            <Box sx={{ display: 'flex', justifyContent: 'flex-end', mb: 0.5 }}>
              <PanelDetachButton
                panelId="blotter"
                label="Positions & orders"
                detached={panelLayout.isDetached('blotter')}
                onDetach={panelLayout.detach}
                onReattach={panelLayout.reattach}
              />
            </Box>
            {/* Keyed by account so a switch REMOUNTS the blotter: no state (a gh#969 resize notice, a prior
                account's venue view) survives into a different account's context — a fresh read, never stale
                truth under the wrong header. */}
            <Blotter key={activeAccountId} accountId={activeAccountId} />
          </Box>
        ) : null}
      </Box>
    </Box>
  );
}
