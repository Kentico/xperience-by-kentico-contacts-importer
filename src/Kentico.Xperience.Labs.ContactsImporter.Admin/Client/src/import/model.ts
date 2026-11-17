import type { OpenSourceOptions, SourceFormat, TargetField } from '../import-core';
import type {
  AggregatesDto,
  FieldDto,
  ImportJobResponse,
  JobDto,
  JobState,
  LimitsDto,
  ProcessPhase,
  ProgressDto,
  ReportsDto,
  StatusDto,
} from '../shared/commands';

/** Steps of an import (PLAN §14). */
export const STEPS = ['file', 'map', 'upload', 'options', 'review', 'run', 'results'] as const;

export type Step = (typeof STEPS)[number];

/**
 * Step a job in `state` is at (PLAN §4 "UI steps ↔ states"). `Paused` is derived from a stale
 * heartbeat and can be a paused analysis or a paused import: `phase` tells which.
 */
export function stepForState(state: JobState, phase: ProcessPhase = 'execute'): Step {
  switch (state) {
    case 'Configuring':
      return 'file';
    case 'Uploading':
    case 'Uploaded':
      return 'upload';
    case 'Analyzing':
    case 'Analyzed':
      return 'review';
    case 'Confirmed':
    case 'Running':
      return 'run';
    case 'Paused':
      return phase === 'analyze' ? 'review' : 'run';
    case 'Completed':
    case 'Failed':
    case 'Cancelled':
      return 'results';
    default:
      return 'file';
  }
}

export function isFinished(state: JobState): boolean {
  return state === 'Completed' || state === 'Failed' || state === 'Cancelled';
}

/** How the file is read; empty strings mean "detect". Chosen in the File step. */
export interface SourceSettings {
  readonly format: '' | SourceFormat;
  readonly encoding: string;
  readonly delimiter: string;
  readonly hasHeader: boolean;
}

export const DEFAULT_SOURCE_SETTINGS: SourceSettings = { format: '', encoding: '', delimiter: '', hasHeader: true };

export function toOpenOptions(settings: SourceSettings): OpenSourceOptions {
  return {
    ...(settings.format !== '' && { format: settings.format }),
    ...(settings.encoding !== '' && { encoding: settings.encoding }),
    ...(settings.delimiter !== '' && { delimiter: settings.delimiter }),
    hasHeader: settings.hasHeader,
  };
}

/** Uncompressed size cap of one chunk the browser builds (import core default). */
const CLIENT_MAX_CHUNK_UNCOMPRESSED_BYTES = 32 * 1024 * 1024;

/**
 * Contact fields as the import core wants them. The value length limit mirrors the server's
 * chunk validation, so rows the server would refuse are skipped in the browser instead:
 * text fields are limited by their size, all other fields by a fixed length.
 */
export function toTargetFields(fields: readonly FieldDto[], limits: LimitsDto): TargetField[] {
  return fields.map((field) => {
    const isText = field.dataType.toLowerCase() === 'text' || field.dataType.toLowerCase() === 'longtext';
    const maxLength = isText ? (field.maxLength ?? undefined) : limits.maxNonTextValueLength;

    return {
      name: field.name,
      caption: field.caption,
      dataType: field.dataType,
      ...(maxLength !== undefined && { maxLength }),
    };
  });
}

/** Chunk sizes for the worker, within the server's limits. */
export function chunkSizes(limits: LimitsDto): { targetChunkBytes: number; maxChunkUncompressedBytes: number } {
  return {
    targetChunkBytes: limits.chunkSizeBytes,
    maxChunkUncompressedBytes: Math.min(CLIENT_MAX_CHUNK_UNCOMPRESSED_BYTES, limits.maxChunkUncompressedBytes),
  };
}

/** Caption of a contact field for the mapping selects: `Email (ContactEmail)` or just the name. */
export function fieldLabel(field: Pick<FieldDto, 'name' | 'caption'>): string {
  return field.caption && field.caption !== field.name ? `${field.caption} (${field.name})` : field.name;
}

/** A stored job as `GET_JOB` returns it. */
export interface JobView {
  readonly job: JobDto;
  readonly status: StatusDto;
  readonly progress: ProgressDto;
  readonly reports: ReportsDto;
}

/** `JobView` of a command response; `null` when the response holds no job. */
export function toJobView(response: ImportJobResponse | undefined): JobView | null {
  return response?.job && response.status && response.progress && response.reports
    ? { job: response.job, status: response.status, progress: response.progress, reports: response.reports }
    : null;
}

/** Existing contacts changed by one import above which the confirmation must be typed (PLAN §10). */
export const CONFIRM_TYPING_THRESHOLD = 1000;

export const NO_COUNTS: AggregatesDto = {
  rows: 0,
  created: 0,
  updated: 0,
  unchanged: 0,
  contactsUpdated: 0,
  skippedNew: 0,
  skippedExisting: 0,
  skippedMultipleMatches: 0,
  skippedInvalidEmail: 0,
  skippedInvalidValue: 0,
  skipped: 0,
  duplicateRows: 0,
  multipleMatches: 0,
  importedWithoutEmail: 0,
  listAdded: 0,
  listAlreadySubscribed: 0,
  listKeptUnsubscribed: 0,
  listResubscribed: 0,
  listSkippedBounced: 0,
  listSkippedNoEmail: 0,
  bouncedHard: 0,
  bouncedSoftOverLimit: 0,
  bouncedSoftUnderLimit: 0,
  consentRecorded: 0,
  consentAlreadyAgreed: 0,
  consentKeptRevoked: 0,
  consentReAgreed: 0,
  consentNotGiven: 0,
  consentSkippedFutureDate: 0,
  fields: [],
};

/**
 * Progress to show before the first call of `phase`: the stored progress when it belongs to that
 * phase, otherwise the same totals with nothing processed.
 */
export function progressFor(phase: ProcessPhase, progress: ProgressDto): ProgressDto {
  return progress.phase === phase ? progress : { ...progress, phase, rowsProcessed: 0, aggregates: NO_COUNTS };
}
