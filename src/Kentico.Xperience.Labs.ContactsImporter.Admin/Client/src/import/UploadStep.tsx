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

import type { PipelineResult, TargetField } from '../import-core';
import { Commands } from '../shared/commands';
import type { ExecuteCommand, ImportActionResponse, ImportJobResponse, JobDto, LimitsDto, StatusDto } from '../shared/commands';
import { formatBytes, formatDuration, formatNumber, formatPercent } from '../shared/format';
import { useIsMounted } from '../shared/hooks';
import { downloadTextFile, rejectedRowsCsv, reportFileName, suspectRowsCsv } from '../shared/reports';
import { Strings, format } from '../shared/strings';
import { TestIds, Text, testId } from '../shared/ui';
import { RateEstimator, estimateTotalRows, percent } from '../upload/progress';
import { UploadSessionError, startUploadSession } from '../upload/upload-session';
import type { UploadProgress, UploadSession } from '../upload/upload-session';
import type { ImportWorkerClient } from '../worker/client';

import { chunkSizes, toOpenOptions } from './model';
import type { SourceSettings } from './model';

const S = Strings.upload;

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
          setError(describeFailure(failure));
          setPhase('failed');
        }
      },
    );
  }, [execute, file, isMounted, job, limits, onStatus, settings, status, targetFields, worker]);

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
    document.title = `${formatPercent(completed)} % · ${job.file.name}`;

    return () => {
      document.title = original;
    };
  }, [running, completed, job.file.name]);

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
      setError(response?.message ?? S.cancelFailed);
      setPhase('failed');
    }
  }, [execute, isMounted, onStatus]);

  const profile = result?.profile ?? pipeline?.profile ?? (phase === 'done' ? status.profile : undefined);
  const fromEarlierVisit = phase === 'done' && !result;

  return (
    <Stack spacing={Spacing.L}>
      <Headline size={HeadlineSize.M}>{phase === 'done' ? S.doneHeadline : S.headline}</Headline>

      <Text data-testid={TestIds.uploadStatus} data-upload-phase={phase} data-job-state={status.state}>
        {phase === 'running' && (progress?.phase === 'completing' ? S.completing : S.working)}
        {phase === 'paused' && S.paused}
        {phase === 'idle' && (file ? S.paused : S.selectFile)}
        {phase === 'done' && format(fromEarlierVisit ? S.doneTextPrevious : S.doneText, formatNumber(status.profile.rowsValid))}
      </Text>

      {running && (
        <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk}>
          {S.keepOpen}
        </Callout>
      )}

      {pipeline && phase !== 'done' && (
        <Stack spacing={Spacing.S}>
          <ProgressBar completed={completed} />
          <Text>
            {format(S.progress, formatBytes(pipeline.bytesRead), formatBytes(pipeline.totalBytes), formatPercent(completed))}
          </Text>
        </Stack>
      )}

      {progress?.retry && running && (
        <NotificationBarWarning>
          {format(
            S.retrying,
            progress.retry.index + 1,
            Math.ceil(progress.retry.delayMs / 1000),
            progress.retry.attempt + 1,
            progress.retry.maxAttempts,
          )}
        </NotificationBarWarning>
      )}
      {progress?.truncatedFrom !== null && progress?.truncatedFrom !== undefined && <NotificationBarInfo>{S.truncated}</NotificationBarInfo>}
      {progress && progress.stats.skippedChunks > 0 && (
        <NotificationBarInfo>{format(S.resumed, formatNumber(progress.stats.skippedChunks))}</NotificationBarInfo>
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
          headline={format(S.duplicatesHeadline, formatNumber(profile.likelyDuplicates))}
          actionButton={
            result && result.suspectRows.length > 0 ? (
              <Button
                label={S.downloadDuplicates}
                color={ButtonColor.Secondary}
                dataTestId={testId(TestIds.downloadDuplicates)}
                onClick={() =>
                  downloadTextFile(reportFileName(job.file.name, 'likely-duplicates'), suspectRowsCsv(result.suspectRows))
                }
              />
            ) : undefined
          }
        >
          {S.duplicatesText}
          {result?.suspectRowsTruncated ? ` ${format(S.listTruncated, formatNumber(result.suspectRows.length))}` : ''}
        </Callout>
      )}

      {result && result.profile.rowsRejected > 0 && (
        <Callout
          type={CalloutType.FriendlyWarning}
          placement={CalloutPlacementType.OnDesk}
          headline={format(S.rejectedHeadline, formatNumber(result.profile.rowsRejected))}
          actionButton={
            <Button
              label={S.downloadRejected}
              color={ButtonColor.Secondary}
              dataTestId={testId(TestIds.downloadRejected)}
              onClick={() => downloadTextFile(reportFileName(job.file.name, 'skipped-rows'), rejectedRowsCsv(result.rejectedRows))}
            />
          }
        >
          <RejectReasons result={result} />
          {S.rejectedText}
          {result.rejectedRowsTruncated ? ` ${format(S.listTruncated, formatNumber(result.rejectedRows.length))}` : ''}
        </Callout>
      )}

      <Inline spacing={Spacing.M}>
        {phase === 'done' && (
            <Button label={S.next} color={ButtonColor.Primary} dataTestId={testId(TestIds.uploadNext)} onClick={onNext} />
        )}
        {running && (
            <Button label={S.pause} color={ButtonColor.Secondary} dataTestId={testId(TestIds.uploadPause)} onClick={pause} />
        )}
        {(phase === 'paused' || phase === 'failed' || phase === 'idle') && (
            <Button
              label={!file ? Strings.file.select : phase === 'failed' ? S.retry : phase === 'paused' ? S.resume : S.start}
              color={ButtonColor.Primary}
              dataTestId={testId(TestIds.uploadResume)}
              onClick={file ? () => void start() : onSelectFile}
            />
        )}
        {phase !== 'running' && (
            <Button label={S.backToMap} color={ButtonColor.Secondary} onClick={file ? onBackToMap : onSelectFile} />
        )}
          <Button
            label={S.cancelImport}
            color={ButtonColor.Secondary}
            destructive={true}
            dataTestId={testId(TestIds.cancelImport)}
            onClick={() => setCancelOpen(true)}
          />
      </Inline>

      <Dialog
        isOpen={cancelOpen}
        headline={S.cancelHeadline}
        headerCloseButton={{ tooltipText: Strings.leave.close }}
        isDismissable={!cancelling}
        actionInProgress={cancelling}
        onClose={() => setCancelOpen(false)}
        confirmAction={{ label: S.cancelConfirm, destructive: true, onClick: () => void cancelImport() }}
        cancelAction={{ label: S.cancelKeep, onClick: () => setCancelOpen(false) }}
      >
        <Text>{S.cancelMessage}</Text>
      </Dialog>

      <Dialog
        isOpen={blocker.state === 'blocked'}
        headline={Strings.leave.headline}
        headerCloseButton={{ tooltipText: Strings.leave.close }}
        isDismissable={true}
        onClose={() => blocker.reset?.()}
        confirmAction={{ label: Strings.leave.leave, onClick: () => blocker.proceed?.() }}
        cancelAction={{ label: Strings.leave.stay, onClick: () => blocker.reset?.() }}
      >
        <Text>{Strings.leave.message}</Text>
      </Dialog>
    </Stack>
  );
};

function describeFailure(failure: UploadSessionError): string {
  switch (failure.kind) {
    case 'rejected':
      if (failure.reason === 'uploadTooLarge') {
        return S.errors.tooLarge;
      }

      return failure.rowNumber !== undefined
        ? format(S.errors.rejectedRow, failure.message, failure.rowNumber)
        : format(S.errors.rejected, failure.message);
    case 'network':
      return S.errors.network;
    case 'noRows':
      return S.errors.noRows;
    case 'complete':
      return format(S.errors.complete, failure.message);
    default:
      return format(S.errors.worker, failure.message);
  }
}

interface CardsProps {
  readonly profile: { rowsRead: number; rowsValid: number; rowsRejected: number; likelyDuplicates: number; chunkCount: number };
  readonly progress: UploadProgress | null;
  readonly estimator: RateEstimator;
  readonly done: boolean;
  readonly elapsedSeconds: number | null;
}

const Cards = ({ profile, progress, estimator, done, elapsedSeconds }: CardsProps) => {
  const C = S.cards;
  const pipeline = progress?.pipeline;
  const totalRows = pipeline ? estimateTotalRows(profile.rowsRead, pipeline.bytesRead, pipeline.totalBytes) : profile.rowsRead;
  const wholeFileRead = done || !pipeline || pipeline.bytesRead >= pipeline.totalBytes;
  const estimate = pipeline ? estimator.estimate(pipeline.totalBytes) : { ratePerSecond: 0, etaSeconds: null };
  const stats = progress?.stats;
  const stored = stats ? stats.uploadedChunks + stats.skippedChunks : profile.chunkCount;
  const created = progress ? Math.max(progress.chunksCreated, stored) : profile.chunkCount;

  let speedDetail: string = C.speedDetailUnknown;
  if (done) {
    speedDetail = elapsedSeconds !== null ? format(C.speedDetailDone, formatDuration(elapsedSeconds)) : '';
  } else if (estimate.etaSeconds !== null) {
    speedDetail = format(C.speedDetail, formatDuration(estimate.etaSeconds));
  }

  return (
    <Row spacing={Spacing.L}>
      <Column cols={Cols.Col3}>
        <Count name="rows-read" value={profile.rowsRead} />
        <InfoCard
          caption={C.rows}
          tooltip={C.rowsTooltip}
          text={formatNumber(profile.rowsRead)}
          details={wholeFileRead ? C.rowsDetailExact : format(C.rowsDetail, formatNumber(totalRows))}
        />
      </Column>
      <Column cols={Cols.Col3}>
        <Count name="rows-uploaded" value={profile.rowsValid} />
        <InfoCard
          caption={C.chunks}
          tooltip={C.chunksTooltip}
          text={formatNumber(profile.rowsValid)}
          details={
            stats
              ? format(C.chunksDetail, formatNumber(stored), formatNumber(created), formatBytes(stats.uploadedBytes))
              : format(C.chunksDetailStored, formatNumber(stored))
          }
        />
      </Column>
      {progress && (
        <Column cols={Cols.Col2}>
          <InfoCard
            caption={C.speed}
            tooltip={C.speedTooltip}
            text={done || estimate.ratePerSecond <= 0 ? '–' : `${formatBytes(estimate.ratePerSecond)}/s`}
            details={speedDetail}
          />
        </Column>
      )}
      <Column cols={Cols.Col2}>
        <Count name="rows-skipped" value={profile.rowsRejected} />
        <InfoCard
          caption={C.rejected}
          tooltip={C.rejectedTooltip}
          text={formatNumber(profile.rowsRejected)}
          details={format(C.rejectedDetail, formatNumber(profile.rowsValid))}
        />
      </Column>
      <Column cols={Cols.Col2}>
        <Count name="likely-duplicates" value={profile.likelyDuplicates} />
        <InfoCard caption={C.duplicates} tooltip={C.duplicatesTooltip} text={formatNumber(profile.likelyDuplicates)} details={C.duplicatesDetail} />
      </Column>
    </Row>
  );
};

/** Exact value of a count card for tests; the card itself shows it formatted. */
const Count = ({ name, value }: { readonly name: string; readonly value: number }) => (
  <span hidden={true} data-testid={`${TestIds.countPrefix}${name}`} data-count={value} />
);

const RejectReasons = ({ result }: { readonly result: PipelineResult }) => {
  const entries = Object.entries(result.rejectedByReason) as [keyof typeof S.reasons, number][];
  if (entries.length === 0) {
    return null;
  }

  return (
    <ul style={{ margin: `0 0 ${Spacing.S} 0`, paddingLeft: Spacing.XL }}>
      {entries.map(([reason, count]) => (
        <li key={reason}>
          {S.reasons[reason] ?? reason}: {formatNumber(count)}
        </li>
      ))}
    </ul>
  );
};
