import OpenInNewIcon from '@mui/icons-material/OpenInNew';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';

import { detachPanel } from './detachPanel';

export interface PanelDetachButtonProps {
  /** The registry id of the panel to pop out ({@link DETACHABLE_PANELS}). */
  readonly panelId: string;
  /** The panel's human name, for the control's accessible label and tooltip. */
  readonly label: string;
}

/**
 * The "pop out" control on a dockable workspace panel (gh#651). One click detaches the panel into its own window
 * ({@link detachPanel}); re-clicking focuses the window it already opened rather than stacking duplicates.
 */
export function PanelDetachButton({ panelId, label }: PanelDetachButtonProps): React.JSX.Element {
  return (
    <Tooltip title={`Pop out ${label}`}>
      <IconButton
        size="small"
        aria-label={`Pop out ${label} into its own window`}
        data-testid={`detach-${panelId}`}
        onClick={() => detachPanel(panelId)}
      >
        <OpenInNewIcon fontSize="small" />
      </IconButton>
    </Tooltip>
  );
}
