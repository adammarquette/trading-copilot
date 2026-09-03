import AddCommentIcon from '@mui/icons-material/AddComment';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import List from '@mui/material/List';
import ListItemButton from '@mui/material/ListItemButton';
import ListItemText from '@mui/material/ListItemText';
import Typography from '@mui/material/Typography';

import type { Conversation } from '../api/chat';
import { EmptyState } from '../components/EmptyState';

export interface ConversationListProps {
  /** The operator's conversations, in the order to render -- most-recent-first is the server's ordering, not this component's. */
  readonly conversations: readonly Conversation[];
  readonly selectedId: string | null;
  readonly onSelect: (id: string) => void;
  readonly onNew: () => void;
  /** Disables "New conversation" while a create is in flight, so a double-click cannot start two threads. */
  readonly creating: boolean;
}

/**
 * The conversation list pane of the `/chat` surface (gh#1063, #323). Purely presentational -- {@link ChatSurface}
 * owns the load, the selection and the create call -- so the ordering shown here is exactly what the caller passed,
 * never re-sorted locally (the server's `updatedAt` desc ordering is the one truth for "most recent").
 */
export function ConversationList({
  conversations,
  selectedId,
  onSelect,
  onNew,
  creating,
}: ConversationListProps) {
  return (
    <Box
      data-testid="conversation-list"
      sx={{ height: '100%', display: 'flex', flexDirection: 'column' }}
    >
      <Box sx={{ p: 1.5 }}>
        <Button
          fullWidth
          variant="outlined"
          size="small"
          startIcon={<AddCommentIcon />}
          disabled={creating}
          onClick={onNew}
        >
          New conversation
        </Button>
      </Box>

      {conversations.length === 0 ? (
        <EmptyState
          title="No conversations yet"
          description="Start one to ask the co-pilot about a setup, a rule, or a day just traded."
          tag="R-6"
        />
      ) : (
        <List dense disablePadding sx={{ overflowY: 'auto', flex: 1 }}>
          {conversations.map((conversation) => {
            const selected = conversation.id === selectedId;
            return (
              <ListItemButton
                key={conversation.id}
                data-testid="conversation-row"
                selected={selected}
                aria-current={selected ? 'true' : 'false'}
                onClick={() => onSelect(conversation.id)}
              >
                <ListItemText
                  primary={
                    <Typography variant="body2" noWrap sx={{ fontWeight: selected ? 600 : 400 }}>
                      {conversation.title ?? 'Untitled'}
                    </Typography>
                  }
                />
              </ListItemButton>
            );
          })}
        </List>
      )}
    </Box>
  );
}
