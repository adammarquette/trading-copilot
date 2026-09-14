import { useParams } from 'react-router';

import { EmptyState } from '../components/EmptyState';
import { DetachedPanelFrame } from './DetachedPanelFrame';
import { panelById } from './panelRegistry';

/**
 * The route a detached window lands on — `/panel/:panelId` (gh#651, ADR-0006). It resolves the panel from the
 * registry and frames it. An **unknown** id — a mistyped or stale pop-out URL — still renders inside the frame, so a
 * wrong address is never a window without a kill switch: the safety strip is not the panel's to withhold.
 */
export function DetachedPanelPage(): React.JSX.Element {
  const { panelId } = useParams();
  const panel = panelById(panelId);

  if (panel === undefined) {
    return (
      <DetachedPanelFrame title="Unknown panel">
        <EmptyState
          title="No such panel"
          description="This pop-out address does not match a panel. Close it and detach the panel again from the workspace."
        />
      </DetachedPanelFrame>
    );
  }

  const { Panel } = panel;
  return (
    <DetachedPanelFrame title={panel.title}>
      <Panel />
    </DetachedPanelFrame>
  );
}
