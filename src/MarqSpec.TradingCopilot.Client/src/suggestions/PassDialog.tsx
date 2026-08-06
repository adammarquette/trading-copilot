import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useState } from 'react';

import { NOTE_MAX_LENGTH, SuggestionPassReason } from '../api/suggestions';

/**
 * "Suggestion passed — anything worth noting?" (R-4, gh#547).
 *
 * **A pass is a neutral decline, not a rejection**, and the whole sheet is optional — which is why "Skip" passes
 * just as completely as "Save feedback" does. The reason is not bureaucracy: disposition + reason land on the
 * suggestion record (R-8) and feed the R-9 loop, so *passed-but-winning* setups can surface in the
 * taken-vs-suggested breakdown and a repeated reason can tune what gets proposed.
 *
 * The sheet opens **before** the POST rather than after it, and that ordering is forced by the API: there is
 * exactly one disposition row per suggestion and a second write conflicts (409). So the reasons must ride the same
 * request as the pass itself — asking afterwards would have nowhere to put the answer.
 */

/** The canonical eight, in the wireframe's reading order. */
export const PASS_REASON_CHOICES: readonly {
  readonly flag: number;
  readonly label: string;
}[] = [
  { flag: SuggestionPassReason.NewsRisk, label: 'News risk' },
  { flag: SuggestionPassReason.WrongTime, label: 'Wrong time' },
  { flag: SuggestionPassReason.AlreadyPositioned, label: 'Already positioned' },
  { flag: SuggestionPassReason.WaitingBetterLevel, label: 'Waiting / better level' },
  { flag: SuggestionPassReason.WeakRewardRisk, label: 'Weak R:R' },
  { flag: SuggestionPassReason.AgainstARule, label: 'Against a rule' },
  { flag: SuggestionPassReason.Sizing, label: 'Sizing' },
  { flag: SuggestionPassReason.LowConviction, label: 'Low conviction' },
];

export interface PassDialogProps {
  readonly open: boolean;
  /** What is being passed on, for the header line — "LONG ES · 5230.25 · 2.2R". */
  readonly summary: string;
  readonly busy: boolean;
  /** Closes without passing. Distinct from Skip, which *does* pass. */
  readonly onCancel: () => void;
  /** `reasons` is a `[Flags]` bitmask; `None` (zero) with no note is the Skip path and is entirely valid. */
  readonly onPass: (reasons: number, note: string) => void;
}

export function PassDialog({ open, summary, busy, onCancel, onPass }: PassDialogProps) {
  const [reasons, setReasons] = useState<number>(SuggestionPassReason.None);
  const [note, setNote] = useState('');

  function toggle(flag: number) {
    // Multi-select: the reasons are a bitmask precisely because "news risk AND wrong time" is one answer, not two.
    setReasons((current) => ((current & flag) === flag ? current & ~flag : current | flag));
  }

  return (
    <Dialog open={open} onClose={onCancel} fullWidth maxWidth="sm">
      <DialogTitle sx={{ pb: 0.5 }}>
        Anything worth noting?
        <Typography component="div" variant="body2" sx={{ color: 'text.secondary' }}>
          Optional — a pass isn&apos;t a rejection.
        </Typography>
      </DialogTitle>

      <DialogContent data-testid="pass-dialog">
        <Typography
          variant="numeric"
          data-testid="pass-summary"
          sx={{ color: 'text.secondary', display: 'block', mb: 1.5 }}
        >
          {summary}
        </Typography>

        <Stack direction="row" flexWrap="wrap" useFlexGap sx={{ gap: 0.75, mb: 2 }}>
          {PASS_REASON_CHOICES.map(({ flag, label }) => {
            const selected = (reasons & flag) === flag;
            return (
              <Chip
                key={label}
                label={label}
                size="small"
                clickable
                // aria-pressed makes the multi-select legible to assistive tech; a Chip alone reads as a button
                // with no state, which for a toggle is a lie.
                role="button"
                aria-pressed={selected}
                onClick={() => {
                  toggle(flag);
                }}
                variant={selected ? 'filled' : 'outlined'}
                color={selected ? 'primary' : 'default'}
                data-testid="pass-reason"
                data-selected={selected ? 'true' : 'false'}
              />
            );
          })}
        </Stack>

        <TextField
          label="Add a note"
          placeholder="e.g. CPI print in 10 min, not worth the gap risk."
          value={note}
          onChange={(event) => {
            setNote(event.target.value);
          }}
          multiline
          minRows={2}
          fullWidth
          // Mirrors the server's cap so a long note is stopped at the keyboard, not by a 400 after the operator
          // has already written it.
          slotProps={{ htmlInput: { maxLength: NOTE_MAX_LENGTH, 'data-testid': 'pass-note' } }}
          helperText={`${String(note.length)} / ${String(NOTE_MAX_LENGTH)}`}
        />
      </DialogContent>

      <DialogActions sx={{ px: 3, pb: 2 }}>
        {/* Not in the wireframe, which offers only Save / Skip — but both of those PASS, so without this there is
            no way back out of a dialog opened by mistake except Esc, which is not discoverable. */}
        <Button onClick={onCancel} color="inherit" disabled={busy}>
          Cancel
        </Button>
        <Button
          onClick={() => {
            onPass(SuggestionPassReason.None, '');
          }}
          disabled={busy}
          data-testid="pass-skip"
        >
          Skip
        </Button>
        <Button
          onClick={() => {
            onPass(reasons, note);
          }}
          variant="contained"
          disabled={busy}
          data-testid="pass-save"
        >
          Save feedback
        </Button>
      </DialogActions>
    </Dialog>
  );
}
