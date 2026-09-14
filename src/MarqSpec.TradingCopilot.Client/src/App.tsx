import { AuthProvider } from './auth/AuthProvider';
import { ErrorBoundary } from './components/ErrorBoundary';
import { AppRoutes } from './routes/AppRoutes';
import { ThemeModeProvider } from './theme/ThemeModeProvider';

/**
 * The application, minus its router -- `main.tsx` supplies a `BrowserRouter`, tests supply a
 * `MemoryRouter`, and neither has to know about the other.
 *
 * `AuthProvider` sits below the boundary and above the routes: it establishes the session once, and every
 * surface -- the guard that turns signed-out operators away, the sign-in form that lets them back in --
 * reads it from there.
 *
 * The outer boundary is a backstop for a throw in the shell itself. It cannot keep the safety controls
 * on screen (they are inside what failed); it only guarantees the operator sees an explanation instead
 * of a white page. The boundary that actually protects them is inside `AppShell`, around the routed
 * surface.
 */
export function App() {
  return (
    <ThemeModeProvider>
      <ErrorBoundary title="The application failed to start">
        <AuthProvider>
          <AppRoutes />
        </AuthProvider>
      </ErrorBoundary>
    </ThemeModeProvider>
  );
}
