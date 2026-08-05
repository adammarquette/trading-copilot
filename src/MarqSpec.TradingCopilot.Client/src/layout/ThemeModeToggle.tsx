import DarkModeIcon from '@mui/icons-material/DarkMode';
import LightModeIcon from '@mui/icons-material/LightMode';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';

import { useThemeMode } from '../theme/useThemeMode';

/**
 * The dark / light switch, carried over from the wireframe's top-right sun / moon toggle (ADR-0005,
 * gh#28). The button announces the mode it will switch *to*, which is what a user acting on it wants to
 * know -- a control labelled with its current state reads like a status readout.
 */
export function ThemeModeToggle() {
  const { mode, toggleMode } = useThemeMode();
  const nextMode = mode === 'dark' ? 'light' : 'dark';
  const label = `Switch to ${nextMode} theme`;

  return (
    <Tooltip title={label}>
      <IconButton
        onClick={toggleMode}
        size="small"
        aria-label={label}
        data-theme-mode={mode}
        sx={{ color: 'text.secondary' }}
      >
        {mode === 'dark' ? <LightModeIcon fontSize="small" /> : <DarkModeIcon fontSize="small" />}
      </IconButton>
    </Tooltip>
  );
}
