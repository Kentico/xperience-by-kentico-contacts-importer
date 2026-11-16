import { usePageCommandProvider } from '@kentico/xperience-admin-base';
import { useCallback, useEffect, useRef } from 'react';
import { useNavigate } from 'react-router';

import { callWithLinkedAbort } from './abort';
import type { CommandName, ExecuteCommand } from './commands';

/**
 * Page command executor of the current page as an `ExecuteCommand`: resolves with the command
 * result, or `undefined` when the request failed. Stable across renders.
 */
export function useExecuteCommand(): ExecuteCommand {
  const { executeCommand } = usePageCommandProvider();
  const latest = useRef(executeCommand);
  latest.current = executeCommand;

  return useCallback(
    <TResult, TData = void>(name: CommandName, data?: TData, signal?: AbortSignal) =>
      callWithLinkedAbort(signal, (controller) => latest.current<TResult, TData>(name, data, undefined, controller)),
    [],
  );
}

/**
 * Navigates inside the administration without a page load. `path` is an admin page path from the
 * server (`IPageLinkGenerator.GetPath`), relative to the admin root.
 */
export function useAdminNavigate(): (path: string) => void {
  const navigate = useNavigate();

  return useCallback(
    (path: string) => {
      void navigate(path.startsWith('/') ? path : `/${path}`);
    },
    [navigate],
  );
}

/** True until the component unmounts; guards state updates after awaits. */
export function useIsMounted(): () => boolean {
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;

    return () => {
      mounted.current = false;
    };
  }, []);

  return useCallback(() => mounted.current, []);
}
