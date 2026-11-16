import {
  Button,
  ButtonColor,
  Callout,
  CalloutPlacementType,
  CalloutType,
  Dialog,
  Headline,
  HeadlineSize,
  Inline,
  NotificationBarAlert,
  NotificationBarInfo,
  Spacing,
  Stack,
} from '@kentico/xperience-admin-components';
import React, { useCallback, useState } from 'react';

import { Commands } from '../shared/commands';
import type { ExecuteCommand, FieldDto, ImportActionResponse } from '../shared/commands';
import { formatNumber } from '../shared/format';
import { useIsMounted } from '../shared/hooks';
import { Strings, format } from '../shared/strings';
import { TestIds, Text, testId } from '../shared/ui';

import { AggregatesTable, FieldChangesTable } from './Aggregates';
import type { JobView } from './model';
import { ReportButton } from './ReportButton';

const S = Strings.results;

export interface ResultsStepProps {
  readonly execute: ExecuteCommand;
  readonly view: JobView;
  readonly fields: readonly FieldDto[];
  readonly onOpenContactGroups: () => void;
  readonly onBackToList: () => void;
  /** Files were deleted: load the job again. */
  readonly onReload: () => void;
}

/**
 * Results step: how the import ended, the counts of the check next to what was imported, the row
 * reports, and what is left for the marketer to do (contact groups, cache).
 */
export const ResultsStep = ({ execute, view, fields, onOpenContactGroups, onBackToList, onReload }: ResultsStepProps) => {
  const isMounted = useIsMounted();
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleted, setDeleted] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const { status, progress, reports } = view;
  const state = status.state;
  // The import itself ran (at least started): the running counts are what was imported.
  const imported = progress.phase === 'execute' ? progress.aggregates : null;
  const changedContacts = imported !== null && imported.created + imported.contactsUpdated > 0;
  const hasReports = reports.analyze || reports.execute;

  const deleteFiles = useCallback(async () => {
    setDeleting(true);
    setError(null);
    const response = await execute<ImportActionResponse>(Commands.DeleteFiles);
    if (!isMounted()) {
      return;
    }

    setDeleting(false);
    setDeleteOpen(false);
    if (response?.code === 'ok') {
      setDeleted(true);
      onReload();
    } else {
      setError(response?.message ?? S.deleteFailed);
    }
  }, [execute, isMounted, onReload]);

  let summary: string;
  if (imported === null) {
    summary = S.notStarted;
  } else if (state === 'Completed') {
    summary = format(S.completed, formatNumber(imported.rows));
  } else {
    summary = format(state === 'Failed' ? S.failedPartial : S.cancelledPartial, formatNumber(imported.rows), formatNumber(progress.rowsTotal));
  }

  return (
    <Stack spacing={Spacing.L}>
      <Headline size={HeadlineSize.M}>
        {state === 'Completed' ? S.headlineCompleted : state === 'Failed' ? S.headlineFailed : S.headlineCancelled}
      </Headline>
      <Text data-testid={TestIds.resultsStatus} data-job-state={state} data-import-started={imported !== null}>
        {summary}
      </Text>
      {state === 'Failed' && status.error && <NotificationBarAlert>{format(S.error, status.error)}</NotificationBarAlert>}
      {error && <NotificationBarAlert onDismiss={() => setError(null)}>{error}</NotificationBarAlert>}
      {deleted && <NotificationBarInfo onDismiss={() => setDeleted(false)}>{S.deleted}</NotificationBarInfo>}

      {(imported !== null || progress.analysis !== null) && (
        <>
          <Headline size={HeadlineSize.S}>{S.tableHeadline}</Headline>
          <AggregatesTable expected={progress.analysis} actual={imported} />
        </>
      )}
      {imported !== null && imported.fields.length > 0 && (
        <>
          <Headline size={HeadlineSize.S}>{Strings.review.fieldsHeadline}</Headline>
          <FieldChangesTable aggregates={imported} fields={fields} />
        </>
      )}

      {changedContacts && (
        <Callout
          type={CalloutType.QuickTip}
          placement={CalloutPlacementType.OnDesk}
          headline={S.groupsHeadline}
          actionButton={
            <Button label={S.groupsLink} color={ButtonColor.Secondary} dataTestId={testId(TestIds.contactGroupsLink)} onClick={onOpenContactGroups} />
          }
        >
          {S.groupsText}
        </Callout>
      )}
      {imported !== null && state !== 'Completed' && (
        <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk} headline={S.cacheHeadline}>
          {S.cacheText}
        </Callout>
      )}

      <Headline size={HeadlineSize.S}>{S.reportsHeadline}</Headline>
      {!hasReports && <Text hint={true}>{S.noReports}</Text>}
      {hasReports && <Text hint={true}>{reports.includesKey ? Strings.review.reportHintKey : Strings.review.reportHint}</Text>}
      <Inline spacing={Spacing.M}>
        {reports.execute && <ReportButton execute={execute} phase="execute" label={S.downloadImportReport} />}
        {reports.analyze && <ReportButton execute={execute} phase="analyze" label={S.downloadCheckReport} />}
      </Inline>

      <Inline spacing={Spacing.M}>
        <Button label={Strings.app.backToList} color={ButtonColor.Secondary} onClick={onBackToList} />
        {hasReports && (
          <Button
            label={S.deleteFiles}
            color={ButtonColor.Secondary}
            destructive={true}
            dataTestId={testId(TestIds.deleteFiles)}
            onClick={() => setDeleteOpen(true)}
          />
        )}
      </Inline>

      <Dialog
        isOpen={deleteOpen}
        headline={S.deleteHeadline}
        headerCloseButton={{ tooltipText: Strings.leave.close }}
        isDismissable={!deleting}
        actionInProgress={deleting}
        onClose={() => setDeleteOpen(false)}
        confirmAction={{ label: S.deleteConfirm, destructive: true, onClick: () => void deleteFiles() }}
        cancelAction={{ label: Strings.leave.cancel, onClick: () => setDeleteOpen(false) }}
      >
        <Text>{S.deleteMessage}</Text>
      </Dialog>
    </Stack>
  );
};
