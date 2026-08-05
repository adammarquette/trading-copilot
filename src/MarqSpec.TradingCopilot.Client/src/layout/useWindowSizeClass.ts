import useMediaQuery from '@mui/material/useMediaQuery';

import { windowSizeClassMinWidth, type WindowSizeClass } from '../theme/tokens';

/**
 * Which Material 3 window size class the viewport is in -- compact (< 600), medium (600-839), or
 * expanded (>= 840). ADR-0005 is normative on these thresholds, and the adaptive navigation switches
 * form on them.
 *
 * Note the divergence from `documentation/design/wireframes.html`, which switches at 720/900px. Those
 * are the mock's own numbers, chosen so three device frames sit side by side on one page; they are
 * illustrative of a static artifact, not a spec. The ADR wins.
 *
 * Two queries rather than a range: `(min-width: 600px)` and `(min-width: 840px)` are independently
 * true/false, so there is no gap or overlap to get wrong, and each maps to exactly one boundary in the
 * token module.
 */
export function useWindowSizeClass(): WindowSizeClass {
  const atLeastMedium = useMediaQuery(`(min-width:${windowSizeClassMinWidth.medium}px)`);
  const atLeastExpanded = useMediaQuery(`(min-width:${windowSizeClassMinWidth.expanded}px)`);

  if (atLeastExpanded) return 'expanded';
  if (atLeastMedium) return 'medium';
  return 'compact';
}
