import Button from '@mui/material/Button';
import { Link, Route, Routes } from 'react-router';

import { EmptyState } from '../components/EmptyState';
import { AppShell } from '../layout/AppShell';
import { destinations } from '../navigation/destinations';
import { SurfacePlaceholder } from './SurfacePlaceholder';

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
      <Route element={<AppShell />}>
        {destinations.map((destination) =>
          destination.path === '/' ? (
            <Route
              key={destination.id}
              index
              element={<SurfacePlaceholder destination={destination} />}
            />
          ) : (
            <Route
              key={destination.id}
              path={destination.path}
              element={<SurfacePlaceholder destination={destination} />}
            />
          ),
        )}
        <Route path="*" element={<NotFoundSurface />} />
      </Route>
    </Routes>
  );
}
