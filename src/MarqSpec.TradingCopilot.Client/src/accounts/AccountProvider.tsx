import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { ReactNode } from 'react';

import { type Account, listAllAccounts } from '../api/accounts';

/** Where the operator's chosen account id persists, so a reload lands them back on the account they were trading. */
const ACTIVE_ACCOUNT_KEY = 'tc.active-account';

/**
 * The account context (gh#653, R-14): the single source of the **active account** every other surface scopes to.
 * `ready` always carries a resolved `activeAccount`; there is no in-between where a surface has to guess which
 * account an order would hit.
 */
export type AccountContextValue =
  | { readonly status: 'loading' }
  | { readonly status: 'error'; readonly message: string; readonly reload: () => void }
  | { readonly status: 'empty'; readonly reload: () => void }
  | {
      readonly status: 'ready';
      readonly accounts: readonly Account[];
      readonly activeAccount: Account;
      readonly setActiveAccount: (id: string) => void;
      readonly reload: () => void;
    };

const AccountContext = createContext<AccountContextValue | null>(null);

/** The account context. Throws outside an {@link AccountProvider} so a mis-mounted surface fails loudly. */
export function useAccounts(): AccountContextValue {
  const value = useContext(AccountContext);
  if (value === null) {
    throw new Error('useAccounts must be used inside an <AccountProvider>.');
  }
  return value;
}

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly accounts: readonly Account[] };

/**
 * Owns the roster and the active-account selection. It loads every account once (across all connections), lets the
 * operator switch, and remembers the choice. The active account is resolved here rather than stored raw: the
 * persisted id is honoured only while that account still exists, and otherwise falls back to a stable default, so
 * a removed or renamed account never leaves the app pointing at nothing.
 */
export function AccountProvider({ children }: { readonly children: ReactNode }) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [activeId, setActiveId] = useState<string | null>(() =>
    localStorage.getItem(ACTIVE_ACCOUNT_KEY),
  );
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const reload = useCallback(() => {
    setState({ kind: 'loading' });
    void listAllAccounts().then((result) => {
      if (!mounted.current) {
        return;
      }
      setState(
        result.ok
          ? { kind: 'loaded', accounts: result.data }
          : { kind: 'error', message: result.kind === 'refused' ? result.reason : result.error },
      );
    });
  }, []);

  useEffect(() => {
    reload();
  }, [reload]);

  const setActiveAccount = useCallback((id: string) => {
    localStorage.setItem(ACTIVE_ACCOUNT_KEY, id);
    setActiveId(id);
  }, []);

  const value = useMemo<AccountContextValue>(() => {
    if (state.kind === 'loading') {
      return { status: 'loading' };
    }
    if (state.kind === 'error') {
      return { status: 'error', message: state.message, reload };
    }
    if (state.accounts.length === 0) {
      return { status: 'empty', reload };
    }
    // The stored choice if it still exists, else a stable default — never a dangling selection.
    const activeAccount =
      state.accounts.find((account) => account.id === activeId) ?? state.accounts[0];
    return { status: 'ready', accounts: state.accounts, activeAccount, setActiveAccount, reload };
  }, [state, activeId, reload, setActiveAccount]);

  return <AccountContext.Provider value={value}>{children}</AccountContext.Provider>;
}
