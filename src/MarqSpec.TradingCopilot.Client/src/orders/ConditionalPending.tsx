import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Chip from '@mui/material/Chip';
import Typography from '@mui/material/Typography';

import type { ConditionalOrderResponse } from '../api/orders';
import { describeTrigger } from './conditional';

/**
 * A created **pending** conditional entry (gh#655, R-11 / R-12, ADR-0007) — the "send when conditions met" mode.
 *
 * The rule this panel exists to make visible: **a conditional is not an armed order.** It is held local and off
 * the book, and the authoritative risk gate re-runs when it fires (R-12) — so this must say *pending*, never imply
 * something is resting at the venue. The decision the create returned is only a **preview** at creation; it is
 * shown as such and re-decided at fire time, so the operator is never led to believe a size or an allow is locked
 * in.
 */
export function ConditionalPending({
  conditional,
}: {
  readonly conditional: ConditionalOrderResponse;
}) {
  return (
    <Box
      data-testid="conditional-pending"
      sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}
    >
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <Chip size="small" label="Pending · on-trigger" color="info" variant="outlined" />
      </Box>

      <Typography variant="body2">
        Fires when price {describeTrigger(conditional.triggerDirection, conditional.triggerPrice)}.
      </Typography>

      {/* The wireframe's promise: a synthetic order the platform holds and fires — not shown as a standing order at
          the broker until it does, so it cannot be anticipated or stop-hunted. */}
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        Held local — off the book until it triggers.
      </Typography>

      {/* The honesty line (R-12). A conditional is NOT armed: the gate re-runs at fire time, so what it does now is
          only a preview. Saying so is the acceptance criterion this panel is here to meet. */}
      <Alert severity="info">
        Re-checked by the risk gate when it fires (R-12). This is not a placed order.
      </Alert>

      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        Preview at creation: {conditional.outcome} — {conditional.reason}
      </Typography>
    </Box>
  );
}
