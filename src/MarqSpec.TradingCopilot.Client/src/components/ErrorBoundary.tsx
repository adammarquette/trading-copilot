import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { Component, type ErrorInfo, type ReactNode } from 'react';

/**
 * Catches a render-time throw from the subtree below it and offers a way back.
 *
 * Placed around the *routed surface*, not around the whole app: without a boundary, one broken pane
 * unmounts the entire React tree, and the entire tree includes the persistent safety region. A blank
 * page during a live position is the worst outcome this shell can produce, so the boundary's real job
 * is to keep the app bar -- and the kill switch and countdown in it -- on screen while a surface fails.
 *
 * A second boundary sits above the shell in `App`, for the case where the shell itself throws. That one
 * can only apologise; it is a backstop, not a feature.
 *
 * Note the limits of the mechanism: React error boundaries catch render, lifecycle and constructor
 * throws. They do not catch errors in event handlers, in `setTimeout`, or in a rejected promise -- those
 * belong to whoever wrote them.
 */
export interface ErrorBoundaryProps {
  readonly children: ReactNode;
  /** Heading for the failure state. Defaults to something true of any surface. */
  readonly title?: string;
  /**
   * Change this to clear a caught error automatically -- the router's location key is the intended
   * value, so navigating away from a broken surface does not strand the operator on its error card.
   */
  readonly resetKey?: string;
  /** Where the error goes. Defaults to the console; a real sink can be threaded in later. */
  readonly onError?: (error: Error, info: ErrorInfo) => void;
}

interface ErrorBoundaryState {
  readonly error: Error | null;
  readonly resetKey: string | undefined;
}

function toError(thrown: unknown): Error {
  // A throw can be anything -- a string, a number, an object from a library. Normalising here means the
  // failure UI never has to render `[object Object]`.
  return thrown instanceof Error ? thrown : new Error(String(thrown));
}

export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  constructor(props: ErrorBoundaryProps) {
    super(props);
    this.state = { error: null, resetKey: props.resetKey };
  }

  static getDerivedStateFromError(thrown: unknown): Pick<ErrorBoundaryState, 'error'> {
    return { error: toError(thrown) };
  }

  static getDerivedStateFromProps(
    props: ErrorBoundaryProps,
    state: ErrorBoundaryState,
  ): ErrorBoundaryState | null {
    if (props.resetKey === state.resetKey) return null;

    // The key moved, so whatever failed is no longer on screen. Clear the error rather than making the
    // operator find the retry button for a surface they already navigated away from.
    return { error: null, resetKey: props.resetKey };
  }

  override componentDidCatch(thrown: unknown, info: ErrorInfo): void {
    const { onError } = this.props;
    const error = toError(thrown);

    if (onError) {
      onError(error, info);
      return;
    }

    console.error('A surface failed to render.', error, info.componentStack);
  }

  private readonly retry = (): void => {
    this.setState({ error: null });
  };

  override render(): ReactNode {
    const { children, title = 'This surface stopped responding' } = this.props;
    const { error } = this.state;

    if (error === null) return children;

    return (
      <Box
        data-testid="error-boundary-fallback"
        sx={{ display: 'grid', placeItems: 'center', minHeight: '100%', p: 3 }}
      >
        <Alert
          severity="error"
          variant="outlined"
          sx={{ maxWidth: '60ch' }}
          action={
            <Button color="inherit" size="small" onClick={this.retry}>
              Try again
            </Button>
          }
        >
          <AlertTitle>{title}</AlertTitle>
          <Typography variant="body2" sx={{ mb: 1 }}>
            The rest of the app is still running -- the kill switch and the time-to-flat countdown
            are unaffected.
          </Typography>
          {/* The message, not the stack. The operator gets something they can quote; the stack is
              already in the console for whoever is debugging. */}
          <Typography variant="numeric" sx={{ color: 'text.secondary', fontSize: 11 }}>
            {error.message}
          </Typography>
        </Alert>
      </Box>
    );
  }
}
