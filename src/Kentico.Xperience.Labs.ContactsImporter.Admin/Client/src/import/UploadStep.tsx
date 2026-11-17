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
  NotificationBarAlert,
  NotificationBarInfo,
  NotificationBarWarning,
  ProgressBar,
  Row,
  Spacing,
  Stack,
} from '@kentico/xperience-admin-components';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useBlocker } from 'react-router';

import type { PipelineResult, RejectReason, TargetField } from '../import-core';
import { Commands } from '../shared/commands';
import type { ExecuteCommand, ImportActionResponse, ImportJobResponse, JobDto, LimitsDto, StatusDto } from '../shared/commands';
import { useIsMounted } from '../shared/hooks';
import { useText } from '../shared/i18n';
import { downloadTextFile, rejectReasonKey, rejectedRowsCsv, reportFileName, suspectRowsCsv } from '../shared/reports';
import { TestIds, Text, testId } from '../shared/ui';
import { RateEstimator, estimateTotalRows, percent } from '../upload/progress';
import { uploadFailureMessage } from '../upload/upload-messages';
import { UploadSessionError, startUploadSession } from '../upload/upload-session';
import type { UploadProgress, UploadSession } from '../upload/upload-session';
import type { ImportWorkerClient } from '../worker/client';

import { chunkSizes, toOpenOptions } from './model';
import type { SourceSettings } from './model';

type Phase = 'idle' | 'running' | 'paused' | 'failed' | 'done';

export interface UploadStepProps {
  readonly worker: ImportWorkerClient;
  readonly execute: ExecuteCommand;
  readonly limits: LimitsDto;
  readonly targetFields: readonly TargetField[];
  readonly job: JobDto;
  readonly status: StatusDto;
  /** The source file; `null` after a reload until the user selects it again. */
  readonly file: File | null;
  readonly settings: SourceSettings;
  /** Start uploading as soon as the step shows (after "Save and upload" / "Continue upload"). */
  readonly autoStart: boolean;
  readonly onStatus: (status: StatusDto) => void;
  readonly onBackToMap: () => void;
  /** The file is needed (continue the upload, or change the mapping after a reload): go to the File step. */
  readonly onSelectFile: () => void;
  readonly onNext: () => void;
}

/**
 * Upload step: full pass over the file in the worker (parse → map → chunk) with parallel chunk
 * uploads, progress, pause / continue, the client-side reports and "Cancel import".
 */
export const UploadStep = ({
  worker,
  execute,
  limits,
  targetFields,
  job,
  status,
  file,
  settings,
  autoStart,
  onStatus,
  onBackToMap,
  onSelectFile,
  onNext,
}: UploadStepProps) => {
  const { t, m, bytes, percent: formatPercent } = useText();
  const isMounted = useIsMounted();
  const sessionRef = useRef<UploadSession | null>(null);
  const estimatorRef = useRef(new RateEstimator());
  const startedAtRef = useRef(0);
  const startingRef = useRef(false);
  const pauseRequestedRef = useRef(false);
  const [phase, setPhase] = useState<Phase>(status.state === 'Uploaded' ? 'done' : 'idle');
  const [progress, setProgress] = useState<UploadProgress | null>(null);
  const [result, setResult] = useState<PipelineResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [elapsedSeconds, setElapsedSeconds] = useState<number | null>(null);
  const [cancelOpen, setCancelOpen] = useState(false);
  const [cancelling, setCancelling] = useState(false);

  const start = useCallback(async () => {
    if (!file || sessionRef.current || startingRef.current) {
      return;
    }

    startingRef.current = true;
    estimatorRef.current.reset();
    startedAtRef.current = Date.now();
    setError(null);
    setResult(null);
    setProgress(null);
    setElapsedSeconds(null);
    setPhase('running');

    // Ask what the server has now: chunks stored before a pause in this visit are not in the props.
    const current = await execute<ImportJobResponse>(Commands.GetJob);
    startingRef.current = false;
    if (!isMounted()) {
      return;
    }
    if (pauseRequestedRef.current) {
      pauseRequestedRef.current = false;
      setPhase('paused');

      return;
    }

    const currentStatus = current?.status ?? status;
    if (currentStatus.state !== 'Uploading') {
      // Finished or changed elsewhere (another tab or user): show what the server says.
      setPhase(currentStatus.state === 'Uploaded' ? 'done' : 'idle');
      onStatus(currentStatus);

      return;
    }

    const session = startUploadSession({
      worker,
      execute,
      file,
      source: toOpenOptions(settings),
      pipeline: {
        jobId: job.jobId,
        keyField: job.keyField,
        mapping: job.mapping,
        dateFormat: job.dateFormat,
        consentMapping: job.consentMapping ?? null,
        fields: [...targetFields],
        ...chunkSizes(limits),
      },
      parallel: limits.parallelUploads,
      storedChunks: currentStatus.chunks,
      onProgress: (snapshot) => {
        if (sessionRef.current !== session || !isMounted()) {
          return;
        }

        estimatorRef.current.add(Date.now(), snapshot.pipeline.bytesRead);
        setProgress(snapshot);
      },
    });
    sessionRef.current = session;

    session.done.then(
      (outcome) => {
        if (sessionRef.current !== session) {
          return;
        }

        sessionRef.current = null;
        if (!isMounted()) {
          return;
        }

        setResult(outcome.result);
        setElapsedSeconds((Date.now() - startedAtRef.current) / 1000);
        setPhase('done');
        onStatus(outcome.status);
      },
      (reason: unknown) => {
        if (sessionRef.current !== session) {
          return;
        }

        sessionRef.current = null;
        if (!isMounted()) {
          return;
        }

        const failure = reason instanceof UploadSessionError ? reason : new UploadSessionError('worker', String(reason));
        if (failure.result) {
          setResult(failure.result);
        }
        if (failure.kind === 'aborted') {
          setPhase('paused');
        } else {
          setError(m(uploadFailureMessage(failure)));
          setPhase('failed');
        }
      },
    );
  }, [execute, file, isMounted, job, limits, m, onStatus, settings, status, targetFields, worker]);

  const pause = useCallback(() => {
    if (startingRef.current) {
      pauseRequestedRef.current = true;
    }
    sessionRef.current?.abort();
  }, []);

  // Start once when the step opens with a file.
  const autoStarted = useRef(false);
  useEffect(() => {
    if (autoStart && !autoStarted.current && file && status.state !== 'Uploaded') {
      autoStarted.current = true;
      void start();
    }
  }, [autoStart, file, start, status.state]);

  // Leaving the step stops the pass; stored chunks stay for a resume.
  useEffect(
    () => () => {
      sessionRef.current?.abort();
      sessionRef.current = null;
    },
    [],
  );

  const running = phase === 'running';
  const pipeline = progress?.pipeline;
  const completed = pipeline ? percent(pipeline.bytesRead, pipeline.totalBytes) : 0;

  // Show progress in the tab title, so it is visible from other tabs.
  useEffect(() => {
    if (!running) {
      return undefined;
    }

    const original = document.title;
    document.title = t('contactsimporter.run.title', { percent: formatPercent(completed), fileName: job.file.name });

    return () => {
      document.title = original;
    };
  }, [running, completed, formatPercent, job.file.name, t]);

  // Warn before the tab closes or reloads mid-upload.
  useEffect(() => {
    if (!running) {
      return undefined;
    }

    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = true;
    };
    window.addEventListener('beforeunload', onBeforeUnload);

    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [running]);

  // Ask before navigating inside the administration mid-upload.
  const blocker = useBlocker(running);

  const cancelImport = useCallback(async () => {
    setCancelling(true);
    sessionRef.current?.abort();
    const response = await execute<ImportActionResponse>(Commands.CancelImport);
    if (!isMounted()) {
      return;
    }

    setCancelling(false);
    setCancelOpen(false);
    if (response?.code === 'ok' && response.status) {
      onStatus(response.status);
    } else {
      setError(response?.message ?? t('contactsimporter.upload.cancelfailed'));
      setPhase('failed');
    }
  }, [execute, isMounted, onStatus, t]);

  const profile = result?.profile ?? pipeline?.profile ?? (phase === 'done' ? status.profile : undefined);
  const fromEarlierVisit = phase === 'done' && !result;

  return (
    <Stack spacing={Spacing.L}>
      <Headline size={HeadlineSize.M}>{phase === 'done' ? t('contactsimporter.upload.doneheadline') : t('contactsimporter.upload.headline')}</Headline>

      <Text data-testid={TestIds.uploadStatus} data-upload-phase={phase} data-job-state={status.state}>
        {phase === 'running' && (progress?.phase === 'completing' ? t('contactsimporter.upload.completing') : t('contactsimporter.upload.working'))}
        {phase === 'paused' && t('contactsimporter.upload.paused')}
        {phase === 'idle' && (file ? t('contactsimporter.upload.paused') : t('contactsimporter.upload.selectfile'))}
        {phase === 'done' &&
          t(fromEarlierVisit ? 'contactsimporter.upload.donetextprevious' : 'contactsimporter.upload.donetext', { amount: status.profile.rowsValid })}
      </Text>

      {running && (
        <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk}>
          {t('contactsimporter.upload.keepopen')}
        </Callout>
      )}

      {pipeline && phase !== 'done' && (
        <Stack spacing={Spacing.S}>
          <ProgressBar completed={completed} />
          <Text>
            {t('contactsimporter.upload.progress', { read: bytes(pipeline.bytesRead), total: bytes(pipeline.totalBytes), percent: formatPercent(completed) })}
          </Text>
        </Stack>
      )}

      {progress?.retry && running && (
        <NotificationBarWarning>
          {t('contactsimporter.upload.retrying', {
            part: progress.retry.index + 1,
            seconds: Math.ceil(progress.retry.delayMs / 1000),
            attempt: progress.retry.attempt + 1,
            maxAttempts: progress.retry.maxAttempts,
          })}
        </NotificationBarWarning>
      )}
      {progress?.truncatedFrom !== null && progress?.truncatedFrom !== undefined && <NotificationBarInfo>{t('contactsimporter.upload.truncated')}</NotificationBarInfo>}
      {progress && progress.stats.skippedChunks > 0 && (
        <NotificationBarInfo>{t('contactsimporter.upload.resumed', { count: progress.stats.skippedChunks, amount: progress.stats.skippedChunks })}</NotificationBarInfo>
      )}
      {error && <NotificationBarAlert>{error}</NotificationBarAlert>}

      {profile && (
        <Cards
          profile={profile}
          progress={progress}
          estimator={estimatorRef.current}
          done={phase === 'done'}
          elapsedSeconds={elapsedSeconds}
        />
      )}

      {profile && profile.likelyDuplicates > 0 && (
        <Callout
          type={CalloutType.FriendlyWarning}
          placement={CalloutPlacementType.OnDesk}
          headline={t('contactsimporter.upload.duplicatesheadline', { amount: profile.likelyDuplicates })}
          actionButton={
            result && result.suspectRows.length > 0 ? (
              <Button
                label={t('contactsimporter.upload.downloadduplicates')}
                color={ButtonColor.Secondary}
                dataTestId={testId(TestIds.downloadDuplicates)}
                onClick={() =>
                  downloadTextFile(reportFileName(job.file.name, 'likely-duplicates'), suspectRowsCsv(result.suspectRows))
                }
              />
            ) : undefined
          }
        >
          {t('contactsimporter.upload.duplicatestext')}
          {result?.suspectRowsTruncated ? ` ${t('contactsimporter.upload.listtruncated', { amount: result.suspectRows.length })}` : ''}
        </Callout>
      )}

      {result && result.profile.rowsRejected > 0 && (
        <Callout
          type={CalloutType.FriendlyWarning}
          placement={CalloutPlacementType.OnDesk}
          headline={t('contactsimporter.upload.rejectedheadline', { amount: result.profile.rowsRejected })}
          actionButton={
            <Button
              label={t('contactsimporter.upload.downloadrejected')}
              color={ButtonColor.Secondary}
              dataTestId={testId(TestIds.downloadRejected)}
              onClick={() =>
                downloadTextFile(
                  reportFileName(job.file.name, 'skipped-rows'),
                  rejectedRowsCsv(result.rejectedRows, (reason) => t(rejectReasonKey(reason))),
                )
              }
            />
          }
        >
          <RejectReasons result={result} />
          {t('contactsimporter.upload.rejectedtext')}
          {result.rejectedRowsTruncated ? ` ${t('contactsimporter.upload.listtruncated', { amount: result.rejectedRows.length })}` : ''}
        </Callout>
      )}

      <Inline spacing={Spacing.M}>
        {phase === 'done' && (
            <Button label={t('contactsimporter.upload.next')} color={ButtonColor.Primary} dataTestId={testId(TestIds.uploadNext)} onClick={onNext} />
        )}
        {running && (
            <Button label={t('contactsimporter.upload.pause')} color={ButtonColor.Secondary} dataTestId={testId(TestIds.uploadPause)} onClick={pause} />
        )}
        {(phase === 'paused' || phase === 'failed' || phase === 'idle') && (
            <Button
              label={!file ? t('contactsimporter.file.select') : phase === 'failed' ? t('contactsimporter.upload.retry') : phase === 'paused' ? t('contactsimporter.upload.resume') : t('contactsimporter.upload.start')}
              color={ButtonColor.Primary}
              dataTestId={testId(TestIds.uploadResume)}
              onClick={file ? () => void start() : onSelectFile}
            />
        )}
        {phase !== 'running' && (
            <Button label={t('contactsimporter.upload.backtomap')} color={ButtonColor.Secondary} onClick={file ? onBackToMap : onSelectFile} />
        )}
          <Button
            label={t('contactsimporter.upload.cancelimport')}
            color={ButtonColor.Secondary}
            destructive={true}
            dataTestId={testId(TestIds.cancelImport)}
            onClick={() => setCancelOpen(true)}
          />
      </Inline>

      <Dialog
        isOpen={cancelOpen}
        headline={t('contactsimporter.upload.cancelheadline')}
        headerCloseButton={{ tooltipText: t('contactsimporter.leave.close') }}
        isDismissable={!cancelling}
        actionInProgress={cancelling}
        onClose={() => setCancelOpen(false)}
        confirmAction={{ label: t('contactsimporter.upload.cancelconfirm'), destructive: true, onClick: () => void cancelImport() }}
        cancelAction={{ label: t('contactsimporter.upload.cancelkeep'), onClick: () => setCancelOpen(false) }}
      >
        <Text>{t('contactsimporter.upload.cancelmessage')}</Text>
      </Dialog>

      <Dialog
        isOpen={blocker.state === 'blocked'}
        headline={t('contactsimporter.leave.headline')}
        headerCloseButton={{ tooltipText: t('contactsimporter.leave.close') }}
        isDismissable={true}
        onClose={() => blocker.reset?.()}
        confirmAction={{ label: t('contactsimporter.leave.leave'), onClick: () => blocker.proceed?.() }}
        cancelAction={{ label: t('contactsimporter.leave.stay'), onClick: () => blocker.reset?.() }}
      >
        <Text>{t('contactsimporter.leave.message')}</Text>
      </Dialog>
    </Stack>
  );
};

interface CardsProps {
  readonly profile: { rowsRead: number; rowsValid: number; rowsRejected: number; likelyDuplicates: number; chunkCount: number };
  readonly progress: UploadProgress | null;
  readonly estimator: RateEstimator;
  readonly done: boolean;
  readonly elapsedSeconds: number | null;
}

const Cards = ({ profile, progress, estimator, done, elapsedSeconds }: CardsProps) => {
  const { t, number, bytes, duration } = useText();
  const pipeline = progress?.pipeline;
  const totalRows = pipeline ? estimateTotalRows(profile.rowsRead, pipeline.bytesRead, pipeline.totalBytes) : profile.rowsRead;
  const wholeFileRead = done || !pipeline || pipeline.bytesRead >= pipeline.totalBytes;
  const estimate = pipeline ? estimator.estimate(pipeline.totalBytes) : { ratePerSecond: 0, etaSeconds: null };
  const stats = progress?.stats;
  const stored = stats ? stats.uploadedChunks + stats.skippedChunks : profile.chunkCount;
  const created = progress ? Math.max(progress.chunksCreated, stored) : profile.chunkCount;

  let speedDetail: string = t('contactsimporter.upload.cards.speeddetailunknown');
  if (done) {
    speedDetail = elapsedSeconds !== null ? t('contactsimporter.upload.cards.speeddetaildone', { duration: duration(elapsedSeconds) }) : '';
  } else if (estimate.etaSeconds !== null) {
    speedDetail = t('contactsimporter.upload.cards.speeddetail', { duration: duration(estimate.etaSeconds) });
  }

  return (
    <Row spacing={Spacing.L}>
      <Column cols={Cols.Col3}>
        <Count name="rows-read" value={profile.rowsRead} />
        <InfoCard
          caption={t('contactsimporter.upload.cards.rows')}
          tooltip={t('contactsimporter.upload.cards.rowstooltip')}
          text={number(profile.rowsRead)}
          details={wholeFileRead ? t('contactsimporter.upload.cards.rowsdetailexact') : t('contactsimporter.upload.cards.rowsdetail', { total: totalRows })}
        />
      </Column>
      <Column cols={Cols.Col3}>
        <Count name="rows-uploaded" value={profile.rowsValid} />
        <InfoCard
          caption={t('contactsimporter.upload.cards.chunks')}
          tooltip={t('contactsimporter.upload.cards.chunkstooltip')}
          text={number(profile.rowsValid)}
          details={
            stats
              ? t('contactsimporter.upload.cards.chunksdetail', { stored, created, sent: bytes(stats.uploadedBytes) })
              : t('contactsimporter.upload.cards.chunksdetailstored', { count: stored, stored })
          }
        />
      </Column>
      {progress && (
        <Column cols={Cols.Col2}>
          <InfoCard
            caption={t('contactsimporter.upload.cards.speed')}
            tooltip={t('contactsimporter.upload.cards.speedtooltip')}
            text={done || estimate.ratePerSecond <= 0 ? '–' : t('contactsimporter.upload.cards.speedvalue', { rate: bytes(estimate.ratePerSecond) })}
            details={speedDetail}
          />
        </Column>
      )}
      <Column cols={Cols.Col2}>
        <Count name="rows-skipped" value={profile.rowsRejected} />
        <InfoCard
          caption={t('contactsimporter.upload.cards.rejected')}
          tooltip={t('contactsimporter.upload.cards.rejectedtooltip')}
          text={number(profile.rowsRejected)}
          details={t('contactsimporter.upload.cards.rejecteddetail', { amount: profile.rowsValid })}
        />
      </Column>
      <Column cols={Cols.Col2}>
        <Count name="likely-duplicates" value={profile.likelyDuplicates} />
        <InfoCard
          caption={t('contactsimporter.upload.cards.duplicates')}
          tooltip={t('contactsimporter.upload.cards.duplicatestooltip')}
          text={number(profile.likelyDuplicates)}
          details={t('contactsimporter.upload.cards.duplicatesdetail')}
        />
      </Column>
    </Row>
  );
};

/** Exact value of a count card for tests; the card itself shows it formatted. */
const Count = ({ name, value }: { readonly name: string; readonly value: number }) => (
  <span hidden={true} data-testid={`${TestIds.countPrefix}${name}`} data-count={value} />
);

const RejectReasons = ({ result }: { readonly result: PipelineResult }) => {
  const { t, number } = useText();
  const entries = Object.entries(result.rejectedByReason) as [RejectReason, number][];
  if (entries.length === 0) {
    return null;
  }

  return (
    <ul style={{ margin: `0 0 ${Spacing.S} 0`, paddingLeft: Spacing.XL }}>
      {entries.map(([reason, count]) => (
        <li key={reason}>
          {t(rejectReasonKey(reason))}: {number(count)}
        </li>
      ))}
    </ul>
  );
};
