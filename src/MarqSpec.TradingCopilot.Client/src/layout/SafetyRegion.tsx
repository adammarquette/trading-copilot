import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';

/**
 * The persistent safety region -- the reserved frame for the time-to-flat countdown (R-13) and the kill
 * switch, which ADR-0005 requires visible at every breakpoint.
 *
 * Three properties are load-bearing, and all three are structural rather than conventional:
 *
 *  1. **Non-dismissable.** There is no `hidden`, `collapsed` or `onDismiss` prop, and no close control.
 *     The only way to remove it is to delete the element from the shell, which the tests catch.
 *  2. **Cannot be scrolled away.** It is rendered inside the app bar of a shell that is exactly viewport
 *     height and does not scroll; only the content region scrolls. So it is off the scrolling axis
 *     entirely, rather than merely pinned to it.
 *  3. **Present while empty.** The slots reserve their footprint before there is anything to put in
 *     them, so the layout does not shift -- and the operator does not learn a control's position -- the
 *     day gh#25 fills them.
 *
 * The two slots were filled by gh#657. The degraded-protection indicator (gh#222) sits AHEAD of them and
 * deliberately outside the slot chrome: it is an alert, not a labelled control, so it carries no caption and no
 * dashed reservation -- a permanent "PROTECTION" frame beside a healthy system is noise, and the indicator
 * reserves its own footprint while quiet so nothing shifts the moment a drop happens.
 */
export interface SafetyRegionProps {
  /** The R-13 time-to-flat countdown. gh#657. */
  readonly timeToFlat?: ReactNode;
  /** The kill switch. gh#657. */
  readonly killSwitch?: ReactNode;
  /** The degraded-protection indicator (gh#222) — rendered ahead of the slots, without slot chrome. */
  readonly protection?: ReactNode;
  /** Compact drops the slot captions; the controls themselves stay at full size. */
  readonly dense?: boolean;
}

interface SafetySlotProps {
  readonly name: string;
  readonly caption: string;
  readonly minWidth: number;
  readonly dense: boolean;
  readonly children?: ReactNode;
}

function SafetySlot({ name, caption, minWidth, dense, children }: SafetySlotProps) {
  const filled = children !== undefined && children !== null;

  return (
    <Box
      data-safety-slot={name}
      data-filled={filled ? 'true' : 'false'}
      sx={{
        minWidth,
        minHeight: 32,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 0.75,
        px: 1,
        borderRadius: 1,
        // An empty slot is drawn as an outline rather than left blank: a gap in the app bar reads as a
        // layout bug, where a dashed reservation reads as "not built yet", which is the truth.
        border: filled ? 'none' : '1px dashed',
        borderColor: 'divider',
      }}
    >
      {filled ? (
        children
      ) : (
        <Typography
          variant="caption"
          sx={{
            color: 'text.disabled',
            fontSize: 9.5,
            letterSpacing: '.08em',
            whiteSpace: 'nowrap',
          }}
        >
          {dense ? caption.split(' ')[0] : caption}
        </Typography>
      )}
    </Box>
  );
}

export function SafetyRegion({
  timeToFlat,
  killSwitch,
  protection,
  dense = false,
}: SafetyRegionProps) {
  return (
    <Box
      // A landmark, not a decoration: at any breakpoint, on any surface, an assistive-technology user
      // must be able to jump straight to the two controls that stop trading.
      role="region"
      aria-label="Safety controls"
      data-testid="safety-region"
      sx={{ display: 'flex', alignItems: 'center', gap: 0.75, flex: 'none' }}
    >
      {protection}
      <SafetySlot name="time-to-flat" caption="TO FLAT" minWidth={dense ? 62 : 84} dense={dense}>
        {timeToFlat}
      </SafetySlot>
      <SafetySlot name="kill-switch" caption="KILL" minWidth={dense ? 44 : 62} dense={dense}>
        {killSwitch}
      </SafetySlot>
    </Box>
  );
}
