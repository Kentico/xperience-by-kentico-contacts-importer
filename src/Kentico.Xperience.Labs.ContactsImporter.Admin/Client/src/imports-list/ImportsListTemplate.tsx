import { RoutingContentPlaceholder } from '@kentico/xperience-admin-base';
import {
  Box,
  Button,
  ButtonColor,
  Callout,
  CalloutPlacementType,
  CalloutType,
  CellType,
  Dialog,
  Headline,
  HeadlineSize,
  NotificationBarAlert,
  Spacing,
  Stack,
  Table,
} from '@kentico/xperience-admin-components';
import type { TableAction, TableCell, TableRow } from '@kentico/xperience-admin-components';
import React, { useCallback, useEffect, useMemo, useState } from 'react';

import { Commands } from '../shared/commands';
import type { ImportActionResponse, ImportJobRequest, ImportListItemDto, ImportListResponse } from '../shared/commands';
import { formatBytes, formatDateTime, formatNumber } from '../shared/format';
import { useAdminNavigate, useExecuteCommand, useIsMounted } from '../shared/hooks';
import { Strings, format } from '../shared/strings';
import { TestIds, Text, actionColumn, stringCell, testId, textColumn } from '../shared/ui';

/** Client properties of `ImportsListPage` (UIPages/ImportsListPage.cs). */
interface ImportsListProps {
  readonly canImport: boolean;
  readonly importPathTemplate: string;
  readonly jobIdPlaceholder: string;
}

const OPEN_ACTION = 'open';
const DELETE_ACTION = 'delete';

const S = Strings.list;

/**
 * Recent imports with "New import", continue and "Delete files". The import page is a child
 * route of this page and replaces the list while it is open.
 */
export const ImportsListTemplate = (props: ImportsListProps) => (
  <RoutingContentPlaceholder>
    <ImportsList {...props} />
  </RoutingContentPlaceholder>
);

const ImportsList = ({ canImport, importPathTemplate, jobIdPlaceholder }: ImportsListProps) => {
  const execute = useExecuteCommand();
  const navigate = useAdminNavigate();
  const isMounted = useIsMounted();
  const [imports, setImports] = useState<ImportListItemDto[] | undefined>(undefined);
  const [truncated, setTruncated] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [toDelete, setToDelete] = useState<ImportListItemDto | null>(null);
  const [deleting, setDeleting] = useState(false);

  const load = useCallback(async () => {
    const result = await execute<ImportListResponse>(Commands.ListImports);
    if (!isMounted()) {
      return;
    }
    if (!result) {
      setError(S.loadFailed);
      setImports((current) => current ?? []);

      return;
    }

    setError(null);
    setImports(result.imports);
    setTruncated(result.truncated);
  }, [execute, isMounted]);

  useEffect(() => {
    void load();
  }, [load]);

  const openImport = useCallback(
    (jobId: string) => navigate(importPathTemplate.replace(jobIdPlaceholder, jobId)),
    [navigate, importPathTemplate, jobIdPlaceholder],
  );

  const confirmDelete = useCallback(async () => {
    if (!toDelete) {
      return;
    }

    setDeleting(true);
    const result = await execute<ImportActionResponse, ImportJobRequest>(Commands.DeleteFiles, { jobId: toDelete.jobId });
    if (!isMounted()) {
      return;
    }

    setDeleting(false);
    setToDelete(null);
    if (!result || result.code !== 'ok') {
      setError(result?.message ?? S.deleteFailed);
    }
    await load();
  }, [execute, isMounted, load, toDelete]);

  const columns = useMemo(
    () => [
      textColumn('file', S.columns.file, 24, 60),
      textColumn('size', S.columns.size, 10, 14),
      textColumn('created', S.columns.created, 18, 24),
      textColumn('createdBy', S.columns.createdBy, 14, 24),
      textColumn('state', S.columns.state, 16, 24),
      textColumn('rows', S.columns.rows, 12, 16),
      textColumn('contactsCreated', S.columns.contactsCreated, 12, 16),
      textColumn('contactsUpdated', S.columns.contactsUpdated, 12, 16),
      ...(canImport ? [actionColumn('actions', S.columns.actions, 10, 14)] : []),
    ],
    [canImport],
  );

  const rows = useMemo<TableRow[] | undefined>(
    () =>
      imports?.map((item) => {
        const cells: TableCell[] = [
          stringCell('file', item.fileName),
          stringCell('size', formatBytes(item.fileSize)),
          stringCell('created', formatDateTime(item.createdUtc)),
          stringCell('createdBy', item.createdBy),
          stringCell('state', Strings.states[item.state] ?? item.state),
          stringCell('rows', item.rowsValid > 0 ? formatNumber(item.rowsValid) : ''),
          stringCell('contactsCreated', item.importStarted ? formatNumber(item.created) : ''),
          stringCell('contactsUpdated', item.importStarted ? formatNumber(item.updated) : ''),
        ];
        if (canImport) {
          const actions: TableAction[] = [
            {
              identifier: OPEN_ACTION,
              label: item.canContinue ? S.continue : S.open,
              icon: 'xp-arrow-right-top-square',
              disabled: false,
              destructive: false,
            },
            {
              identifier: DELETE_ACTION,
              label: S.deleteFiles,
              icon: 'xp-bin',
              disabled: !item.hasFiles && !item.canContinue,
              destructive: true,
            },
          ];
          cells.push({
            type: CellType.Action,
            columnName: 'actions',
            actions,
            onInvokeAction: (action: TableAction) => {
              if (action.identifier === DELETE_ACTION) {
                setToDelete(item);
              } else {
                openImport(item.jobId);
              }

              return Promise.resolve();
            },
          } as TableCell);
        }

        return { identifier: item.jobId, cells, disabled: false };
      }),
    [imports, canImport, openImport],
  );

  return (
    <Box spacing={Spacing.XL}>
      <Stack spacing={Spacing.L}>
        <Headline size={HeadlineSize.L}>{S.headline}</Headline>
        <Text>{S.intro}</Text>
        {error && <NotificationBarAlert onDismiss={() => setError(null)}>{error}</NotificationBarAlert>}
        {canImport ? (
          <div>
            <Button
              label={S.newImport}
              color={ButtonColor.Primary}
              icon="xp-plus"
              dataTestId={testId(TestIds.newImport)}
              onClick={() => openImport(crypto.randomUUID())}
            />
          </div>
        ) : (
          <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk} dataTestId={testId(TestIds.noPermission)}>
            {Strings.app.noPermission}
          </Callout>
        )}
        {rows !== undefined && rows.length === 0 && !error && <Text>{S.empty}</Text>}
        {(rows === undefined || rows.length > 0) && (
          <Table
            columns={columns}
            rows={rows}
            dataTestId={testId(TestIds.importsTable)}
            onRowClick={canImport ? (identifier) => openImport(String(identifier)) : undefined}
          />
        )}
        {truncated && <Text hint={true}>{S.truncated}</Text>}
      </Stack>
      <Dialog
        isOpen={toDelete !== null}
        headline={S.deleteHeadline}
        headerCloseButton={{ tooltipText: S.close }}
        isDismissable={!deleting}
        actionInProgress={deleting}
        onClose={() => setToDelete(null)}
        confirmAction={{ label: S.deleteConfirm, destructive: true, onClick: () => void confirmDelete() }}
        cancelAction={{ label: S.cancel, onClick: () => setToDelete(null) }}
      >
        <Text>{format(S.deleteMessage, toDelete?.fileName ?? '')}</Text>
      </Dialog>
    </Box>
  );
};
