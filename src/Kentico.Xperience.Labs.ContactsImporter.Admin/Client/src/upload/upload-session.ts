import type { FileLike, OpenSourceOptions, PipelineProgress, PipelineResult } from '../import-core';
import { Commands } from '../shared/commands';
import type {
  CompleteUploadRequest,
  CompleteUploadResponse,
  ExecuteCommand,
  StatusDto,
  StoredChunkDto,
  TruncateChunksRequest,
  TruncateChunksResponse,
  UploadChunkResponse,
} from '../shared/commands';
import type { ImportRun, ImportWorkerClient } from '../worker/client';
import { ImportWorkerError } from '../worker/protocol';
import type { PipelineConfig } from '../worker/protocol';

import { RetryExhaustedError, withRetry } from './retry';
import type { RetryOptions } from './retry';
import { ChunkUploader, UploadError } from './uploader';
import type { UploadStats } from './uploader';

/**
 * One full pass of the Upload step: the worker parses and chunks the file, the uploader sends
 * the chunks (skipping the ones the server already has), then `COMPLETE_UPLOAD` stores the
 * profile. No React here; the Upload step renders the progress snapshots.
 */
export interface UploadSessionOptions {
  worker: ImportWorkerClient;
  execute: ExecuteCommand;
  file: FileLike;
  source: OpenSourceOptions;
  pipeline: PipelineConfig;
  /** Uploads running at the same time. */
  parallel: number;
  /** Chunks the server already has (resume). */
  storedChunks: readonly StoredChunkDto[];
  retry?: Omit<RetryOptions, 'signal' | 'onRetry'>;
  onProgress?: (progress: UploadProgress) => void;
}

export interface UploadProgress {
  phase: 'uploading' | 'completing';
  /** Parsing position and running row counts from the worker. */
  pipeline: PipelineProgress;
  /** Chunks produced by the worker so far. */
  chunksCreated: number;
  stats: UploadStats;
  /** Set while a failed request waits for its next attempt. */
  retry: { index: number; attempt: number; maxAttempts: number; delayMs: number } | null;
  /** Stored chunks were deleted because the file was chunked differently before (other browser). */
  truncatedFrom: number | null;
}

export interface UploadOutcome {
  /** Totals and the client-side reports (rejected rows, likely duplicates). */
  result: PipelineResult;
  /** Job status after `COMPLETE_UPLOAD` (state `Uploaded`). */
  status: StatusDto;
  stats: UploadStats;
}

export type UploadSessionErrorKind =
  /** Stopped by the user. */
  | 'aborted'
  /** The file could not be parsed or the mapping does not fit it. */
  | 'worker'
  /** The server refused a chunk. */
  | 'rejected'
  /** Requests kept failing (network, server down). */
  | 'network'
  /** No row of the file can be imported, so there is nothing to upload. */
  | 'noRows'
  /** The server did not accept the finished upload. */
  | 'complete';

export class UploadSessionError extends Error {
  readonly kind: UploadSessionErrorKind;
  /** Source row number named by the server (`rejected`). */
  readonly rowNumber?: number;
  /** Server's reject reason, camelCase `ChunkRejectReason` (`rejected`). */
  readonly reason?: string;
  /** Totals of the pass, when it got far enough (`noRows`, `complete`). */
  readonly result?: PipelineResult;

  constructor(kind: UploadSessionErrorKind, message: string, details: { rowNumber?: number; reason?: string; result?: PipelineResult } = {}) {
    super(message);
    this.name = 'UploadSessionError';
    this.kind = kind;
    this.rowNumber = details.rowNumber;
    this.reason = details.reason;
    this.result = details.result;
  }
}

export interface UploadSession {
  /** Resolves when the job is `Uploaded`; rejects with `UploadSessionError`. */
  readonly done: Promise<UploadOutcome>;
  /** Stops parsing and uploading. Chunks already stored stay on the server for a later resume. */
  abort(): void;
}

const EMPTY_PROGRESS: PipelineProgress = {
  bytesRead: 0,
  totalBytes: 0,
  profile: { rowsRead: 0, rowsValid: 0, rowsRejected: 0, likelyDuplicates: 0, chunkCount: 0 },
  invalidEmailRows: 0,
};

export function startUploadSession(options: UploadSessionOptions): UploadSession {
  const controller = new AbortController();
  let run: ImportRun | undefined;
  let uploader: ChunkUploader | undefined;

  const abort = () => {
    controller.abort(new UploadSessionError('aborted', 'The upload was stopped.'));
    run?.abort();
    uploader?.abort();
  };

  const execute = async (): Promise<UploadOutcome> => {
    const { worker, execute: executeCommand, onProgress } = options;
    const progress: UploadProgress = {
      phase: 'uploading',
      pipeline: { ...EMPTY_PROGRESS, totalBytes: options.file.size },
      chunksCreated: 0,
      stats: { uploadedChunks: 0, skippedChunks: 0, uploadedBytes: 0, skippedBytes: 0, retries: 0 },
      retry: null,
      truncatedFrom: null,
    };
    const report = () => onProgress?.({ ...progress });

    const activeUploader = new ChunkUploader({
      parallel: options.parallel,
      storedChunks: options.storedChunks,
      retry: options.retry,
      send: (payload, signal) => executeCommand<UploadChunkResponse, typeof payload>(Commands.UploadChunk, payload, signal),
      truncate: (fromIndex, signal) =>
        executeCommand<TruncateChunksResponse, TruncateChunksRequest>(Commands.TruncateChunks, { fromIndex }, signal),
      onEvent: (event) => {
        progress.stats = activeUploader.stats;
        if (event.type === 'retry') {
          progress.retry = { index: event.index, attempt: event.attempt, maxAttempts: event.maxAttempts, delayMs: event.delayMs };
        } else if (event.type === 'truncated') {
          progress.truncatedFrom = event.fromIndex;
        } else {
          progress.retry = null;
        }
        report();
      },
    });
    uploader = activeUploader;

    const activeRun = worker.run(options.file, {
      source: options.source,
      pipeline: options.pipeline,
      // One chunk more than the uploads in flight, so a slot never waits for the worker.
      window: options.parallel + 1,
      onProgress: (pipeline) => {
        progress.pipeline = pipeline;
        report();
      },
      onChunk: (chunk) => {
        progress.chunksCreated = chunk.payload.index + 1;
        report();
        activeUploader.add(chunk).then(
          () => activeRun.acknowledge(chunk.payload.index),
          // The first failure stops the worker; nothing else to do per chunk.
          () => activeRun.abort(),
        );
      },
    });
    run = activeRun;
    if (controller.signal.aborted) {
      activeRun.abort();
      activeUploader.abort();
    }

    let result: PipelineResult;
    try {
      result = await activeRun.done;
    } catch (error) {
      activeUploader.abort();
      // An upload failure aborts the worker, so the upload error is the cause to report.
      const uploadError = await activeUploader.finish().then(
        () => undefined,
        (reason: unknown) => reason,
      );
      throw toSessionError(controller.signal, uploadError, error);
    }

    try {
      await activeUploader.finish();
    } catch (error) {
      throw toSessionError(controller.signal, error, undefined);
    }

    progress.pipeline = result;
    progress.stats = activeUploader.stats;
    progress.retry = null;
    if (result.profile.chunkCount === 0) {
      report();
      throw new UploadSessionError('noRows', 'No row of the file can be imported.', { result });
    }

    progress.phase = 'completing';
    report();

    let completion: CompleteUploadResponse;
    try {
      completion = await withRetry(
        () =>
          executeCommand<CompleteUploadResponse, CompleteUploadRequest>(
            Commands.CompleteUpload,
            { profile: result.profile },
            controller.signal,
          ),
        { ...options.retry, signal: controller.signal },
      );
    } catch (error) {
      throw toSessionError(controller.signal, error, undefined);
    }

    if (completion.code !== 'ok' || !completion.status) {
      throw new UploadSessionError('complete', completion.message ?? 'The upload could not be completed.', { result });
    }

    return { result, status: completion.status, stats: activeUploader.stats };
  };

  return { done: execute(), abort };
}

function toSessionError(signal: AbortSignal, uploadError: unknown, workerError: unknown): UploadSessionError {
  if (signal.aborted) {
    return new UploadSessionError('aborted', 'The upload was stopped.');
  }
  if (uploadError instanceof UploadError && uploadError.code !== 'aborted') {
    const kind = uploadError.code === 'network' ? 'network' : 'rejected';

    return new UploadSessionError(kind, uploadError.message, { rowNumber: uploadError.rowNumber, reason: uploadError.reason });
  }
  if (uploadError instanceof RetryExhaustedError) {
    return new UploadSessionError('network', uploadError.message);
  }
  if (workerError instanceof ImportWorkerError && workerError.code !== 'aborted') {
    return new UploadSessionError('worker', workerError.message);
  }

  const cause = uploadError ?? workerError;

  return new UploadSessionError('worker', cause instanceof Error ? cause.message : 'The upload failed.');
}
