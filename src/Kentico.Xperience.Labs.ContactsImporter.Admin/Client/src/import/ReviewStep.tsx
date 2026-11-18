import {
  Button,
  ButtonColor,
  Callout,
  CalloutPlacementType,
  CalloutType,
  Cols,
  Column,
  Dialog,
  Headline,
  HeadlineSize,
  InfoCard,
  Inline,
  Input,
  NotificationBarAlert,
  NotificationBarInfo,
  Row,
  Spacing,
  Stack,
} from '@kentico/xperience-admin-components';
import React, { useCallback, useEffect, useMemo, useState } from 'react';

import type { FinishReason } from '../process/process-loop';
import { Commands } from '../shared/commands';
import type { AggregatesDto, ConfirmImportRequest, ExecuteCommand, FieldDto, ImportJobResponse } from '../shared/commands';
import { useIsMounted } from '../shared/hooks';
import { useText } from '../shared/i18n';
import { DelayedReveal, DIALOG_MAX_WIDTH, TestIds, Text, testId } from '../shared/ui';

import { AggregatesTable, Count, FieldChangesTable, bouncedContacts, clearedValues } from './Aggregates';
import { ConsentSummary } from './ConsentSummary';
import { MappingSummaryCard } from './MappingSummary';
import { CONFIRM_TYPING_THRESHOLD, progressFor } from './model';
import type { JobView } from './model';
import { OtherImportCallout, useOtherRuns } from './OtherImportRunning';
import { ProcessPanel } from './ProcessPanel';
import type { CountCardTexts } from './ProcessPanel';
import { ReportButton } from './ReportButton';
import { isCheckOlderThanOtherRun } from './running-import';
import { useProcessLoop } from './useProcessLoop';
import { ValueSamplesCard } from './ValueSamples';

export interface ReviewStepProps {
  readonly execute: ExecuteCommand;
  readonly view: JobView;
  readonly fields: readonly FieldDto[];
  /** The user has the permission to confirm consent (UI hint; the server checks it when the import is confirmed). */
  readonly canConfirmConsent: boolean;
  /** Shown above the progress (why the file is checked again). */
  readonly notice: string | null;
  readonly onBackToOptions: () => void;
  /** "Cancel import" and "Start a new import" (both delete this import); `onBeforeCancel` stops the check first. */
  readonly renderActions?: (onBeforeCancel?: () => void) => React.ReactNode;
  /** The job changed on the server (check finished, cancelled, changed elsewhere): load it again. */
  readonly onReload: () => void;
  /** The import is confirmed; the response holds the job as it is now. */
  readonly onConfirmed: (response: ImportJobResponse) => void;
  /** Opens another import (the one whose unfinished Run keeps this one from running). */
  readonly onOpenImport: (jobId: string) => void;
}

/**
 * Review step: runs the check (dry run) of the whole file, then shows what the import will do and
 * asks for the confirmation.
 */
export const ReviewStep = (props: ReviewStepProps) =>
  props.view.status.state === 'Analyzed' ? <Summary {...props} /> : <Analyzing {...props} />;

const Analyzing = ({ execute, view, notice, onBackToOptions, renderActions, onReload }: ReviewStepProps) => {
  const { t } = useText();
  const cards = useMemo<CountCardTexts>(
    () => ({
      created: t('contactsimporter.review.cards.created'),
      createdDetail: t('contactsimporter.review.cards.createddetail'),
      createdTooltip: t('contactsimporter.review.cards.createdtooltip'),
      updated: t('contactsimporter.review.cards.updated'),
      updatedDetail: t('contactsimporter.review.cards.updateddetail'),
      updatedTooltip: t('contactsimporter.review.cards.updatedtooltip'),
      unchanged: t('contactsimporter.review.cards.unchanged'),
      unchangedDetail: t('contactsimporter.review.cards.unchangeddetail'),
      unchangedTooltip: t('contactsimporter.review.cards.unchangedtooltip'),
      skipped: t('contactsimporter.review.cards.skipped'),
      skippedDetail: t('contactsimporter.review.cards.skippeddetail'),
      skippedTooltip: t('contactsimporter.review.cards.skippedtooltip'),
    }),
    [t],
  );
  const loop = useProcessLoop({
    jobId: view.job.jobId,
    phase: 'analyze',
    execute,
    initialProgress: progressFor('analyze', view.progress),
    autoStart: true,
  });
  const finished: FinishReason | null = loop.snapshot.status === 'finished' ? loop.snapshot.finishReason : null;

  useEffect(() => {
    if (finished !== null) {
      onReload();
    }
  }, [finished, onReload]);

  return (
    <DelayedReveal>
      <Stack spacing={Spacing.L}>
        <Headline size={HeadlineSize.M}>{t('contactsimporter.review.headlinerunning')}</Headline>
        {notice && <NotificationBarInfo>{notice}</NotificationBarInfo>}
        <ProcessPanel
          view={loop}
          runningText={t('contactsimporter.review.running')}
          cards={cards}
          titleName={view.job.file.name}
          usesList={view.job.options.recipientListId !== null}
          usesConsent={view.job.options.consent !== 'none'}
          actions={[
            <Button
              key="back"
              label={t('contactsimporter.review.back')}
              color={ButtonColor.Secondary}
              dataTestId={testId(TestIds.reviewBack)}
              onClick={onBackToOptions}
            />,
            renderActions?.(loop.pause),
          ]}
        />
      </Stack>
    </DelayedReveal>
  );
};

const Summary = ({ execute, view, fields, canConfirmConsent, onBackToOptions, renderActions, onReload, onConfirmed, onOpenImport }: ReviewStepProps) => {
  const { t, number, dateTime } = useText();
  const isMounted = useIsMounted();
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [typed, setTyped] = useState('');
  const [error, setError] = useState<string | null>(null);
  // Another import whose Run was confirmed and has not ended (PLAN D60): Run waits for it. Checked while this view is shown.
  const otherRuns = useOtherRuns(execute, true);
  const otherRunning = otherRuns.blocker;
  const refreshOtherRuns = otherRuns.refresh;

  const { job, status, progress, reports } = view;
  // The counts of the finished check, computed by the server.
  const analysis: AggregatesDto = progress.analysis ?? progress.aggregates;
  const cleared = clearedValues(analysis);
  const mustType = analysis.contactsUpdated > CONFIRM_TYPING_THRESHOLD;
  const confirmWord = t('contactsimporter.review.confirmword');
  const typedOk = !mustType || typed.trim().toUpperCase() === confirmWord.toUpperCase();
  const usesList = job.options.recipientListId !== null;
  const usesConsent = job.options.consent !== 'none';
  // Decided by the server from the saved options; the statement and its version come from there too.
  const mustAttest = job.attestationRequired;
  const blockedByPermission = mustAttest && !canConfirmConsent;
  const bounced = bouncedContacts(analysis);

  const confirm = useCallback(async () => {
    setConfirming(true);
    setError(null);
    const response = await execute<ImportJobResponse, ConfirmImportRequest>(Commands.ConfirmImport, {
      statementVersion: mustAttest ? job.attestationVersion : null,
    });
    if (!isMounted()) {
      return;
    }

    setConfirming(false);
    if (response?.code === 'ok' && response.status) {
      onConfirmed(response);
    } else if (response?.code === 'analysisExpired' || response?.code === 'invalidState') {
      setConfirmOpen(false);
      onReload();
    } else if (response?.code === 'attestationRequired') {
      // The options or the statement changed since this page loaded them: show the current ones.
      setConfirmOpen(false);
      setError(t('contactsimporter.review.confirmstatementchanged'));
      onReload();
    } else if (response?.code === 'otherImportRunning') {
      // Another import started after the last check: show it and keep Run disabled until it ends.
      setError(response.message ?? t('contactsimporter.review.otherrunning.headline'));
      refreshOtherRuns();
    } else if (response?.code === 'consentPermissionRequired') {
      setError(response.message ?? t('contactsimporter.review.confirmnopermission'));
    } else {
      setError(t('contactsimporter.review.confirmfailed', { reason: response?.message ?? '' }));
    }
  }, [execute, isMounted, job.attestationVersion, mustAttest, onConfirmed, onReload, refreshOtherRuns, t]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: Spacing.L }}>
      <Headline size={HeadlineSize.M}>{t('contactsimporter.review.headlinedone')}</Headline>
      <Text data-testid={TestIds.processStatus} data-process-status="finished" data-process-phase="analyze" data-job-state={status.state}>
        {t('contactsimporter.review.done')}
        {progress.analyzedUtc ? ` ${t('contactsimporter.review.analyzedat', { time: dateTime(progress.analyzedUtc) })}` : ''}
      </Text>
      {otherRuns.lastRunEndedUtc && isCheckOlderThanOtherRun(progress.analyzedUtc, otherRuns.lastRunEndedUtc) && (
        <div data-testid={TestIds.checkOlderThanOtherRun} data-last-run-ended={otherRuns.lastRunEndedUtc}>
          <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk}>
            {t('contactsimporter.review.olderthanotherrun', { time: dateTime(otherRuns.lastRunEndedUtc) })}
          </Callout>
        </div>
      )}

      <Row spacing={Spacing.L}>
        <Column cols={Cols.Col3}>
          <Count name="review-created" value={analysis.created} />
          <InfoCard
            caption={t('contactsimporter.review.cards.created')}
            tooltip={t('contactsimporter.review.cards.createdtooltip')}
            text={number(analysis.created)}
            details={t('contactsimporter.review.cards.createddetail')}
          />
        </Column>
        <Column cols={Cols.Col3}>
          <Count name="review-updated" value={analysis.contactsUpdated} />
          <InfoCard
            caption={t('contactsimporter.review.cards.updated')}
            tooltip={t('contactsimporter.review.cards.updatedtooltip')}
            text={number(analysis.contactsUpdated)}
            details={t('contactsimporter.review.cards.updateddetail')}
          />
        </Column>
        <Column cols={Cols.Col3}>
          <Count name="review-unchanged" value={analysis.unchanged} />
          <InfoCard
            caption={t('contactsimporter.review.cards.unchanged')}
            tooltip={t('contactsimporter.review.cards.unchangedtooltip')}
            text={number(analysis.unchanged)}
            details={t('contactsimporter.review.cards.unchangeddetail')}
          />
        </Column>
        <Column cols={Cols.Col3}>
          <Count name="review-skipped" value={analysis.skipped} />
          <InfoCard
            caption={t('contactsimporter.review.cards.skipped')}
            tooltip={t('contactsimporter.review.cards.skippedtooltip')}
            text={number(analysis.skipped)}
            details={t('contactsimporter.review.cards.skippeddetail')}
          />
        </Column>
      </Row>

      {cleared > 0 && (
        <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk} headline={t('contactsimporter.review.clearedheadline', { amount: cleared })}>
          {t('contactsimporter.review.clearedtext')}
        </Callout>
      )}
      {(analysis.duplicateRows > 0 || status.profile.likelyDuplicates > 0) && (
        <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk}>
          {t('contactsimporter.review.duplicatestext', { amount: status.profile.likelyDuplicates })}
        </Callout>
      )}

      <MappingSummaryCard job={job} fields={fields} />

      <ConsentSummary job={job} />
      {usesList && bounced > 0 && (
        <Callout
          type={CalloutType.QuickTip}
          placement={CalloutPlacementType.OnDesk}
          headline={t('contactsimporter.consentsummary.bouncedheadline', { amount: bounced })}
        >
          {t('contactsimporter.consentsummary.bouncedtext')}
        </Callout>
      )}

      <Headline size={HeadlineSize.S}>{t('contactsimporter.review.detailsheadline')}</Headline>
      <AggregatesTable expected={analysis} actual={null} usesList={usesList} usesConsent={usesConsent} />

      {analysis.fields.length > 0 && (
        <>
          <Headline size={HeadlineSize.S}>{t('contactsimporter.review.fieldsheadline')}</Headline>
          <FieldChangesTable aggregates={analysis} fields={fields} />
        </>
      )}

      {analysis.fields.length > 0 && <ValueSamplesCard execute={execute} fields={fields} analyzedUtc={progress.analyzedUtc} />}

      {reports.analyze && (
        <ReportButton
          execute={execute}
          phase="analyze"
          label={t('contactsimporter.review.downloadreport')}
          hint={reports.includesKey ? t('contactsimporter.review.reporthintkey') : t('contactsimporter.review.reporthint')}
        />
      )}

      {otherRunning && <OtherImportCallout running={otherRunning} onOpenImport={onOpenImport} />}

      <Inline spacing={Spacing.M}>
        <Button label={t('contactsimporter.review.back')} color={ButtonColor.Secondary} dataTestId={testId(TestIds.reviewBack)} onClick={onBackToOptions} />
        <Button
          label={t('contactsimporter.review.run')}
          color={ButtonColor.Primary}
          disabled={otherRunning !== null}
          dataTestId={testId(TestIds.reviewRun)}
          onClick={() => {
            setTyped('');
            setError(null);
            setConfirmOpen(true);
          }}
        />
        {otherRunning && <Text>{t('contactsimporter.review.otherrunning.reason')}</Text>}
        {renderActions?.()}
      </Inline>

      <Dialog
        maxWidth={DIALOG_MAX_WIDTH}
        isOpen={confirmOpen}
        headline={t('contactsimporter.review.confirmheadline')}
        headerCloseButton={{ tooltipText: t('contactsimporter.leave.close') }}
        isDismissable={!confirming}
        actionInProgress={confirming}
        onClose={() => setConfirmOpen(false)}
        confirmAction={{
          label: t('contactsimporter.review.confirmrun'),
          disabled: !typedOk || blockedByPermission || otherRunning !== null,
          onClick: () => void confirm(),
        }}
        cancelAction={{ label: t('contactsimporter.review.confirmcancel'), onClick: () => setConfirmOpen(false) }}
      >
        <Stack spacing={Spacing.M}>
          {error && <NotificationBarAlert>{error}</NotificationBarAlert>}
          <Text strong={true}>
            {t('contactsimporter.review.confirmtext', { created: analysis.created, updated: analysis.contactsUpdated, skipped: analysis.skipped })}
          </Text>
          <Text>{t('contactsimporter.steps.fileline', { fileName: job.file.name })}</Text>
          <Text>{t('contactsimporter.review.confirmnoundo')}</Text>
          {mustAttest && (
            <>
              {usesList && (
                <Text>{t('contactsimporter.review.confirmlistadded', { list: job.recipientListName ?? '', amount: analysis.listAdded ?? 0 })}</Text>
              )}
              {usesList && job.options.unsubscribed === 'resubscribe' && (
                <Text>{t('contactsimporter.review.confirmlistresubscribed', { amount: analysis.listResubscribed ?? 0 })}</Text>
              )}
              {usesConsent && (
                <Text>
                  {t('contactsimporter.review.confirmconsentrecorded', {
                    consent: job.consentName ?? '',
                    amount: (analysis.consentRecorded ?? 0) + (analysis.consentReAgreed ?? 0),
                  })}
                </Text>
              )}
              {usesConsent && job.options.revokedConsent === 'reAgree' && (
                <Text>{t('contactsimporter.review.confirmconsentreagreed', { amount: analysis.consentReAgreed ?? 0 })}</Text>
              )}
              {blockedByPermission ? (
                <NotificationBarAlert dataTestId={testId(TestIds.attestationNoPermission)}>{t('contactsimporter.review.confirmnopermission')}</NotificationBarAlert>
              ) : (
                <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnPaper} headline={t('contactsimporter.review.confirmstatementheadline')}>
                  <div data-testid={TestIds.attestationStatement} data-statement-version={job.attestationVersion}>
                    {job.attestationStatement}
                  </div>
                  <div style={{ marginTop: Spacing.S }}>{t('contactsimporter.review.confirmstatementhint')}</div>
                </Callout>
              )}
            </>
          )}
          {mustType && (
            <Input
              label={t('contactsimporter.review.confirmtypelabel')}
              explanationText={t('contactsimporter.review.confirmtype', { word: confirmWord })}
              value={typed}
              dataTestId={testId(TestIds.confirmInput)}
              onChange={(event) => setTyped(event.target.value)}
            />
          )}
        </Stack>
      </Dialog>
    </div>
  );
};
