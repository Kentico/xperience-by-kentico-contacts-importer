import { useCallback, useEffect, useRef, useState } from 'react';

import { ProcessLoop, storageTokenStore } from '../process/process-loop';
import type { LoopSnapshot } from '../process/process-loop';
import { acquireTabLock } from '../process/tab-lock';
import type { TabLock } from '../process/tab-lock';
import { Commands } from '../shared/commands';
import type { ExecuteCommand, ProcessNextRequest, ProcessPhase, ProgressDto } from '../shared/commands';
import { RateEstimator } from '../upload/progress';
import type { RateEstimate } from '../upload/progress';

export interface ProcessLoopView {
  readonly snapshot: LoopSnapshot;
  /** Rows per second and time left, from a moving window of answers. */
  readonly rate: RateEstimate;
  /** Another tab of this browser is processing the import. */
  readonly lockedByOtherTab: boolean;
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
  /** Start as soon as the step shows. */
  readonly autoStart: boolean;
}

const IDLE_RATE: RateEstimate = { ratePerSecond: 0, etaSeconds: null };

/**
 * Thin React wrapper of `ProcessLoop`: one loop per mounted step, disposed on unmount. The driver
 * token is kept in session storage, so a reload of the tab continues as the same driver.
 */
export function useProcessLoop({ jobId, phase, execute, initialProgress, autoStart }: UseProcessLoopOptions): ProcessLoopView {
  const loopRef = useRef<ProcessLoop | null>(null);
  const lockRef = useRef<TabLock | null>(null);
  const estimatorRef = useRef(new RateEstimator(60_000));
  const initialRef = useRef({ initialProgress, autoStart });
  const [snapshot, setSnapshot] = useState<LoopSnapshot>(() => ({
    status: 'idle',
    phase,
    progress: initialProgress,
    pauseReason: null,
    finishReason: null,
    retry: null,
    error: null,
    calls: 0,
    processedCalls: 0,
    lastAnswerAtMs: null,
  }));
  const [rate, setRate] = useState<RateEstimate>(IDLE_RATE);
  const [lockedByOtherTab, setLockedByOtherTab] = useState(false);

  const start = useCallback(
    (takeOver = false) => {
      void (async () => {
        const loop = loopRef.current;
        if (!loop) {
          return;
        }

        if (!lockRef.current) {
          const lock = await acquireTabLock(`contacts-importer:${jobId}`);
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
        loop.start({ takeOver });
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
      tokens: storageTokenStore(sessionStorageOrUndefined(), `contacts-importer:driver:${jobId}`),
      call: (request, signal) => execute<ProgressDto, ProcessNextRequest>(Commands.ProcessNext, request, signal),
      onChange: (next) => {
        if (next.calls !== calls && next.progress) {
          calls = next.calls;
          estimator.add(Date.now(), next.progress.rowsProcessed);
          setRate(estimator.estimate(next.progress.rowsTotal));
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
      loop.dispose();
      lockRef.current?.release();
      lockRef.current = null;
    };
  }, [execute, jobId, phase, start]);

  const pause = useCallback(() => loopRef.current?.pause(), []);

  return { snapshot, rate, lockedByOtherTab, start, pause };
}

function sessionStorageOrUndefined(): Storage | undefined {
  try {
    return window.sessionStorage;
  } catch {
    return undefined;
  }
}
