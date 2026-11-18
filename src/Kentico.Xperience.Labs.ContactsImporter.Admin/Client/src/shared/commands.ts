import type { ConsentMapping, DateFormat, FieldMapping, ImportProfile, JobFile } from '../import-core';

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
  DeleteImport: 'DELETE_IMPORT',
  SaveOptions: 'SAVE_OPTIONS',
  ProcessNext: 'PROCESS_NEXT',
  ConfirmImport: 'CONFIRM_IMPORT',
  GetReport: 'GET_REPORT',
  GetRunningImport: 'GET_RUNNING_IMPORT',
  GetSamples: 'GET_SAMPLES',
  PauseProcessing: 'PAUSE_PROCESSING',
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
  | 'analysisExpired'
  /** `CONFIRM_IMPORT`: the import needs the consent confirmation and the request did not carry the current statement version. */
  | 'attestationRequired'
  /** `CONFIRM_IMPORT`: the user lacks the "Confirm consent" permission the import needs. */
  | 'consentPermissionRequired'
  /** `CONFIRM_IMPORT`: another import's Run was confirmed and has not ended; only one Run is unfinished at a time. */
  | 'otherImportRunning'
  /** `CONFIRM_IMPORT`: the server is checking another confirmation right now; try again. */
  | 'busy'
  /** `DELETE_IMPORT`: another browser tab or user analyzes or runs the import right now. */
  | 'importActive'
  /** `DELETE_IMPORT`: the files of the import could not be deleted. */
  | 'deleteFailed';

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

/** A recipient list or a consent the Options step offers. */
export interface ChoiceDto {
  id: number;
  name: string;
}

export interface ImportConfigResponse {
  fields: FieldDto[];
  limits: LimitsDto;
  /** Existing recipient lists, by name. */
  recipientLists: ChoiceDto[];
  /** Existing consents, by name. */
  consents: ChoiceDto[];
}

/** Who confirmed the import and which consent statement they saw. */
export interface AttestationDto {
  /** User name; empty when the user no longer exists. */
  userName: string;
  utc: string;
  statementVersion: string;
}

export interface JobDto {
  jobId: string;
  file: JobFile;
  keyField: string;
  mapping: FieldMapping[];
  dateFormat: DateFormat;
  /** File columns that say whether and when a contact gave consent; `null` when none. */
  consentMapping: ConsentMapping | null;
  createdUtc: string;
  options: OptionsDto;
  /** The email field is mapped and is not the match field. */
  invalidEmailApplies: boolean;
  /** Text contact fields that can hold the import source stamp. */
  sourceFields: FieldDto[];
  defaultSourceValue: string;
  /** Name of the recipient list of the saved options; `null` when none is chosen or it no longer exists. */
  recipientListName: string | null;
  /** Name of the consent of the saved options; `null` when none is chosen or it no longer exists. */
  consentName: string | null;
  /** The saved options change recipient list subscriptions or record consent: running needs the consent confirmation. */
  attestationRequired: boolean;
  /** Statement the user confirms by running such an import. Text and version come from the server only. */
  attestationStatement: string;
  attestationVersion: string;
  /** The stored confirmation; `null` until the import is confirmed with the statement. */
  attestation: AttestationDto | null;
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
  /** Recipient list contacts are added to as subscribed; `null` = recipient lists are not changed. */
  recipientListId: number | null;
  unsubscribed: 'keep' | 'resubscribe';
  bouncedAddress: 'addAndReport' | 'skipAndReport';
  consent: 'none' | 'blanket' | 'blanketWithColumnOverride';
  /** Consent agreements are recorded for; `null` when `consent` is `none`. */
  consentId: number | null;
  revokedConsent: 'keep' | 'reAgree';
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
  | 'busy'
  /** A call that would start the Run: another import's Run is unfinished (PLAN D60). Nothing changed. */
  | 'otherImportRunning';

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
  /** Recipient list: contacts added as subscribed. */
  listAdded: number;
  listAlreadySubscribed: number;
  listKeptUnsubscribed: number;
  listResubscribed: number;
  /** Not added because emails to the address bounced and the option says to skip them. */
  listSkippedBounced: number;
  listSkippedNoEmail: number;
  /** Contacts whose address had a hard bounce. */
  bouncedHard: number;
  /** Contacts whose address reached the soft bounce limit. */
  bouncedSoftOverLimit: number;
  /** Contacts whose address has soft bounces below the limit (emails are still sent). */
  bouncedSoftUnderLimit: number;
  consentRecorded: number;
  consentAlreadyAgreed: number;
  consentKeptRevoked: number;
  consentReAgreed: number;
  /** Column override: the row does not say the contact agreed. */
  consentNotGiven: number;
  consentSkippedFutureDate: number;
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
  /** `Paused` because a user selected Pause: the import is not continued by itself when it is opened again. */
  pausedByUser: boolean;
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
  consentMapping: ConsentMapping | null;
}

/** `CONFIRM_IMPORT` data. */
export interface ConfirmImportRequest {
  /** Version of the consent statement the user saw; `null` when the import needs no consent confirmation. */
  statementVersion: string | null;
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

/** `PAUSE_PROCESSING` data: the driver token of this tab (only the driver may pause the import). */
export interface PauseProcessingRequest {
  driverToken: string;
}

/** `DELETE_IMPORT` data. */
export interface DeleteImportRequest {
  /** Job to delete, on the imports list; the import page takes the job from its URL. */
  jobId?: string | null;
  /** Driver token of this tab when it analyzes the import, so the tab may delete the import it drives itself. */
  driverToken?: string | null;
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
  /** Name of the recipient list the import adds contacts to; `null` when none (or it no longer exists). */
  recipientListName: string | null;
  /** Name of the consent the import records agreements for; `null` when none (or it no longer exists). */
  consentName: string | null;
}

export interface ImportListResponse {
  imports: ImportListItemDto[];
  truncated: boolean;
}

/** Another import that runs right now (`GET_RUNNING_IMPORT`). File identity and counts only. */
export interface RunningImportDto {
  jobId: string;
  fileName: string;
  /** Who started the import (before it starts: who confirmed it, or the creator); empty when the user no longer exists. */
  userName: string;
  /** `Running`, `Paused` (started, waits to be continued or cancelled), or `Confirmed` (not started yet). */
  state: JobState;
  /** When the import started; `null` before it starts or when not known. */
  startedUtc: string | null;
  /** When a Run that has not started yet was confirmed; `null` once it started. */
  confirmedUtc?: string | null;
  /** Last progress of a started Run; `null` when not known. */
  lastProgressUtc?: string | null;
  rowsProcessed: number;
  rowsTotal: number;
}

export interface RunningImportResponse {
  code: CommandCode;
  message: string | null;
  /** `null` when no other import's Run is unfinished. */
  import: RunningImportDto | null;
  /** When the newest Run of another import ended (completed, failed or cancelled); `null` when none did. */
  lastRunEndedUtc?: string | null;
}

/** An example of a value the import changes in an existing contact (`GET_SAMPLES`). Personal data. */
export interface ValueSampleDto {
  /** Source file row number. */
  row: number;
  /** Match value of the row (for example the email). */
  key: string;
  /** Current value of the contact; `null` when it has none. */
  current: string | null;
  /** Value after the import; `null` when it is cleared. */
  new: string | null;
}

/** Examples of one contact field: at most three cleared and three changed values, lowest rows first. */
export interface FieldSamplesDto {
  field: string;
  cleared: ValueSampleDto[];
  changed: ValueSampleDto[];
}

/** Result of `GET_SAMPLES`: examples from the current check (dry run). `invalidState` when the job is not checked. */
export interface GetSamplesResponse {
  code: CommandCode;
  message: string | null;
  fields: FieldSamplesDto[];
}
