import { Commands } from "../shared/commands";
import type {
  ExecuteCommand,
  RunningImportDto,
  RunningImportResponse,
} from "../shared/commands";
import type { Message } from "../shared/text";

/** How often the Review and Run steps ask whether another import's Run is unfinished (PLAN D60). */
export const RUNNING_IMPORT_POLL_MS = 10_000;

/** What the server said about the other imports' Runs (`GET_RUNNING_IMPORT`). */
export interface OtherRuns {
  /** The other import whose Run was confirmed and has not ended; `null` when none. */
  readonly blocker: RunningImportDto | null;
  /** When the newest Run of another import ended; `null` when none did. */
  readonly lastRunEndedUtc: string | null;
}

/** Headline of the notice about the other import: it runs, it is paused, or it is confirmed and not started. */
export function runningImportHeadline(running: RunningImportDto): Message {
  if (running.state === "Running") {
    return { key: "contactsimporter.review.otherrunning.headline" };
  }

  return running.state === "Paused"
    ? { key: "contactsimporter.review.otherrunning.headlinepaused" }
    : { key: "contactsimporter.review.otherrunning.headlineconfirmed" };
}

/**
 * The lines that describe the other import whose Run is unfinished: the file, who started it,
 * when, how far it is, and its state. Parts the server does not know are left out.
 */
export function runningImportLines(
  running: RunningImportDto,
  dateTime: (isoUtc: string) => string,
): Message[] {
  const lines: Message[] = [
    {
      key: "contactsimporter.review.otherrunning.file",
      params: { fileName: running.fileName },
    },
  ];
  if (running.userName) {
    lines.push({
      key: "contactsimporter.review.otherrunning.user",
      params: { user: running.userName },
    });
  }

  if (running.state === "Confirmed") {
    if (running.confirmedUtc) {
      lines.push({
        key: "contactsimporter.review.otherrunning.confirmed",
        params: { time: dateTime(running.confirmedUtc) },
      });
    }

    lines.push({ key: "contactsimporter.review.otherrunning.starting" });

    return lines;
  }

  if (running.startedUtc) {
    lines.push({
      key: "contactsimporter.review.otherrunning.started",
      params: { time: dateTime(running.startedUtc) },
    });
  }

  if (running.rowsTotal > 0) {
    lines.push({
      key: "contactsimporter.review.otherrunning.progress",
      params: { done: running.rowsProcessed, total: running.rowsTotal },
    });
  }

  if (running.state === "Paused") {
    if (running.lastProgressUtc) {
      lines.push({
        key: "contactsimporter.review.otherrunning.lastprogress",
        params: { time: dateTime(running.lastProgressUtc) },
      });
    }

    lines.push({ key: "contactsimporter.review.otherrunning.paused" });
  }

  return lines;
}

/**
 * Whether another import's Run ended after the check was made, so the expected counts may differ
 * from what the import does (that import may have changed the same contacts).
 */
export function isCheckOlderThanOtherRun(
  analyzedUtc: string | null | undefined,
  lastRunEndedUtc: string | null | undefined,
): boolean {
  if (!analyzedUtc || !lastRunEndedUtc) {
    return false;
  }

  const checked = Date.parse(analyzedUtc);
  const ended = Date.parse(lastRunEndedUtc);

  return Number.isFinite(checked) && Number.isFinite(ended) && ended > checked;
}

export interface RunningImportWatch {
  /** Checks now (for example after the server refused the confirmation) and restarts the interval. */
  readonly refresh: () => void;
  readonly stop: () => void;
}

/**
 * Asks the server for another import's unfinished Run now and then every `intervalMs` until
 * stopped, and reports each answer. A failed request keeps the last answer: the server refuses the
 * confirmation and the start anyway while another Run is unfinished.
 */
export function watchRunningImport(
  execute: ExecuteCommand,
  onChange: (runs: OtherRuns) => void,
  intervalMs: number = RUNNING_IMPORT_POLL_MS,
): RunningImportWatch {
  let stopped = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let controller: AbortController | undefined;

  const check = async () => {
    clearTimeout(timer);
    controller?.abort();
    const current = new AbortController();
    controller = current;
    let response: RunningImportResponse | undefined;
    try {
      response = await execute<RunningImportResponse>(
        Commands.GetRunningImport,
        undefined,
        current.signal,
      );
    } catch {
      response = undefined;
    }

    if (stopped || current.signal.aborted) {
      return;
    }

    if (response?.code === "ok") {
      onChange({
        blocker: response.import ?? null,
        lastRunEndedUtc: response.lastRunEndedUtc ?? null,
      });
    }

    timer = setTimeout(() => void check(), intervalMs);
  };

  void check();

  return {
    refresh: () => {
      if (!stopped) {
        void check();
      }
    },
    stop: () => {
      stopped = true;
      clearTimeout(timer);
      controller?.abort();
    },
  };
}
