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
import type { ImportActionResponse, ImportJobRequest, ImportListItemDto, ImportListResponse, JobState } from '../shared/commands';
import { useAdminNavigate, useExecuteCommand, useIsMounted } from '../shared/hooks';
import { useText } from '../shared/i18n';
import { TestIds, Text, actionColumn, stringCell, testId, textColumn } from '../shared/ui';

/** Client properties of `ImportsListPage` (UIPages/ImportsListPage.cs). */
interface ImportsListProps {
  readonly canImport: boolean;
  readonly importPathTemplate: string;
  readonly jobIdPlaceholder: string;
}

const OPEN_ACTION = 'open';
const DELETE_ACTION = 'delete';

/** Text of each job state. */
const STATE_KEYS: Record<JobState, string> = {
  Configuring: 'contactsimporter.states.configuring',
  Uploading: 'contactsimporter.states.uploading',
  Uploaded: 'contactsimporter.states.uploaded',
  Analyzing: 'contactsimporter.states.analyzing',
  Analyzed: 'contactsimporter.states.analyzed',
  Confirmed: 'contactsimporter.states.confirmed',
  Running: 'contactsimporter.states.running',
  Paused: 'contactsimporter.states.paused',
  Completed: 'contactsimporter.states.completed',
  Failed: 'contactsimporter.states.failed',
  Cancelled: 'contactsimporter.states.cancelled',
};

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
  const { t, bytes, dateTime, number } = useText();
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
      setError(t('contactsimporter.list.loadfailed'));
      setImports((current) => current ?? []);

      return;
    }

    setError(null);
    setImports(result.imports);
    setTruncated(result.truncated);
  }, [execute, isMounted, t]);

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
      setError(result?.message ?? t('contactsimporter.list.deletefailed'));
    }
    await load();
  }, [execute, isMounted, load, t, toDelete]);

  const columns = useMemo(
    () => [
      textColumn('file', t('contactsimporter.list.columns.file'), 24, 60),
      textColumn('size', t('contactsimporter.list.columns.size'), 10, 14),
      textColumn('created', t('contactsimporter.list.columns.created'), 18, 24),
      textColumn('createdBy', t('contactsimporter.list.columns.createdby'), 14, 24),
      textColumn('state', t('contactsimporter.list.columns.state'), 16, 24),
      textColumn('rows', t('contactsimporter.list.columns.rows'), 12, 16),
      textColumn('contactsCreated', t('contactsimporter.list.columns.contactscreated'), 12, 16),
      textColumn('contactsUpdated', t('contactsimporter.list.columns.contactsupdated'), 12, 16),
      textColumn('recipientList', t('contactsimporter.list.columns.recipientlist'), 12, 24),
      textColumn('consent', t('contactsimporter.list.columns.consent'), 12, 24),
      ...(canImport ? [actionColumn('actions', t('contactsimporter.list.columns.actions'), 10, 14)] : []),
    ],
    [canImport, t],
  );

  const rows = useMemo<TableRow[] | undefined>(
    () =>
      imports?.map((item) => {
        const cells: TableCell[] = [
          stringCell('file', item.fileName),
          stringCell('size', bytes(item.fileSize)),
          stringCell('created', dateTime(item.createdUtc)),
          stringCell('createdBy', item.createdBy),
          stringCell('state', STATE_KEYS[item.state] ? t(STATE_KEYS[item.state]) : item.state),
          stringCell('rows', item.rowsValid > 0 ? number(item.rowsValid) : ''),
          stringCell('contactsCreated', item.importStarted ? number(item.created) : ''),
          stringCell('contactsUpdated', item.importStarted ? number(item.updated) : ''),
          stringCell('recipientList', item.recipientListName ?? ''),
          stringCell('consent', item.consentName ?? ''),
        ];
        if (canImport) {
          const actions: TableAction[] = [
            {
              identifier: OPEN_ACTION,
              label: item.canContinue ? t('contactsimporter.list.continue') : t('contactsimporter.list.open'),
              icon: 'xp-arrow-right-top-square',
              disabled: false,
              destructive: false,
            },
            {
              identifier: DELETE_ACTION,
              label: t('contactsimporter.list.deletefiles'),
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
    [imports, canImport, openImport, t, bytes, dateTime, number],
  );

  return (
    <Box spacing={Spacing.XL}>
      <Stack spacing={Spacing.L}>
        <Headline size={HeadlineSize.L}>{t('contactsimporter.list.headline')}</Headline>
        <Text>{t('contactsimporter.list.intro')}</Text>
        {error && <NotificationBarAlert onDismiss={() => setError(null)}>{error}</NotificationBarAlert>}
        {canImport ? (
          <div>
            <Button
              label={t('contactsimporter.list.newimport')}
              color={ButtonColor.Primary}
              icon="xp-plus"
              dataTestId={testId(TestIds.newImport)}
              onClick={() => openImport(crypto.randomUUID())}
            />
          </div>
        ) : (
          <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk} dataTestId={testId(TestIds.noPermission)}>
            {t('contactsimporter.app.nopermission')}
          </Callout>
        )}
        {rows !== undefined && rows.length === 0 && !error && <Text>{t('contactsimporter.list.empty')}</Text>}
        {(rows === undefined || rows.length > 0) && (
          <Table
            columns={columns}
            rows={rows}
            dataTestId={testId(TestIds.importsTable)}
            onRowClick={canImport ? (identifier) => openImport(String(identifier)) : undefined}
          />
        )}
        {truncated && <Text hint={true}>{t('contactsimporter.list.truncated')}</Text>}
      </Stack>
      <Dialog
        isOpen={toDelete !== null}
        headline={t('contactsimporter.list.deleteheadline')}
        headerCloseButton={{ tooltipText: t('contactsimporter.list.close') }}
        isDismissable={!deleting}
        actionInProgress={deleting}
        onClose={() => setToDelete(null)}
        confirmAction={{ label: t('contactsimporter.list.deleteconfirm'), destructive: true, onClick: () => void confirmDelete() }}
        cancelAction={{ label: t('contactsimporter.list.cancel'), onClick: () => setToDelete(null) }}
      >
        <Text>{t('contactsimporter.list.deletemessage', { fileName: toDelete?.fileName ?? '' })}</Text>
      </Dialog>
    </Box>
  );
};
