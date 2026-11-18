import { useCallback, useEffect, useRef, useState } from 'react';

import { ProcessLoop, initialSnapshot, storageTokenStore } from '../process/process-loop';
import type { LoopSnapshot } from '../process/process-loop';
import { acquireTabLock } from '../process/tab-lock';
import type { TabLock } from '../process/tab-lock';
import { Commands } from '../shared/commands';
import type { ExecuteCommand, ImportActionResponse, PauseProcessingRequest, ProcessNextRequest, ProcessPhase, ProgressDto } from '../shared/commands';
import { RateEstimator } from '../upload/progress';
import type { RateEstimate } from '../upload/progress';

export interface ProcessLoopView {
  readonly snapshot: LoopSnapshot;
  /** Rows per second and time left, from a moving window of answers. */
  readonly rate: RateEstimate;
  /** Another tab of this browser is processing the import. */
  readonly lockedByOtherTab: boolean;
  /** The user chose "Continue here": waiting until the other tab or user stops (`pause` stops waiting). */
  readonly waitingForOther: boolean;
  /** Starts or continues; `takeOver` after the user chose to continue an import left elsewhere. */
  readonly start: (takeOver?: boolean) => void;
  /** Lets the call in flight finish and sends no further one. */
  readonly pause: () => void;
}

export interface UseProcessLoopOptions {
  readonly jobId: string;
  readonly phase: ProcessPhase;
  readonly execute: ExecuteCommand;
  readonly initialProgress: ProgressDto | null;
  /** Start as soon as the step shows (not when the import was paused with Pause, see pausedByUser). */
  readonly autoStart: boolean;
  /**
   * A user paused the import with Pause (GET_JOB status pausedByUser): it stays paused until the user selects
   * Continue, which takes the import over when another tab or user paused it.
   */
  readonly pausedByUser?: boolean;
}

const IDLE_RATE: RateEstimate = { ratePerSecond: 0, etaSeconds: null };

/**
 * Thin React wrapper of `ProcessLoop`: one loop per mounted step, disposed on unmount. The driver
 * token is kept in session storage, so a reload of the tab continues as the same driver.
 */
export function useProcessLoop({ jobId, phase, execute, initialProgress, autoStart, pausedByUser = false }: UseProcessLoopOptions): ProcessLoopView {
  const loopRef = useRef<ProcessLoop | null>(null);
  const lockRef = useRef<TabLock | null>(null);
  const estimatorRef = useRef(new RateEstimator(60_000));
  const initialRef = useRef({ initialProgress, autoStart: shouldAutoStart(autoStart, pausedByUser), pausedByUser });
  const [snapshot, setSnapshot] = useState<LoopSnapshot>(() => initialSnapshot(phase, initialProgress, pausedByUser));
  const [rate, setRate] = useState<RateEstimate>(IDLE_RATE);
  const [lockedByOtherTab, setLockedByOtherTab] = useState(false);
  const [waitingForLock, setWaitingForLock] = useState(false);
  const lockWaitRef = useRef<AbortController | null>(null);

  const start = useCallback(
    (takeOver = false) => {
      void (async () => {
        const loop = loopRef.current;
        if (!loop) {
          return;
        }

        // Continue of an import a user paused before this page loaded takes it over (ProcessLoop.startTakesOver).
        const takesOver = takeOver || loop.startTakesOver;
        if (!lockRef.current) {
          const name = `contacts-importer:${jobId}`;
          let lock = await acquireTabLock(name);
          // "Continue here" while another tab holds the lock: wait for it to let go.
          if (!lock && takesOver && loopRef.current === loop && !lockWaitRef.current) {
            const waiting = new AbortController();
            lockWaitRef.current = waiting;
            setLockedByOtherTab(false);
            setWaitingForLock(true);
            lock = await acquireTabLock(name, undefined, waiting.signal);
            if (lockWaitRef.current === waiting) {
              lockWaitRef.current = null;
            }
            setWaitingForLock(false);
          }
          if (loopRef.current !== loop) {
            lock?.release();

            return;
          }

          lockRef.current = lock;
          setLockedByOtherTab(lock === null);
          if (!lock) {
            return;
          }
        }

        // First sample of the rate: what was done when this run started.
        const estimator = estimatorRef.current;
        estimator.reset();
        estimator.add(Date.now(), loop.snapshot.progress?.rowsProcessed ?? 0);
        loop.start({ takeOver: takesOver });
      })();
    },
    [jobId],
  );

  useEffect(() => {
    const estimator = estimatorRef.current;
    estimator.reset();
    let calls = 0;
    const loop = new ProcessLoop({
      phase,
      initialProgress: initialRef.current.initialProgress,
      pausedByUser: initialRef.current.pausedByUser,
      tokens: driverTokenStore(jobId),
      call: (request, signal) => execute<ProgressDto, ProcessNextRequest>(Commands.ProcessNext, request, signal),
      release: (driverToken) => execute<ImportActionResponse, PauseProcessingRequest>(Commands.PauseProcessing, { driverToken }),
      onChange: (next) => {
        if (next.calls !== calls && next.progress) {
          calls = next.calls;
          estimator.add(Date.now(), next.progress.rowsProcessed);
          setRate(estimator.estimate(next.progress.rowsTotal));
        }
        // A paused or finished tab no longer processes: another tab of this browser may continue the import.
        if ((next.status === 'paused' || next.status === 'finished') && loopRef.current === loop) {
          lockRef.current?.release();
          lockRef.current = null;
        }

        setSnapshot(next);
      },
    });
    loopRef.current = loop;
    if (initialRef.current.autoStart) {
      start();
    }

    return () => {
      loopRef.current = null;
      lockWaitRef.current?.abort();
      lockWaitRef.current = null;
      loop.dispose();
      lockRef.current?.release();
      lockRef.current = null;
    };
  }, [execute, jobId, phase, start]);

  const pause = useCallback(() => {
    lockWaitRef.current?.abort();
    loopRef.current?.pause();
  }, []);

  return { snapshot, rate, lockedByOtherTab, waitingForOther: waitingForLock || snapshot.waitingForOther, start, pause };
}

/** Whether the step starts processing as soon as it shows: never after a Pause (the user selects Continue). */
export function shouldAutoStart(autoStart: boolean, pausedByUser: boolean): boolean {
  return autoStart && !pausedByUser;
}

/** Driver token of this tab for the import, kept in session storage (survives a reload of the tab). */
function driverTokenStore(jobId: string) {
  return storageTokenStore(sessionStorageOrUndefined(), `contacts-importer:driver:${jobId}`);
}

/** The token this tab drives the import with, or `null`; `DELETE_IMPORT` sends it so the tab may delete its own check. */
export function currentDriverToken(jobId: string): string | null {
  return driverTokenStore(jobId).get();
}

function sessionStorageOrUndefined(): Storage | undefined {
  try {
    return window.sessionStorage;
  } catch {
    return undefined;
  }
}
