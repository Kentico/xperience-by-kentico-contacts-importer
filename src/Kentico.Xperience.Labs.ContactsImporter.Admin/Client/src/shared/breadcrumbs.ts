import * as AdminBase from '@kentico/xperience-admin-base';
import { useEffect } from 'react';

/**
 * The admin's breadcrumbs context. `useBreadcrumbs` is exported by `@kentico/xperience-admin-base` at runtime but not
 * in its typings, so it is read defensively: if a later admin version drops or changes it, the breadcrumb keeps the
 * page name from the server and nothing else changes.
 */
interface BreadcrumbsApi {
  readonly breadcrumbs?: readonly unknown[];
  readonly refreshItem?: (index: number, text: string) => void;
}

const useAdminBreadcrumbs: () => BreadcrumbsApi | null =
  typeof (AdminBase as unknown as { useBreadcrumbs?: unknown }).useBreadcrumbs === 'function'
    ? (AdminBase as unknown as { useBreadcrumbs: () => BreadcrumbsApi }).useBreadcrumbs
    : () => null;

/** Sets the text of the last breadcrumb (the page in view), e.g. "Import contacts.csv"; `null` leaves it as it is. */
export function useLastBreadcrumbText(text: string | null): void {
  const api = useAdminBreadcrumbs();
  const count = Array.isArray(api?.breadcrumbs) ? api.breadcrumbs.length : 0;
  const refreshItem = api?.refreshItem;

  useEffect(() => {
    if (text !== null && count > 0 && typeof refreshItem === 'function') {
      refreshItem(count - 1, text);
    }
  }, [count, refreshItem, text]);
}
