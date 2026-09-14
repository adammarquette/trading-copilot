import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline';
import EditOutlinedIcon from '@mui/icons-material/EditOutlined';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import Typography from '@mui/material/Typography';
import { type FormEvent, useCallback, useEffect, useRef, useState } from 'react';

import {
  createTopic,
  deleteTopic,
  listTopics,
  type Topic,
  type TopicInput,
  TopicScope,
  updateTopic,
} from '../api/relevance';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';

/**
 * The relevance **topics** (gh#658, of gh#742; R-2/R-6/R-7): the named keyword sets that decide which news is
 * salient, either **globally** (market-wide — FOMC, CPI) or **scoped to one instrument** (Fed policy → ES).
 * Deployment-global config, so — like the maps beside it — nothing here is account-scoped. A topic is created,
 * edited and deleted here; the AI-suggested topics and the star / mute feedback the wireframe also draws ride the
 * feed and stay [D2] follow-ups.
 */
type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly topics: readonly Topic[] };

/** Keywords are entered as a comma-separated list and stored as an array; empty entries are dropped. */
const parseKeywords = (text: string): string[] =>
  text
    .split(',')
    .map((keyword) => keyword.trim())
    .filter((keyword) => keyword !== '');

export function RelevanceTopics() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [name, setName] = useState('');
  const [keywords, setKeywords] = useState('');
  const [scope, setScope] = useState<TopicScope>(TopicScope.Global);
  const [instrument, setInstrument] = useState('');
  // The topic being edited, or null when the form is adding a new one — the one piece of state that flips the
  // form (and its submit) between createTopic and updateTopic.
  const [editingId, setEditingId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  // In-flight guards, as in RelevanceMaps: a slow POST/PATCH must not stay clickable, or a double-click fires a
  // duplicate. Delete is tracked per id so one row's request never disables another's.
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState<ReadonlySet<string>>(() => new Set());
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    void listTopics().then((result) => {
      if (!mounted.current) {
        return;
      }
      setState(
        result.ok
          ? { kind: 'loaded', topics: result.data }
          : { kind: 'error', message: result.kind === 'refused' ? result.reason : result.error },
      );
    });
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const reload = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  const failed = (
    result: { kind: 'refused'; reason: string } | { kind: 'failed'; error: string },
  ) => setActionError(result.kind === 'refused' ? result.reason : result.error);

  const resetForm = () => {
    setName('');
    setKeywords('');
    setScope(TopicScope.Global);
    setInstrument('');
    setEditingId(null);
  };

  const isInstrument = scope === TopicScope.Instrument;
  const canSubmit = name.trim() !== '' && (!isInstrument || instrument.trim() !== '');

  const submit = useCallback(
    (event: FormEvent) => {
      event.preventDefault();
      if (!canSubmit || saving) {
        return;
      }
      const input: TopicInput = {
        name: name.trim(),
        keywords: parseKeywords(keywords),
        scope,
        instrument: isInstrument ? instrument.trim() : null,
      };
      setSaving(true);
      const request = editingId === null ? createTopic(input) : updateTopic(editingId, input);
      void request.then((result) => {
        if (!mounted.current) {
          return;
        }
        setSaving(false);
        if (result.ok) {
          resetForm();
          setActionError(null);
          reload();
        } else {
          failed(result);
        }
      });
    },
    [canSubmit, saving, name, keywords, scope, isInstrument, instrument, editingId, reload],
  );

  const startEdit = (topic: Topic) => {
    setEditingId(topic.id);
    setName(topic.name);
    setKeywords(topic.keywords.join(', '));
    setScope(topic.scope);
    setInstrument(topic.instrument ?? '');
    setActionError(null);
  };

  const remove = useCallback(
    (topic: Topic) => {
      setDeleting((current) => new Set(current).add(topic.id));
      void deleteTopic(topic.id).then((result) => {
        if (!mounted.current) {
          return;
        }
        setDeleting((current) => {
          const next = new Set(current);
          next.delete(topic.id);
          return next;
        });
        if (result.ok) {
          setActionError(null);
          // A delete of the row being edited leaves the form pointing at a topic that no longer exists — clear it.
          if (editingId === topic.id) {
            resetForm();
          }
          reload();
        } else {
          failed(result);
        }
      });
    },
    [reload, editingId],
  );

  return (
    <Box data-testid="relevance-topics" sx={{ mt: 4 }}>
      <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>
        Topics
      </Typography>
      <Typography variant="body2" sx={{ color: 'text.secondary', mb: 1.5 }}>
        Named keyword sets that weight what news is salient — <b>global</b> (market-wide, like{' '}
        <code>CPI</code>) or scoped to one <b>instrument</b> (<code>crude supply</code> →{' '}
        <code>CL</code>).
      </Typography>

      <Box component="form" noValidate onSubmit={submit} sx={{ mb: 2 }}>
        <Stack spacing={1.5}>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} alignItems="flex-start">
            <TextField
              label="Topic name"
              value={name}
              onChange={(event) => setName(event.target.value)}
              size="small"
            />
            <TextField
              label="Keywords"
              value={keywords}
              onChange={(event) => setKeywords(event.target.value)}
              size="small"
              fullWidth
              helperText="Comma-separated — e.g. rate, powell, dot plot"
            />
          </Stack>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} alignItems="center">
            <ToggleButtonGroup
              exclusive
              size="small"
              value={scope}
              onChange={(_, value: TopicScope | null) => value !== null && setScope(value)}
            >
              <ToggleButton value={TopicScope.Global}>Global</ToggleButton>
              <ToggleButton value={TopicScope.Instrument}>Instrument</ToggleButton>
            </ToggleButtonGroup>
            {isInstrument ? (
              <TextField
                label="Instrument"
                value={instrument}
                onChange={(event) => setInstrument(event.target.value)}
                size="small"
              />
            ) : null}
            <Box sx={{ flexGrow: 1 }} />
            {editingId !== null ? (
              <Button variant="text" onClick={resetForm} disabled={saving}>
                Cancel
              </Button>
            ) : null}
            <Button type="submit" variant="contained" disabled={!canSubmit || saving}>
              {saving ? 'Saving…' : editingId !== null ? 'Save topic' : 'Add topic'}
            </Button>
          </Stack>
        </Stack>
      </Box>

      {actionError !== null ? (
        <Alert severity="warning" sx={{ mb: 2 }} onClose={() => setActionError(null)}>
          {actionError}
        </Alert>
      ) : null}

      {state.kind === 'loading' ? <LoadingState label="Loading the topics" /> : null}

      {state.kind === 'error' ? (
        <EmptyState
          title="Could not load the topics"
          description={state.message}
          action={
            <Button variant="outlined" size="small" onClick={reload}>
              Try again
            </Button>
          }
          tag="R-2"
        />
      ) : null}

      {state.kind === 'loaded' && state.topics.length === 0 ? (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          No topics yet — add one above to start weighting the news that matters.
        </Typography>
      ) : null}

      {state.kind === 'loaded' && state.topics.length > 0 ? (
        <TopicGroups
          topics={state.topics}
          onEdit={startEdit}
          onDelete={remove}
          deleting={deleting}
        />
      ) : null}
    </Box>
  );
}

interface TopicGroupsProps {
  readonly topics: readonly Topic[];
  readonly onEdit: (topic: Topic) => void;
  readonly onDelete: (topic: Topic) => void;
  readonly deleting: ReadonlySet<string>;
}

/** Splits the flat topic list the way the operator reasons about it: per instrument, then market-wide. */
function TopicGroups({ topics, onEdit, onDelete, deleting }: TopicGroupsProps) {
  const instruments = [
    ...new Set(
      topics
        .filter((topic) => topic.scope === TopicScope.Instrument && topic.instrument !== null)
        .map((topic) => topic.instrument as string),
    ),
  ].sort((left, right) => left.localeCompare(right));
  const globalTopics = topics.filter((topic) => topic.scope === TopicScope.Global);

  return (
    <Stack spacing={2}>
      {instruments.map((instrument) => (
        <Box key={instrument}>
          <Typography variant="overline" sx={{ color: 'text.secondary' }}>
            Topics for {instrument}
          </Typography>
          <Stack spacing={1}>
            {topics
              .filter(
                (topic) => topic.scope === TopicScope.Instrument && topic.instrument === instrument,
              )
              .map((topic) => (
                <TopicRow
                  key={topic.id}
                  topic={topic}
                  onEdit={onEdit}
                  onDelete={onDelete}
                  deleting={deleting.has(topic.id)}
                />
              ))}
          </Stack>
        </Box>
      ))}

      {globalTopics.length > 0 ? (
        <Box>
          <Typography variant="overline" sx={{ color: 'text.secondary' }}>
            Global topics · market-wide
          </Typography>
          <Stack spacing={1}>
            {globalTopics.map((topic) => (
              <TopicRow
                key={topic.id}
                topic={topic}
                onEdit={onEdit}
                onDelete={onDelete}
                deleting={deleting.has(topic.id)}
              />
            ))}
          </Stack>
        </Box>
      ) : null}
    </Stack>
  );
}

interface TopicRowProps {
  readonly topic: Topic;
  readonly onEdit: (topic: Topic) => void;
  readonly onDelete: (topic: Topic) => void;
  readonly deleting: boolean;
}

function TopicRow({ topic, onEdit, onDelete, deleting }: TopicRowProps) {
  return (
    <Paper
      variant="outlined"
      sx={{ px: 2, py: 1, display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}
    >
      <Box sx={{ minWidth: 0 }}>
        <Typography variant="body2" sx={{ fontWeight: 600 }}>
          {topic.name}
        </Typography>
        {topic.keywords.length > 0 ? (
          <Typography variant="caption" sx={{ color: 'text.secondary' }} noWrap>
            {topic.keywords.join(', ')}
          </Typography>
        ) : null}
      </Box>
      <Box sx={{ flexShrink: 0 }}>
        <IconButton
          size="small"
          aria-label={`Edit the ${topic.name} topic`}
          onClick={() => onEdit(topic)}
        >
          <EditOutlinedIcon fontSize="small" />
        </IconButton>
        <IconButton
          size="small"
          aria-label={`Delete the ${topic.name} topic`}
          onClick={() => onDelete(topic)}
          disabled={deleting}
        >
          <DeleteOutlineIcon fontSize="small" />
        </IconButton>
      </Box>
    </Paper>
  );
}
