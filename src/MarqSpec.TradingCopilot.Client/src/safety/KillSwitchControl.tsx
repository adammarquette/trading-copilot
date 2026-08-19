import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import FormControlLabel from '@mui/material/FormControlLabel';
import Radio from '@mui/material/Radio';
import RadioGroup from '@mui/material/RadioGroup';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import {
  KillSwitchMode,
  type KillSwitchState,
  disengageKillSwitch,
  engageKillSwitch,
  getKillSwitch,
  isKillSwitchEvent,
} from '../api/killSwitch';
import { useOptionalRealtime } from '../realtime/RealtimeProvider';

/**
 * A low-frequency backstop re-read. The realtime events + resync are the primary refresh path; this only bounds the
 * window in which a broadcast-driven re-read that hit a transient failure (which correctly leaves the last state)
 * could strand a window on a stale kill state — matching TimeToFlat's schedule refresh (gh#985 review).
 */
const REFRESH_MS = 5 * 60 * 1000;

/**
 * The kill switch, in the safety strip's second reserved slot (gh#657, gh#189, R-11, ADR-0007).
 *
 * Three things about this control are safety properties rather than styling:
 *
 * 1. **The server is the truth on load.** The state survives a restart — it is rehydrated at startup so nothing
 *    silently re-enables trading — so a fresh page must never render "armed" by assumption.
 * 2. **Engaging is a deliberate gesture.** R-11's hold-to-confirm: a plain click opens the confirmation, it does
 *    not engage. The server independently refuses an unconfirmed request (422); this is the human half.
 * 3. **The confirmation says what it does *not* stop.** An operator who believes engaging strands their open
 *    positions may hesitate exactly when they should not. It stops new risk before the order is sized; reducing
 *    actions — auto-flatten's close, stop promotion — do not route through the send path and keep working.
 *
 * Failures fail toward the safe reading: a refused disengage leaves the control showing *engaged*, because that
 * is what the server still is.
 */
export function KillSwitchControl() {
  const [state, setState] = useState<KillSwitchState | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [mode, setMode] = useState<KillSwitchMode>(KillSwitchMode.FlattenAll);
  const [failure, setFailure] = useState<string | null>(null);
  const realtime = useOptionalRealtime();
  const mounted = useRef(true);
  /** Monotonic read generation — only the newest in-flight read may write state. */
  const latestRequest = useRef(0);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    // Ordering is by request GENERATION, not arrival. The mount read races the first broadcast, and a resync can
    // fire while a read is in flight; under HTTP/2 multiplexing a slow first connection or a retry can land them out
    // of order. Without this, an older "engaged" answer resolving late would overwrite a newer "disengaged" one (or
    // the reverse) and put the strip into exactly the dangerous, wrong state this control exists to prevent
    // (ProtectionStatus's #777 lesson). `mounted` guards post-unmount writes and says nothing about ordering.
    const generation = ++latestRequest.current;

    return getKillSwitch().then((result) => {
      if (mounted.current && generation === latestRequest.current && result.ok) {
        setState(result.data);
      }
    });
  }, []);

  // The initial state is already null (rendered as not-engaged chrome), so nothing is set synchronously here. The
  // interval is the backstop above: it bounds the staleness a missed or failed event-driven read could leave, on a
  // control that misleads in BOTH directions (a stale ENGAGED says trading is halted when it is not; a stale
  // disengaged, the reverse) — unlike ProtectionStatus's single alarming state.
  useEffect(() => {
    void load();
    const refresh = setInterval(() => void load(), REFRESH_MS);
    return () => clearInterval(refresh);
  }, [load]);

  // The read is the truth; the broadcast is only the prompt to take it again (ProtectionStatus's posture). Without
  // this, a SECOND window — a gh#651 pop-out, or the same tab after a reconnect — keeps showing whatever it last
  // read: an operator who disengaged in the main window still sees ENGAGED on the other monitor. The server
  // broadcasts every engage / disengage / escalation on the safety-strip channel; re-read on each, and on a resync
  // (the retention-gap / reconnect re-fetch). A replayed event is safe — it triggers a re-read, and the READ is
  // what renders, so no is-this-live reasoning is needed here.
  useEffect(() => {
    if (realtime === null) {
      return;
    }

    const stopEvents = realtime.onEvent((event) => {
      if (isKillSwitchEvent(event.type)) {
        void load();
      }
    });
    const stopResync = realtime.onResync(() => void load());

    return () => {
      stopEvents();
      stopResync();
    };
  }, [realtime, load]);

  const confirmEngage = useCallback(async () => {
    const result = await engageKillSwitch(mode, null);
    if (!result.ok) {
      setFailure('The kill switch was NOT engaged — the server refused. Trading is still enabled.');
      return;
    }
    setConfirming(false);
    setFailure(null);
    // Read the state back rather than assuming it: what the switch now IS is the server's answer.
    await load();
  }, [load, mode]);

  const requestDisengage = useCallback(async () => {
    const result = await disengageKillSwitch();
    if (!result.ok) {
      setFailure('Still engaged — the server refused to re-enable outbound orders.');
      return;
    }
    setFailure(null);
    await load();
  }, [load]);

  const engaged = state?.engaged === true;

  return (
    <Box data-testid="kill-switch" sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
      {engaged ? (
        <>
          <Typography
            variant="body2"
            sx={{ fontWeight: 700, fontSize: 12, color: 'error.main', whiteSpace: 'nowrap' }}
          >
            ENGAGED
          </Typography>
          <Button size="small" color="inherit" onClick={() => void requestDisengage()}>
            Disengage
          </Button>
        </>
      ) : (
        <Button
          size="small"
          color="error"
          variant="outlined"
          aria-label="Kill switch"
          onClick={() => setConfirming(true)}
          sx={{ minWidth: 0, fontSize: 11, fontWeight: 700, px: 1 }}
        >
          KILL
        </Button>
      )}

      <Dialog
        open={confirming}
        onClose={() => setConfirming(false)}
        aria-labelledby="kill-switch-title"
      >
        <DialogTitle id="kill-switch-title">Engage the kill switch?</DialogTitle>
        <DialogContent>
          <Typography variant="body2" gutterBottom>
            <strong>It stops new risk.</strong> Every outbound order is refused before it is sized,
            and working orders are cancelled.
          </Typography>
          <Typography variant="body2" gutterBottom>
            <strong>It does not stop what protects you.</strong> Your native safety stop stays with
            the position, and auto-flatten&rsquo;s close and stop promotion keep working — they do
            not route through the send path. Engaging does not strand open positions.
          </Typography>
          <Typography variant="body2" gutterBottom>
            It stays engaged across a restart until you disengage it.
          </Typography>

          <RadioGroup
            value={String(mode)}
            onChange={(event) => setMode(Number(event.target.value) as KillSwitchMode)}
          >
            <FormControlLabel
              value={String(KillSwitchMode.FlattenAll)}
              control={<Radio size="small" />}
              label="Flatten all — close open positions now"
            />
            <FormControlLabel
              value={String(KillSwitchMode.HaltOnly)}
              control={<Radio size="small" />}
              label="Halt only — leave positions on their native safety stops"
            />
          </RadioGroup>

          {failure !== null ? <Alert severity="error">{failure}</Alert> : null}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirming(false)}>Cancel</Button>
          <Button color="error" variant="contained" onClick={() => void confirmEngage()}>
            Engage
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
