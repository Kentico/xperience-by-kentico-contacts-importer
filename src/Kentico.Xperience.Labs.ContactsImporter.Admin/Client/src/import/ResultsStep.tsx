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
import { useIsMounted } from '../shared/hooks';
import { useText } from '../shared/i18n';
import { TestIds, Text, testId } from '../shared/ui';

import { AggregatesTable, FieldChangesTable } from './Aggregates';
import { ConsentSummary } from './ConsentSummary';
import type { JobView } from './model';
import { ReportButton } from './ReportButton';

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
  const { t } = useText();
  const isMounted = useIsMounted();
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleted, setDeleted] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const { job, status, progress, reports } = view;
  const usesList = job.options.recipientListId !== null;
  const usesConsent = job.options.consent !== 'none';
  const state = status.state;
  // The import itself ran (at least started): the running counts are what was imported.
  const imported = progress.phase === 'execute' ? progress.aggregates : null;
  const changedContacts = imported !== null && imported.created + imported.contactsUpdated > 0;
  // Nothing about the list or consent to show for an import that never had either and never started.
  const showConsentSummary = usesList || usesConsent || job.attestation !== null;
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
      setError(response?.message ?? t('contactsimporter.results.deletefailed'));
    }
  }, [execute, isMounted, onReload, t]);

  let summary: string;
  if (imported === null) {
    summary = t('contactsimporter.results.notstarted');
  } else if (state === 'Completed') {
    summary = t('contactsimporter.results.completed', { count: imported.rows, rows: imported.rows });
  } else {
    summary = t(state === 'Failed' ? 'contactsimporter.results.failedpartial' : 'contactsimporter.results.cancelledpartial', {
      done: imported.rows,
      total: progress.rowsTotal,
    });
  }

  return (
    <Stack spacing={Spacing.L}>
      <Headline size={HeadlineSize.M}>
        {state === 'Completed'
          ? t('contactsimporter.results.headlinecompleted')
          : state === 'Failed'
            ? t('contactsimporter.results.headlinefailed')
            : t('contactsimporter.results.headlinecancelled')}
      </Headline>
      <Text data-testid={TestIds.resultsStatus} data-job-state={state} data-import-started={imported !== null}>
        {summary}
      </Text>
      {state === 'Failed' && status.error && <NotificationBarAlert>{t('contactsimporter.results.error', { reason: status.error })}</NotificationBarAlert>}
      {error && <NotificationBarAlert onDismiss={() => setError(null)}>{error}</NotificationBarAlert>}
      {deleted && <NotificationBarInfo onDismiss={() => setDeleted(false)}>{t('contactsimporter.results.deleted')}</NotificationBarInfo>}

      {(imported !== null || progress.analysis !== null) && (
        <>
          <Headline size={HeadlineSize.S}>{t('contactsimporter.results.tableheadline')}</Headline>
          <AggregatesTable expected={progress.analysis} actual={imported} usesList={usesList} usesConsent={usesConsent} />
        </>
      )}
      {showConsentSummary && <ConsentSummary job={job} />}
      {imported !== null && imported.fields.length > 0 && (
        <>
          <Headline size={HeadlineSize.S}>{t('contactsimporter.review.fieldsheadline')}</Headline>
          <FieldChangesTable aggregates={imported} fields={fields} />
        </>
      )}

      {changedContacts && (
        <Callout
          type={CalloutType.QuickTip}
          placement={CalloutPlacementType.OnDesk}
          headline={t('contactsimporter.results.groupsheadline')}
          actionButton={
            <Button
                label={t('contactsimporter.results.groupslink')} color={ButtonColor.Secondary} dataTestId={testId(TestIds.contactGroupsLink)} onClick={onOpenContactGroups} />
          }
        >
          {t('contactsimporter.results.groupstext')}
        </Callout>
      )}
      {imported !== null && state !== 'Completed' && (
        <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk} headline={t('contactsimporter.results.cacheheadline')}>
          {t('contactsimporter.results.cachetext')}
        </Callout>
      )}

      <Headline size={HeadlineSize.S}>{t('contactsimporter.results.reportsheadline')}</Headline>
      {!hasReports && <Text hint={true}>{t('contactsimporter.results.noreports')}</Text>}
      {hasReports && <Text hint={true}>{reports.includesKey ? t('contactsimporter.review.reporthintkey') : t('contactsimporter.review.reporthint')}</Text>}
      <Inline spacing={Spacing.M}>
        {reports.execute && <ReportButton execute={execute} phase="execute" label={t('contactsimporter.results.downloadimportreport')} />}
        {reports.analyze && <ReportButton execute={execute} phase="analyze" label={t('contactsimporter.results.downloadcheckreport')} />}
      </Inline>

      <Inline spacing={Spacing.M}>
        <Button label={t('contactsimporter.app.backtolist')} color={ButtonColor.Secondary} onClick={onBackToList} />
        {hasReports && (
          <Button
            label={t('contactsimporter.results.deletefiles')}
            color={ButtonColor.Secondary}
            destructive={true}
            dataTestId={testId(TestIds.deleteFiles)}
            onClick={() => setDeleteOpen(true)}
          />
        )}
      </Inline>

      <Dialog
        isOpen={deleteOpen}
        headline={t('contactsimporter.results.deleteheadline')}
        headerCloseButton={{ tooltipText: t('contactsimporter.leave.close') }}
        isDismissable={!deleting}
        actionInProgress={deleting}
        onClose={() => setDeleteOpen(false)}
        confirmAction={{ label: t('contactsimporter.results.deleteconfirm'), destructive: true, onClick: () => void deleteFiles() }}
        cancelAction={{ label: t('contactsimporter.leave.cancel'), onClick: () => setDeleteOpen(false) }}
      >
        <Text>{t('contactsimporter.results.deletemessage')}</Text>
      </Dialog>
    </Stack>
  );
};
