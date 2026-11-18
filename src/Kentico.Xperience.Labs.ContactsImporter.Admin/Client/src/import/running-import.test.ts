import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type {
  ExecuteCommand,
  RunningImportDto,
  RunningImportResponse,
} from "../shared/commands";

import {
  RUNNING_IMPORT_POLL_MS,
  isCheckOlderThanOtherRun,
  runningImportHeadline,
  runningImportLines,
  watchRunningImport,
} from "./running-import";
import type { OtherRuns } from "./running-import";

const running: RunningImportDto = {
  jobId: "job-2",
  fileName: "other.csv",
  userName: "mbrown",
  state: "Running",
  startedUtc: "2026-11-16T12:00:00Z",
  rowsProcessed: 40,
  rowsTotal: 100,
};

const time = (iso: string) => `at ${iso}`;

describe("runningImportLines", () => {
  it("names the file, the user, the start and the progress", () => {
    expect(runningImportLines(running, time)).toEqual([
      {
        key: "contactsimporter.review.otherrunning.file",
        params: { fileName: "other.csv" },
      },
      {
        key: "contactsimporter.review.otherrunning.user",
        params: { user: "mbrown" },
      },
      {
        key: "contactsimporter.review.otherrunning.started",
        params: { time: "at 2026-11-16T12:00:00Z" },
      },
      {
        key: "contactsimporter.review.otherrunning.progress",
        params: { done: 40, total: 100 },
      },
    ]);
  });

  it("leaves out what the server does not know", () => {
    expect(
      runningImportLines(
        { ...running, userName: "", startedUtc: null, rowsTotal: 0 },
        time,
      ),
    ).toEqual([
      {
        key: "contactsimporter.review.otherrunning.file",
        params: { fileName: "other.csv" },
      },
    ]);
  });

  it("says when a confirmed import was confirmed and that it has not started", () => {
    expect(
      runningImportLines(
        {
          ...running,
          state: "Confirmed",
          startedUtc: null,
          confirmedUtc: "2026-11-16T11:00:00Z",
          rowsProcessed: 0,
        },
        time,
      ),
    ).toEqual([
      {
        key: "contactsimporter.review.otherrunning.file",
        params: { fileName: "other.csv" },
      },
      {
        key: "contactsimporter.review.otherrunning.user",
        params: { user: "mbrown" },
      },
      {
        key: "contactsimporter.review.otherrunning.confirmed",
        params: { time: "at 2026-11-16T11:00:00Z" },
      },
      { key: "contactsimporter.review.otherrunning.starting" },
    ]);
  });

  it("says that a paused import is not finished and when it last made progress", () => {
    expect(
      runningImportLines(
        {
          ...running,
          state: "Paused",
          lastProgressUtc: "2026-11-16T12:30:00Z",
        },
        time,
      ),
    ).toEqual([
      {
        key: "contactsimporter.review.otherrunning.file",
        params: { fileName: "other.csv" },
      },
      {
        key: "contactsimporter.review.otherrunning.user",
        params: { user: "mbrown" },
      },
      {
        key: "contactsimporter.review.otherrunning.started",
        params: { time: "at 2026-11-16T12:00:00Z" },
      },
      {
        key: "contactsimporter.review.otherrunning.progress",
        params: { done: 40, total: 100 },
      },
      {
        key: "contactsimporter.review.otherrunning.lastprogress",
        params: { time: "at 2026-11-16T12:30:00Z" },
      },
      { key: "contactsimporter.review.otherrunning.paused" },
    ]);
  });
});

describe("runningImportHeadline", () => {
  it("says whether the other import runs, is paused or is confirmed", () => {
    expect(runningImportHeadline(running).key).toBe(
      "contactsimporter.review.otherrunning.headline",
    );
    expect(runningImportHeadline({ ...running, state: "Paused" }).key).toBe(
      "contactsimporter.review.otherrunning.headlinepaused",
    );
    expect(runningImportHeadline({ ...running, state: "Confirmed" }).key).toBe(
      "contactsimporter.review.otherrunning.headlineconfirmed",
    );
  });
});

describe("isCheckOlderThanOtherRun", () => {
  it("is true only when another Run ended after the check", () => {
    expect(
      isCheckOlderThanOtherRun("2026-11-16T12:00:00Z", "2026-11-16T12:00:01Z"),
    ).toBe(true);
    expect(
      isCheckOlderThanOtherRun("2026-11-16T12:00:00Z", "2026-11-16T12:00:00Z"),
    ).toBe(false);
    expect(
      isCheckOlderThanOtherRun("2026-11-16T12:00:00Z", "2026-11-16T11:00:00Z"),
    ).toBe(false);
    expect(isCheckOlderThanOtherRun(null, "2026-11-16T12:00:00Z")).toBe(false);
    expect(isCheckOlderThanOtherRun("2026-11-16T12:00:00Z", null)).toBe(false);
    expect(isCheckOlderThanOtherRun("not a date", "2026-11-16T12:00:00Z")).toBe(
      false,
    );
  });
});

describe("watchRunningImport", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function server(answers: (RunningImportResponse | undefined | Error)[]) {
    const calls: string[] = [];
    const execute = vi.fn((name: string) => {
      calls.push(name);
      const answer = answers.shift();

      return answer instanceof Error
        ? Promise.reject(answer)
        : Promise.resolve(answer);
    }) as unknown as ExecuteCommand;

    return { execute, calls };
  }

  const ok = (
    dto: RunningImportDto | null,
    lastRunEndedUtc: string | null = null,
  ): RunningImportResponse => ({
    code: "ok",
    message: null,
    import: dto,
    lastRunEndedUtc,
  });
  const runs = (
    blocker: RunningImportDto | null,
    lastRunEndedUtc: string | null = null,
  ): OtherRuns => ({ blocker, lastRunEndedUtc });

  it("checks at once and then every interval until stopped", async () => {
    const { execute, calls } = server([
      ok(running),
      ok(null, "2026-11-16T13:00:00Z"),
      ok(null),
    ]);
    const changes: OtherRuns[] = [];

    const watch = watchRunningImport(execute, (value) => changes.push(value));
    await vi.advanceTimersByTimeAsync(0);
    expect(changes).toEqual([runs(running)]);

    await vi.advanceTimersByTimeAsync(RUNNING_IMPORT_POLL_MS);
    expect(changes).toEqual([
      runs(running),
      runs(null, "2026-11-16T13:00:00Z"),
    ]);
    expect(calls).toEqual(["GET_RUNNING_IMPORT", "GET_RUNNING_IMPORT"]);

    watch.stop();
    await vi.advanceTimersByTimeAsync(RUNNING_IMPORT_POLL_MS * 3);
    expect(calls).toHaveLength(2);
  });

  it("keeps the last answer when a check fails", async () => {
    const { execute } = server([
      ok(running),
      undefined,
      new Error("network"),
      { code: "invalidRequest", message: "x", import: null },
    ]);
    const changes: OtherRuns[] = [];

    const watch = watchRunningImport(
      execute,
      (value) => changes.push(value),
      1000,
    );
    await vi.advanceTimersByTimeAsync(3500);
    watch.stop();

    expect(changes).toEqual([runs(running)]);
  });

  it("checks again at once on refresh", async () => {
    const { execute, calls } = server([ok(null), ok(running)]);
    const changes: OtherRuns[] = [];

    const watch = watchRunningImport(execute, (value) => changes.push(value));
    await vi.advanceTimersByTimeAsync(0);
    watch.refresh();
    await vi.advanceTimersByTimeAsync(0);
    watch.stop();

    expect(calls).toHaveLength(2);
    expect(changes).toEqual([runs(null), runs(running)]);
  });
});
