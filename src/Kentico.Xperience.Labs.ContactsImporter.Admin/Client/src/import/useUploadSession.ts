import { useCallback, useEffect, useRef, useState } from 'react';

import type { PipelineResult, TargetField } from '../import-core';
import { Commands } from '../shared/commands';
import type { ExecuteCommand, ImportJobResponse, JobDto, LimitsDto, StatusDto } from '../shared/commands';
import { useIsMounted } from '../shared/hooks';
import { useText } from '../shared/i18n';
import { RateEstimator, percent } from '../upload/progress';
import type { UploadPhase } from '../upload/upload-bar';
import { uploadFailureMessage } from '../upload/upload-messages';
import { UploadSessionError, startUploadSession } from '../upload/upload-session';
import type { UploadProgress, UploadSession } from '../upload/upload-session';
import type { ImportWorkerClient } from '../worker/client';

import { chunkSizes, toOpenOptions } from './model';
import type { SourceSettings } from './model';

/** What one upload pass needs: the saved job and the file selected in this tab. */
export interface UploadInput {
  readonly job: JobDto;
  /** Status the job had when the pass was asked for; used when the server cannot be asked again. */
  readonly status: StatusDto;
  readonly file: File;
  readonly settings: SourceSettings;
}

export interface UploadSessionOptions {
  /** `null` until the page has created its worker. */
  readonly worker: ImportWorkerClient | null;
  readonly execute: ExecuteCommand;
  /** `null` until the configuration is loaded. */
  readonly limits: LimitsDto | null;
  readonly targetFields: readonly TargetField[];
  /** The job status changed (upload completed, or changed elsewhere). */
  readonly onStatus: (status: StatusDto) => void;
}

export interface UploadController {
  readonly phase: UploadPhase;
  readonly progress: UploadProgress | null;
  /** Totals and the client-side reports of the last finished (or failed) pass in this tab. */
  readonly result: PipelineResult | null;
  readonly error: string | null;
  readonly elapsedSeconds: number | null;
  readonly estimator: RateEstimator;
  /** A file is selected in this tab, so the upload can continue. */
  readonly hasFile: boolean;
  /** Starts a pass with this job and file (after "Save and upload" or "Continue upload"). */
  readonly begin: (input: UploadInput) => void;
  /** Continues with the file of the last pass (after a pause or a failure). */
  readonly resume: () => void;
  readonly pause: () => void;
  /** Stops the pass without a trace (the import is cancelled). */
  readonly stop: () => void;
}

/**
 * The upload pass of the import page (worker + parallel chunk uploads), above the steps so it runs
 * on while the Options step is in view (D50). One pass at a time; stopped when the page closes.
 * Warns before the tab closes and shows the progress in the tab title while it runs.
 */
export function useUploadSession({ worker, execute, limits, targetFields, onStatus }: UploadSessionOptions): UploadController {
  const { t, m, percent: formatPercent } = useText();
  const isMounted = useIsMounted();
  const sessionRef = useRef<UploadSession | null>(null);
  const inputRef = useRef<UploadInput | null>(null);
  const estimatorRef = useRef(new RateEstimator());
  const startedAtRef = useRef(0);
  const startingRef = useRef(false);
  const pauseRequestedRef = useRef(false);
  const onStatusRef = useRef(onStatus);
  onStatusRef.current = onStatus;
  const [phase, setPhase] = useState<UploadPhase>('idle');
  const [progress, setProgress] = useState<UploadProgress | null>(null);
  const [result, setResult] = useState<PipelineResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [elapsedSeconds, setElapsedSeconds] = useState<number | null>(null);
  const [hasFile, setHasFile] = useState(false);

  const run = useCallback(async () => {
    const input = inputRef.current;
    if (!input || !worker || !limits || sessionRef.current || startingRef.current) {
      return;
    }

    startingRef.current = true;
    pauseRequestedRef.current = false;
    estimatorRef.current.reset();
    startedAtRef.current = Date.now();
    setError(null);
    setResult(null);
    setProgress(null);
    setElapsedSeconds(null);
    setPhase('running');

    // Ask what the server has now: chunks stored before a pause in this visit are not in the input.
    const current = await execute<ImportJobResponse>(Commands.GetJob);
    startingRef.current = false;
    if (!isMounted() || inputRef.current !== input) {
      return;
    }
    if (pauseRequestedRef.current) {
      pauseRequestedRef.current = false;
      setPhase('paused');

      return;
    }

    const currentStatus = current?.status ?? input.status;
    if (currentStatus.state !== 'Uploading') {
      // Finished or changed elsewhere (another tab or user): show what the server says.
      setPhase(currentStatus.state === 'Uploaded' ? 'done' : 'idle');
      onStatusRef.current(currentStatus);

      return;
    }

    const { job } = input;
    const session = startUploadSession({
      worker,
      execute,
      file: input.file,
      source: toOpenOptions(input.settings),
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
        onStatusRef.current(outcome.status);
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
  }, [execute, isMounted, limits, m, targetFields, worker]);

  const begin = useCallback(
    (input: UploadInput) => {
      sessionRef.current?.abort();
      sessionRef.current = null;
      inputRef.current = input;
      setHasFile(true);
      void run();
    },
    [run],
  );

  const resume = useCallback(() => void run(), [run]);

  const pause = useCallback(() => {
    if (startingRef.current) {
      pauseRequestedRef.current = true;
    }
    sessionRef.current?.abort();
  }, []);

  const stop = useCallback(() => {
    const session = sessionRef.current;
    sessionRef.current = null;
    inputRef.current = null;
    session?.abort();
    setHasFile(false);
    setPhase('idle');
  }, []);

  // Closing the page stops the pass; stored chunks stay for a resume.
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
  const fileName = inputRef.current?.job.file.name ?? '';

  // Show progress in the tab title, so it is visible from other tabs.
  useEffect(() => {
    if (!running) {
      return undefined;
    }

    const original = document.title;
    document.title = t('contactsimporter.run.title', { percent: formatPercent(completed), fileName });

    return () => {
      document.title = original;
    };
  }, [running, completed, fileName, formatPercent, t]);

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

  return {
    phase,
    progress,
    result,
    error,
    elapsedSeconds,
    estimator: estimatorRef.current,
    hasFile,
    begin,
    resume,
    pause,
    stop,
  };
}
