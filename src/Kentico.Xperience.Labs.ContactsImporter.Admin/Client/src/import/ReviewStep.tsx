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
import React, { useCallback, useEffect, useState } from 'react';

import type { FinishReason } from '../process/process-loop';
import { Commands } from '../shared/commands';
import type { AggregatesDto, ExecuteCommand, FieldDto, ImportJobResponse } from '../shared/commands';
import { formatDateTime, formatNumber } from '../shared/format';
import { useIsMounted } from '../shared/hooks';
import { Strings, format } from '../shared/strings';
import { TestIds, Text, testId } from '../shared/ui';

import { AggregatesTable, Count, FieldChangesTable, clearedValues } from './Aggregates';
import { CancelImport } from './CancelImport';
import { CONFIRM_TYPING_THRESHOLD, progressFor } from './model';
import type { JobView } from './model';
import { ProcessPanel } from './ProcessPanel';
import { ReportButton } from './ReportButton';
import { useProcessLoop } from './useProcessLoop';

const S = Strings.review;

export interface ReviewStepProps {
  readonly execute: ExecuteCommand;
  readonly view: JobView;
  readonly fields: readonly FieldDto[];
  /** Shown above the progress (why the file is checked again). */
  readonly notice: string | null;
  readonly onBackToOptions: () => void;
  /** The job changed on the server (check finished, cancelled, changed elsewhere): load it again. */
  readonly onReload: () => void;
  /** The import is confirmed; the response holds the job as it is now. */
  readonly onConfirmed: (response: ImportJobResponse) => void;
}

/**
 * Review step: runs the check (dry run) of the whole file, then shows what the import will do and
 * asks for the confirmation.
 */
export const ReviewStep = (props: ReviewStepProps) =>
  props.view.status.state === 'Analyzed' ? <Summary {...props} /> : <Analyzing {...props} />;

const Analyzing = ({ execute, view, notice, onBackToOptions, onReload }: ReviewStepProps) => {
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
    <Stack spacing={Spacing.L}>
      <Headline size={HeadlineSize.M}>{S.headlineRunning}</Headline>
      {notice && <NotificationBarInfo>{notice}</NotificationBarInfo>}
      <ProcessPanel
        view={loop}
        runningText={S.running}
        cards={S.cards}
        titleName={view.job.file.name}
        actions={
          <>
            <Button label={S.back} color={ButtonColor.Secondary} dataTestId={testId(TestIds.reviewBack)} onClick={onBackToOptions} />
            <CancelImport
              execute={execute}
              message={Strings.upload.cancelMessage}
              keepLabel={Strings.upload.cancelKeep}
              onBeforeCancel={loop.pause}
              onCancelled={onReload}
            />
          </>
        }
      />
    </Stack>
  );
};

const Summary = ({ execute, view, fields, onBackToOptions, onReload, onConfirmed }: ReviewStepProps) => {
  const isMounted = useIsMounted();
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [typed, setTyped] = useState('');
  const [error, setError] = useState<string | null>(null);

  const { job, status, progress, reports } = view;
  // The counts of the finished check, computed by the server.
  const analysis: AggregatesDto = progress.analysis ?? progress.aggregates;
  const cleared = clearedValues(analysis);
  const mustType = analysis.contactsUpdated > CONFIRM_TYPING_THRESHOLD;
  const typedOk = !mustType || typed.trim().toUpperCase() === S.confirmWord;

  const confirm = useCallback(async () => {
    setConfirming(true);
    setError(null);
    const response = await execute<ImportJobResponse>(Commands.ConfirmImport);
    if (!isMounted()) {
      return;
    }

    setConfirming(false);
    if (response?.code === 'ok' && response.status) {
      onConfirmed(response);
    } else if (response?.code === 'analysisExpired' || response?.code === 'invalidState') {
      setConfirmOpen(false);
      onReload();
    } else {
      setError(format(S.confirmFailed, response?.message ?? ''));
    }
  }, [execute, isMounted, onConfirmed, onReload]);

  return (
    <Stack spacing={Spacing.L}>
      <Headline size={HeadlineSize.M}>{S.headlineDone}</Headline>
      <Text data-testid={TestIds.processStatus} data-process-status="finished" data-process-phase="analyze" data-job-state={status.state}>
        {S.done}
        {progress.analyzedUtc ? ` ${format(S.analyzedAt, formatDateTime(progress.analyzedUtc))}` : ''}
      </Text>

      <Row spacing={Spacing.L}>
        <Column cols={Cols.Col3}>
          <Count name="review-created" value={analysis.created} />
          <InfoCard caption={S.cards.created} tooltip={S.cards.createdTooltip} text={formatNumber(analysis.created)} details={S.cards.createdDetail} />
        </Column>
        <Column cols={Cols.Col3}>
          <Count name="review-updated" value={analysis.contactsUpdated} />
          <InfoCard caption={S.cards.updated} tooltip={S.cards.updatedTooltip} text={formatNumber(analysis.contactsUpdated)} details={S.cards.updatedDetail} />
        </Column>
        <Column cols={Cols.Col3}>
          <Count name="review-unchanged" value={analysis.unchanged} />
          <InfoCard caption={S.cards.unchanged} tooltip={S.cards.unchangedTooltip} text={formatNumber(analysis.unchanged)} details={S.cards.unchangedDetail} />
        </Column>
        <Column cols={Cols.Col3}>
          <Count name="review-skipped" value={analysis.skipped} />
          <InfoCard caption={S.cards.skipped} tooltip={S.cards.skippedTooltip} text={formatNumber(analysis.skipped)} details={S.cards.skippedDetail} />
        </Column>
      </Row>

      {cleared > 0 && (
        <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk} headline={format(S.clearedHeadline, formatNumber(cleared))}>
          {S.clearedText}
        </Callout>
      )}
      {(analysis.duplicateRows > 0 || status.profile.likelyDuplicates > 0) && (
        <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk}>
          {format(S.duplicatesText, formatNumber(status.profile.likelyDuplicates))}
        </Callout>
      )}

      <Headline size={HeadlineSize.S}>{S.detailsHeadline}</Headline>
      <AggregatesTable expected={analysis} actual={null} />

      {analysis.fields.length > 0 && (
        <>
          <Headline size={HeadlineSize.S}>{S.fieldsHeadline}</Headline>
          <FieldChangesTable aggregates={analysis} fields={fields} />
        </>
      )}

      {reports.analyze && (
        <ReportButton execute={execute} phase="analyze" label={S.downloadReport} hint={reports.includesKey ? S.reportHintKey : S.reportHint} />
      )}

      <Inline spacing={Spacing.M}>
        <Button label={S.back} color={ButtonColor.Secondary} dataTestId={testId(TestIds.reviewBack)} onClick={onBackToOptions} />
        <Button
          label={S.run}
          color={ButtonColor.Primary}
          dataTestId={testId(TestIds.reviewRun)}
          onClick={() => {
            setTyped('');
            setError(null);
            setConfirmOpen(true);
          }}
        />
        <CancelImport execute={execute} message={Strings.upload.cancelMessage} keepLabel={Strings.upload.cancelKeep} onCancelled={onReload} />
      </Inline>

      <Dialog
        isOpen={confirmOpen}
        headline={S.confirmHeadline}
        headerCloseButton={{ tooltipText: Strings.leave.close }}
        isDismissable={!confirming}
        actionInProgress={confirming}
        onClose={() => setConfirmOpen(false)}
        confirmAction={{
          label: S.confirmRun,
          disabled: !typedOk,
          onClick: () => void confirm(),
        }}
        cancelAction={{ label: S.confirmCancel, onClick: () => setConfirmOpen(false) }}
      >
        <Stack spacing={Spacing.M}>
          {error && <NotificationBarAlert>{error}</NotificationBarAlert>}
          <Text strong={true}>
            {format(S.confirmText, formatNumber(analysis.created), formatNumber(analysis.contactsUpdated), formatNumber(analysis.skipped))}
          </Text>
          <Text>{format(Strings.steps.fileLine, job.file.name)}</Text>
          <Text>{S.confirmNoUndo}</Text>
          {mustType && (
            <Input
              label={S.confirmTypeLabel}
              explanationText={format(S.confirmType, S.confirmWord)}
              value={typed}
              dataTestId={testId(TestIds.confirmInput)}
              onChange={(event) => setTyped(event.target.value)}
            />
          )}
        </Stack>
      </Dialog>
    </Stack>
  );
};
