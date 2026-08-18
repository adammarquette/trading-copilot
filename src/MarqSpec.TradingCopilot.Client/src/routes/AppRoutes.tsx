import Button from '@mui/material/Button';
import { Link, Route, Routes } from 'react-router';

import { AccountProvider } from '../accounts/AccountProvider';
import { AcceptInvitePage } from '../auth/AcceptInvitePage';
import { RequireAuth } from '../auth/RequireAuth';
import { SignInPage } from '../auth/SignInPage';
import { EmptyState } from '../components/EmptyState';
import { AppShell } from '../layout/AppShell';
import { type Destination, destinations } from '../navigation/destinations';
import { DetachedPanelPage } from '../panels/DetachedPanelPage';
import { RealtimeProvider } from '../realtime/RealtimeProvider';
import { SurfacePlaceholder } from './SurfacePlaceholder';

/**
 * What a destination renders: its own surface once that card has landed, and the placeholder until then. Read
 * from the one navigation table, so "built" is a field on the destination rather than a second list.
 */
function surfaceFor(destination: Destination) {
  const Surface = destination.Surface ?? SurfacePlaceholder;
  return <Surface destination={destination} />;
}

/**
 * An unmatched path. Still rendered inside the shell -- a 404 is not a reason to drop the operator's
 * safety controls, and "back to the workspace" is one click rather than a browser Back guess.
 */
function NotFoundSurface() {
  return (
    <EmptyState
      title="No such surface"
      description="That address does not match anything in this application."
      action={
        <Button component={Link} to="/" variant="outlined" size="small">
          Back to the workspace
        </Button>
      }
    />
  );
}

/**
 * The route table, generated from the navigation table.
 *
 * Two tiers, and the split is the R-18 authorization boundary drawn in the client:
 *
 * - `/sign-in` and `/accept-invite` are the **anonymous** surfaces. They sit outside both the guard and
 *   the shell -- an operator with no session must be able to reach them, and they carry no navigation or
 *   safety region because there is no session to drive one.
 * - Everything else sits behind `RequireAuth`, inside `AppShell`. The generated surfaces (and the 404,
 *   which is a signed-in operator mistyping a path -- not a reason to drop their safety controls) live
 *   there.
 *
 * Generated, not hand-written beside it: a destination with no route renders a dead link, a route with
 * no destination is unreachable, and both are the kind of drift nobody notices until a demo. One list
 * makes each impossible.
 *
 * The router itself lives above this (a `BrowserRouter` in `main.tsx`), so tests can mount the same
 * tree inside a `MemoryRouter` at any path.
 */
export function AppRoutes() {
  return (
    <Routes>
      <Route path="/sign-in" element={<SignInPage />} />
      <Route path="/accept-invite" element={<AcceptInvitePage />} />
      <Route element={<RequireAuth />}>
        <Route
          element={
            <RealtimeProvider>
              <AccountProvider>
                <AppShell />
              </AccountProvider>
            </RealtimeProvider>
          }
        >
          {destinations.map((destination) =>
            destination.path === '/' ? (
              <Route key={destination.id} index element={surfaceFor(destination)} />
            ) : (
              <Route
                key={destination.id}
                path={destination.path}
                element={surfaceFor(destination)}
              />
            ),
          )}
          <Route path="*" element={<NotFoundSurface />} />
        </Route>

        {/*
          Detached pop-out panels (gh#651, ADR-0006). The SAME authenticated providers as the shell — so each
          pop-out is its OWN SignalR client (RealtimeProvider is mount-scoped, one connection per window, torn
          down with it) with the JWT shared same-origin (ADR-0003) — but a LEAN frame ({@link DetachedPanelPage})
          instead of the full shell: a single-panel window is not a place you navigate from. Multi-window is a
          VIEW concern only; no pop-out is ever a second execution path. The static `/panel/` segment outranks the
          shell's `*` catch-all above, so a panel URL lands here rather than on the 404.
        */}
        <Route
          path="/panel/:panelId"
          element={
            <RealtimeProvider>
              <AccountProvider>
                <DetachedPanelPage />
              </AccountProvider>
            </RealtimeProvider>
          }
        />
      </Route>
    </Routes>
  );
}
