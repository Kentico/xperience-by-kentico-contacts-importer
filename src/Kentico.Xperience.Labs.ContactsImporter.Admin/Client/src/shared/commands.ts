import type { DateFormat, FieldMapping, ImportProfile, JobFile } from '../import-core';

/**
 * Page commands of the importer pages and their DTOs. Keep in sync with
 * `UIPages/ImportCommandContracts.cs` on the server (JSON is camelCase).
 */
export const Commands = {
  ListImports: 'LIST_IMPORTS',
  LoadConfig: 'LOAD_CONFIG',
  GetJob: 'GET_JOB',
  SaveConfig: 'SAVE_CONFIG',
  UploadChunk: 'UPLOAD_CHUNK',
  TruncateChunks: 'TRUNCATE_CHUNKS',
  CompleteUpload: 'COMPLETE_UPLOAD',
  CancelImport: 'CANCEL_IMPORT',
  DeleteFiles: 'DELETE_FILES',
  SaveOptions: 'SAVE_OPTIONS',
  ProcessNext: 'PROCESS_NEXT',
  ConfirmImport: 'CONFIRM_IMPORT',
  GetReport: 'GET_REPORT',
} as const;

export type CommandName = (typeof Commands)[keyof typeof Commands];

/**
 * Runs a page command and returns its result, or `undefined` when the request failed
 * (network error, server error, permission denied). Same shape as `executeCommand` of
 * `usePageCommandProvider`, so tests can pass a fake.
 */
export type ExecuteCommand = <TResult, TData = void>(
  name: CommandName,
  data?: TData,
  signal?: AbortSignal,
) => Promise<TResult | undefined>;

export type CommandCode =
  | 'ok'
  | 'invalidRequest'
  | 'jobNotFound'
  | 'invalidState'
  | 'invalidConfig'
  | 'inconsistent'
  | 'analysisExpired';

/** Job states (PLAN §4). `Paused` is derived by the server from a stale heartbeat. */
export type JobState =
  | 'Configuring'
  | 'Uploading'
  | 'Uploaded'
  | 'Analyzing'
  | 'Analyzed'
  | 'Confirmed'
  | 'Running'
  | 'Paused'
  | 'Completed'
  | 'Failed'
  | 'Cancelled';

export interface FieldDto {
  name: string;
  caption: string;
  dataType: string;
  maxLength: number | null;
  /** The import can write the field; other fields are not offered as mapping targets. */
  isSupported: boolean;
  /** The field can be the match field. */
  canBeKey: boolean;
  /** The field holds the ID of another object (country, contact status, user, ...); rows whose value does not exist there are skipped. */
  isReference?: boolean;
  /** Xperience object type the field references, for example `cms.country`; null when not known or not a reference. */
  referencedObjectType?: string | null;
}

export interface LimitsDto {
  maxFileSizeBytes: number;
  chunkSizeBytes: number;
  maxChunkCompressedBytes: number;
  maxChunkUncompressedBytes: number;
  maxNonTextValueLength: number;
  maxKeyLength: number;
  parallelUploads: number;
}

export interface ImportConfigResponse {
  fields: FieldDto[];
  limits: LimitsDto;
}

export interface JobDto {
  jobId: string;
  file: JobFile;
  keyField: string;
  mapping: FieldMapping[];
  dateFormat: DateFormat;
  createdUtc: string;
  options: OptionsDto;
  /** The email field is mapped and is not the match field. */
  invalidEmailApplies: boolean;
  /** Text contact fields that can hold the import source stamp. */
  sourceFields: FieldDto[];
  defaultSourceValue: string;
}

/** Contact options of the Options step (PLAN §8); the values are the server option names. */
export interface OptionsDto {
  newContacts: 'create' | 'skip';
  existingContacts: 'update' | 'skip';
  emptyValues: 'ignore' | 'clear';
  multipleMatches: 'updateNewest' | 'updateAll' | 'skip';
  invalidEmail: 'skipRow' | 'importWithoutEmail';
  sourceField: string | null;
  sourceValue: string | null;
  sourceStamp: 'createdOnly' | 'createdAndUpdated';
  reportIncludesKey: boolean;
}

export type ProcessPhase = 'analyze' | 'execute';

/** Result of one `PROCESS_NEXT` call (server `ImportProcessOutcome`, plus `invalidRequest` and `busy`). */
export type ProcessOutcome =
  | 'processed'
  | 'phaseFinished'
  | 'jobNotFound'
  | 'invalidState'
  | 'activeElsewhere'
  | 'staleRequiresTakeOver'
  | 'analysisExpired'
  | 'interrupted'
  | 'transientError'
  | 'failed'
  | 'invalidRequest'
  | 'busy';

export interface FieldChangeDto {
  field: string;
  changed: number;
  cleared: number;
}

/** Counts of an analysis or an import, computed by the server. */
export interface AggregatesDto {
  rows: number;
  created: number;
  updated: number;
  unchanged: number;
  contactsUpdated: number;
  skippedNew: number;
  skippedExisting: number;
  skippedMultipleMatches: number;
  skippedInvalidEmail: number;
  skippedInvalidValue: number;
  skipped: number;
  duplicateRows: number;
  multipleMatches: number;
  importedWithoutEmail: number;
  fields: FieldChangeDto[];
}

export interface ProgressDto {
  outcome: ProcessOutcome;
  phase: ProcessPhase;
  state: JobState;
  driverToken: string | null;
  rowsProcessed: number;
  rowsTotal: number;
  /** Running counts of `phase`. */
  aggregates: AggregatesDto;
  /** Counts of the finished analysis (expected). */
  analysis: AggregatesDto | null;
  analyzedUtc: string | null;
  error: string | null;
}

export interface ProcessNextRequest {
  phase: ProcessPhase;
  driverToken: string | null;
  takeOver: boolean;
}

export interface ReportsDto {
  analyze: boolean;
  execute: boolean;
  includesKey: boolean;
}

export interface GetReportRequest {
  phase: ProcessPhase;
  part: number;
  line: number;
}

export interface GetReportResponse {
  code: CommandCode;
  message: string | null;
  fileName: string | null;
  header: string | null;
  /** Base64 of the gzipped CSV lines of the page. */
  data: string | null;
  rows: number;
  nextPart: number;
  nextLine: number;
  done: boolean;
}

export interface StoredChunkDto {
  index: number;
  sha256: string;
  rows: number;
}

export interface StatusDto {
  state: JobState;
  chunks: StoredChunkDto[];
  profile: ImportProfile;
  updatedUtc: string;
  error: string | null;
}

/** Result of `GET_JOB`, `SAVE_OPTIONS` and `CONFIRM_IMPORT`. */
export interface ImportJobResponse {
  code: CommandCode;
  message: string | null;
  errors: string[];
  job: JobDto | null;
  status: StatusDto | null;
  progress: ProgressDto | null;
  reports: ReportsDto | null;
}

export interface SaveConfigRequest {
  file: JobFile;
  keyField: string;
  mapping: FieldMapping[];
  dateFormat: DateFormat;
}

export interface SaveConfigResponse {
  code: CommandCode;
  message: string | null;
  errors: string[];
  job: JobDto | null;
  status: StatusDto | null;
}

export interface UploadChunkResponse {
  code: 'stored' | 'alreadyStored' | 'rejected';
  /** camelCase `ChunkRejectReason` when rejected. */
  reason: string | null;
  message: string | null;
  rowNumber: number | null;
}

export interface TruncateChunksRequest {
  fromIndex: number;
}

export interface TruncateChunksResponse {
  code: CommandCode;
  message: string | null;
  deleted: number;
  chunks: StoredChunkDto[];
}

export interface CompleteUploadRequest {
  profile: ImportProfile;
}

export interface CompleteUploadResponse {
  code: CommandCode;
  message: string | null;
  missingChunks: number[];
  status: StatusDto | null;
}

export interface ImportActionResponse {
  code: CommandCode;
  message: string | null;
  status: StatusDto | null;
}

export interface ImportJobRequest {
  jobId: string;
}

export interface ImportListItemDto {
  jobId: string;
  fileName: string;
  fileSize: number;
  createdUtc: string;
  createdBy: string;
  state: JobState;
  updatedUtc: string;
  rowsValid: number;
  importStarted: boolean;
  created: number;
  updated: number;
  canContinue: boolean;
  hasFiles: boolean;
}

export interface ImportListResponse {
  imports: ImportListItemDto[];
  truncated: boolean;
}
