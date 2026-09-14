import CloseFullscreenIcon from '@mui/icons-material/CloseFullscreen';
import OpenInNewIcon from '@mui/icons-material/OpenInNew';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';

export interface PanelDetachButtonProps {
  /** The registry id of the panel ({@link DETACHABLE_PANELS}). */
  readonly panelId: string;
  /** The panel's human name, for the control's accessible label and tooltip. */
  readonly label: string;
  /** Whether the panel is currently popped out — the control toggles between pop-out and reattach. */
  readonly detached: boolean;
  readonly onDetach: (panelId: string) => void;
  readonly onReattach: (panelId: string) => void;
}

/**
 * The dock toggle on a workspace panel (gh#651): **"pop out"** when the panel is docked, **"reattach"** when it is
 * in its own window. The detached state is the operator's persisted layout ({@link usePanelLayout}), so a reload
 * shows the reattach affordance for a panel that was popped out — the layout survives the restart.
 */
export function PanelDetachButton({
  panelId,
  label,
  detached,
  onDetach,
  onReattach,
}: PanelDetachButtonProps): React.JSX.Element {
  if (detached) {
    return (
      <Tooltip title={`Reattach ${label}`}>
        <IconButton
          size="small"
          aria-label={`Reattach ${label} into this window`}
          data-testid={`reattach-${panelId}`}
          onClick={() => onReattach(panelId)}
        >
          <CloseFullscreenIcon fontSize="small" />
        </IconButton>
      </Tooltip>
    );
  }

  return (
    <Tooltip title={`Pop out ${label}`}>
      <IconButton
        size="small"
        aria-label={`Pop out ${label} into its own window`}
        data-testid={`detach-${panelId}`}
        onClick={() => onDetach(panelId)}
      >
        <OpenInNewIcon fontSize="small" />
      </IconButton>
    </Tooltip>
  );
}
