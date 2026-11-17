import type {
  ConsentMapping,
  DateFormat,
  EncodedChunk,
  FieldMapping,
  FileLike,
  ImportErrorParams,
  OpenSourceOptions,
  PipelineProgress,
  PipelineResult,
  SampleProfile,
  TargetField,
} from '../import-core';

/**
 * Messages between the page (main thread) and the import worker.
 *
 * One worker serves one import page. Every request carries a `requestId`; events answer with
 * the same ID, so a late event of an aborted request is ignored by the page.
 *
 * Full pass (`start`) flow control: the worker sends at most `window` chunks that the page did
 * not acknowledge (`ack`) yet, then stops pulling the pipeline. The page acknowledges a chunk
 * once it is uploaded or skipped, so parsing never runs ahead of the upload and memory stays
 * bounded by `window` chunks.
 */
export type WorkerRequest = ProfileRequest | StartRequest | AckRequest | AbortRequest;

/** Profile the first rows of a file and compute its fingerprint. Answer: `profiled` or `error`. */
export interface ProfileRequest {
  type: 'profile';
  requestId: number;
  file: FileLike;
  source: OpenSourceOptions;
  targetFields: TargetField[];
  /** Rows to profile. Default 1000. */
  maxRows?: number;
}

/** Full pass. Answers: `chunk`*, `progress`*, then `done` or `error`. */
export interface StartRequest {
  type: 'start';
  requestId: number;
  file: FileLike;
  source: OpenSourceOptions;
  pipeline: PipelineConfig;
  /** Most chunks sent but not acknowledged. */
  window: number;
  /** Shortest time between two `progress` events in ms. Default 200. */
  progressIntervalMs?: number;
}

export interface PipelineConfig {
  jobId: string;
  keyField: string;
  mapping: FieldMapping[];
  dateFormat: DateFormat;
  /** Consent columns of the job; `null` or missing = records carry no consent part. */
  consentMapping?: ConsentMapping | null;
  fields: TargetField[];
  targetChunkBytes: number;
  maxChunkUncompressedBytes: number;
}

/** The page finished with chunk `index` of request `requestId` (uploaded or skipped). */
export interface AckRequest {
  type: 'ack';
  requestId: number;
  index: number;
}

/** Stops request `requestId`. The worker answers `error` with code `aborted`. */
export interface AbortRequest {
  type: 'abort';
  requestId: number;
}

export type WorkerEvent = ProfiledEvent | ChunkEvent | ProgressEvent | DoneEvent | ErrorEvent;

export interface ProfiledEvent {
  type: 'profiled';
  requestId: number;
  profile: SampleProfile;
  fingerprint: string;
}

export interface ChunkEvent {
  type: 'chunk';
  requestId: number;
  chunk: EncodedChunk;
}

export interface ProgressEvent {
  type: 'progress';
  requestId: number;
  progress: PipelineProgress;
}

export interface DoneEvent {
  type: 'done';
  requestId: number;
  result: PipelineResult;
}

/** `ImportErrorCode` of the import core, `aborted`, or `failed` for anything unexpected. */
export type WorkerErrorCode =
  | 'unsupportedFormat'
  | 'unsupportedEncoding'
  | 'recordTooLarge'
  | 'invalidConfig'
  | 'sourceConsumed'
  | 'aborted'
  | 'failed';

export interface ErrorEvent {
  type: 'error';
  requestId: number;
  code: WorkerErrorCode;
  /** Developer text (English); for `failed` the message of the unexpected error. */
  message: string;
  /** `ImportError.params` of the import core. */
  params?: ImportErrorParams;
}

/**
 * Error of a worker request, as seen by the page. The UI shows a text of its own for `code` and
 * `params` (see `workerErrorMessage`); `message` is developer text.
 */
export class ImportWorkerError extends Error {
  readonly code: WorkerErrorCode;
  readonly params: ImportErrorParams;

  constructor(code: WorkerErrorCode, message: string, params: ImportErrorParams = {}) {
    super(message);
    this.name = 'ImportWorkerError';
    this.code = code;
    this.params = params;
  }
}
