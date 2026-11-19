import type {
  OpenSourceOptions,
  SourceFormat,
  TargetField,
} from "../import-core";
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
} from "../shared/commands";

/** Steps of an import (PLAN §14). */
export const STEPS = [
  "file",
  "map",
  "upload",
  "options",
  "review",
  "run",
  "results",
] as const;

export type Step = (typeof STEPS)[number];

/**
 * Step a job in `state` is at (PLAN §4 "UI steps ↔ states"). `Paused` is derived from a stale
 * heartbeat and can be a paused analysis or a paused import: `phase` tells which.
 */
export function stepForState(
  state: JobState,
  phase: ProcessPhase = "execute",
): Step {
  switch (state) {
    case "Configuring":
      return "file";
    case "Uploading":
    case "Uploaded":
      // The upload runs in the background (upload bar); the Options step is in view meanwhile.
      return "options";
    case "Analyzing":
    case "Analyzed":
      return "review";
    case "Confirmed":
    case "Running":
      return "run";
    case "Paused":
      return phase === "analyze" ? "review" : "run";
    case "Completed":
    case "Failed":
    case "Cancelled":
      return "results";
    default:
      return "file";
  }
}

export function isFinished(state: JobState): boolean {
  return state === "Completed" || state === "Failed" || state === "Cancelled";
}

/**
 * How a step shows in the step header. `done`: finished and can be opened again; `locked`: finished
 * (or passed) and cannot be opened again for this import; `upcoming`: not reached yet, or running
 * (the upload in the background).
 */
export type StepState = "current" | "done" | "upcoming" | "locked";

export interface StepView {
  readonly state: StepState;
  /** The header offers the step as a button. */
  readonly clickable: boolean;
}

export interface StepContext {
  /** Step in view. */
  readonly current: Step;
  /** State of the stored job; `null` before the first save (no job yet). */
  readonly jobState: JobState | null;
  /** Phase of a `Paused` job (`analyze` = paused check). */
  readonly phase?: ProcessPhase;
}

/**
 * Step header rules (D50): File, Map and Upload lock once the upload has started; Options and
 * Review stay open until the import is confirmed (Review once a check exists or runs); from the
 * confirmation on every earlier step is locked; a finished import offers only Results.
 */
export function stepViews({
  current,
  jobState,
  phase = "execute",
}: StepContext): Record<Step, StepView> {
  const view = (step: Step, state: StepState): StepView =>
    step === current
      ? { state: "current", clickable: false }
      : { state, clickable: state === "done" };
  const build = (
    pick: (step: Step, index: number) => StepView,
  ): Record<Step, StepView> =>
    Object.fromEntries(
      STEPS.map((step, index) => [step, pick(step, index)]),
    ) as Record<Step, StepView>;

  switch (stageOf(jobState, phase)) {
    case "setup": {
      // Before the upload: back to an earlier step, never ahead.
      const currentIndex = STEPS.indexOf(current);

      return build((step, index) =>
        view(step, index < currentIndex ? "done" : "upcoming"),
      );
    }
    case "prepare": {
      const uploading = jobState === "Uploading";
      const checkExists =
        jobState === "Analyzing" ||
        jobState === "Analyzed" ||
        jobState === "Paused";

      return build((step) => {
        switch (step) {
          case "file":
          case "map":
            // File is in view again only to select the file of an interrupted upload (resume).
            return view(step, "locked");
          case "upload":
            return view(step, uploading ? "upcoming" : "locked");
          case "options":
            // While the file is selected again for a resume, the upload has not continued yet.
            return view(
              step,
              uploading && current === "file" ? "upcoming" : "done",
            );
          case "review":
            return view(step, checkExists ? "done" : "upcoming");
          default:
            return view(step, "upcoming");
        }
      });
    }
    case "execute": {
      const runIndex = STEPS.indexOf("run");

      return build((step, index) =>
        view(step, index < runIndex ? "locked" : "upcoming"),
      );
    }
    default:
      // Finished: Results is the only step left.
      return build((step) =>
        view(step, step === "results" ? "done" : "locked"),
      );
  }
}

type Stage = "setup" | "prepare" | "execute" | "finished";

function stageOf(state: JobState | null, phase: ProcessPhase): Stage {
  switch (state) {
    case null:
    case "Configuring":
      return "setup";
    case "Uploading":
    case "Uploaded":
    case "Analyzing":
    case "Analyzed":
      return "prepare";
    case "Paused":
      return phase === "analyze" ? "prepare" : "execute";
    case "Confirmed":
    case "Running":
      return "execute";
    default:
      return "finished";
  }
}

/** How the file is read; empty strings mean "detect". Chosen in the File step. */
export interface SourceSettings {
  readonly format: "" | SourceFormat;
  readonly encoding: string;
  readonly delimiter: string;
  readonly hasHeader: boolean;
  /** XLSX worksheet; empty = the first visible sheet. */
  readonly sheet: string;
}

export const DEFAULT_SOURCE_SETTINGS: SourceSettings = {
  format: "",
  encoding: "",
  delimiter: "",
  hasHeader: true,
  sheet: "",
};

export function toOpenOptions(settings: SourceSettings): OpenSourceOptions {
  return {
    ...(settings.format !== "" && { format: settings.format }),
    ...(settings.encoding !== "" && { encoding: settings.encoding }),
    ...(settings.delimiter !== "" && { delimiter: settings.delimiter }),
    hasHeader: settings.hasHeader,
    ...(settings.sheet !== "" && { sheet: settings.sheet }),
  };
}

/** Uncompressed size cap of one chunk the browser builds (import core default). */
const CLIENT_MAX_CHUNK_UNCOMPRESSED_BYTES = 32 * 1024 * 1024;

/**
 * Contact fields as the import core wants them. The value length limit mirrors the server's
 * chunk validation, so rows the server would refuse are skipped in the browser instead:
 * text fields are limited by their size, all other fields by a fixed length.
 */
export function toTargetFields(
  fields: readonly FieldDto[],
  limits: LimitsDto,
): TargetField[] {
  return fields.map((field) => {
    const isText =
      field.dataType.toLowerCase() === "text" ||
      field.dataType.toLowerCase() === "longtext";
    const maxLength = isText
      ? (field.maxLength ?? undefined)
      : limits.maxNonTextValueLength;

    return {
      name: field.name,
      caption: field.caption,
      dataType: field.dataType,
      ...(maxLength !== undefined && { maxLength }),
    };
  });
}

/** Chunk sizes for the worker, within the server's limits. */
export function chunkSizes(limits: LimitsDto): {
  targetChunkBytes: number;
  maxChunkUncompressedBytes: number;
} {
  return {
    targetChunkBytes: limits.chunkSizeBytes,
    maxChunkUncompressedBytes: Math.min(
      CLIENT_MAX_CHUNK_UNCOMPRESSED_BYTES,
      limits.maxChunkUncompressedBytes,
    ),
  };
}

/**
 * Readable name of a contact field: its caption, or, when the field has none (Xperience system
 * fields: caption = column name), the column name without the `Contact` prefix in words
 * (`ContactBusinessPhone` → `Business phone`). The column name itself is `field.name`.
 */
export function fieldLabel(field: Pick<FieldDto, "name" | "caption">): string {
  if (field.caption && field.caption !== field.name) {
    return field.caption;
  }

  return humanizeFieldName(field.name);
}

function humanizeFieldName(name: string): string {
  const bare = /^Contact[A-Z]/.test(name) ? name.slice("Contact".length) : name;
  const words = bare
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .split(/[\s_]+/)
    .filter((word) => word.length > 0)
    // Keep acronyms (ID, URL) as they are.
    .map((word, index) =>
      index === 0 || /^[A-Z0-9]{2,}$/.test(word) ? word : word.toLowerCase(),
    );

  return words.length > 0 ? words.join(" ") : name;
}

/** A stored job as `GET_JOB` returns it. */
export interface JobView {
  readonly job: JobDto;
  readonly status: StatusDto;
  readonly progress: ProgressDto;
  readonly reports: ReportsDto;
}

/** `JobView` of a command response; `null` when the response holds no job. */
export function toJobView(
  response: ImportJobResponse | undefined,
): JobView | null {
  return response?.job &&
    response.status &&
    response.progress &&
    response.reports
    ? {
        job: response.job,
        status: response.status,
        progress: response.progress,
        reports: response.reports,
      }
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
export function progressFor(
  phase: ProcessPhase,
  progress: ProgressDto,
): ProgressDto {
  return progress.phase === phase
    ? progress
    : { ...progress, phase, rowsProcessed: 0, aggregates: NO_COUNTS };
}
