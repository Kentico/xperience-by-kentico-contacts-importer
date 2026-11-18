import type { JobState, ProcessNextRequest, ProcessPhase, ProgressDto } from '../shared/commands';
import { RetryExhaustedError, backoffDelay, sleep as defaultSleep, withRetry } from '../upload/retry';

/**
 * Driver of the `PROCESS_NEXT` loop (PLAN D11): the browser asks the server to do the next piece
 * of the analysis or the import until the phase is finished. Framework-free; the React steps
 * only render its snapshots.
 *
 * Rules:
 * - One call at a time. The next call is sent only after the previous one was answered or failed.
 * - A failed request (network, server error) is retried with backoff; an answered call is never repeated.
 * - `pause()` lets the call in flight finish (the server stops at its next batch boundary anyway)
 *   and sends no further call. Only `dispose()` aborts a request. Then `release` tells the server
 *   (`PAUSE_PROCESSING`), so the import shows as paused at once for everyone. The server keeps that pause
 *   (`pausedByUser`): a loop made for such an import starts paused and continues only on `start()`.
 * - The driver token is kept in a `TokenStore`, so a reloaded tab continues as the same driver. The
 *   loop makes the token and stores it before its first call (and before a take over); the server
 *   adopts it, so a reload while that call runs does not lose it.
 * - "Continue here" (take over) while another driver is still active waits and asks again until the
 *   other driver stopped long enough, instead of failing at once.
 */

export type LoopStatus = 'idle' | 'running' | 'pausing' | 'paused' | 'finished';

/** Why the loop waits for the user. */
export type PauseReason =
  /** The user paused. */
  | 'user'
  /** The server could not be reached after all retries. */
  | 'network'
  /** The server kept reporting a temporary problem. */
  | 'server'
  /** Another tab or user is processing the import right now. */
  | 'activeElsewhere'
  /** Another tab or user started processing and went away; "Continue import" takes over. */
  | 'stale'
  /** Another tab or user took the import over while this tab was processing it. */
  | 'takenOver'
  /**
   * The Run could not start: another import's Run was confirmed and has not ended (PLAN D60).
   * Nothing changed; "Continue" starts it once the other import is finished or cancelled.
   */
  | 'otherImportRunning';

/** Why the loop ended; nothing more is sent. */
export type FinishReason =
  /** Analysis done (job Analyzed) or import done (job Completed). */
  | 'phaseFinished'
  | 'failed'
  /**
   * The job configuration does not fit the contact fields (found before anything was written).
   * The job is back at Uploaded with the uploaded data kept: the options or mapping must change.
   */
  | 'configError'
  | 'cancelled'
  /** The analysis is too old to import from; the job is back at Uploaded. */
  | 'analysisExpired'
  /** The job is in a state this phase cannot run from (changed elsewhere): reload it. */
  | 'invalidState'
  | 'jobNotFound';

export interface LoopRetry {
  /** `network`: the request failed. `server`: the server answered with a temporary problem. */
  kind: 'network' | 'server';
  /** Number of the attempt that failed. */
  attempt: number;
  maxAttempts: number;
  delayMs: number;
}

export interface LoopSnapshot {
  status: LoopStatus;
  phase: ProcessPhase;
  /** Last progress the server reported (counts come from the server, never from the client). */
  progress: ProgressDto | null;
  pauseReason: PauseReason | null;
  finishReason: FinishReason | null;
  /** Set while the loop waits before trying again. */
  retry: LoopRetry | null;
  /** Problem description from the server (failed job, temporary error). */
  error: string | null;
  /** Calls the server answered since the loop was created. */
  calls: number;
  /** Calls answered with progress (`processed`): above 0 means this tab drives the job. */
  processedCalls: number;
  /** Time of the last answer (`now()`), `null` before the first one. */
  lastAnswerAtMs: number | null;
  /** The user chose "Continue here"; the loop waits until the other tab or user stops. */
  waitingForOther: boolean;
}

/** Keeps the driver token across page reloads (session storage in the browser). */
export interface TokenStore {
  get(): string | null;
  set(token: string | null): void;
}

export interface BackoffOptions {
  maxAttempts: number;
  baseDelayMs: number;
  maxDelayMs: number;
}

export interface ProcessLoopOptions {
  phase: ProcessPhase;
  /**
   * Sends one `PROCESS_NEXT`. Resolves with the server's answer, or `undefined` / rejects when the
   * request failed. `signal` aborts only when the loop is disposed.
   */
  call: (request: ProcessNextRequest, signal: AbortSignal) => Promise<ProgressDto | undefined>;
  tokens: TokenStore;
  /**
   * Sends `PAUSE_PROCESSING` after the user paused (the call in flight was answered), so the server shows the import
   * as paused at once instead of after the heartbeat age. Optional; failures are ignored.
   */
  release?: (driverToken: string) => Promise<unknown>;
  onChange: (snapshot: LoopSnapshot) => void;
  /** Progress known before the first call (from `GET_JOB`). */
  initialProgress?: ProgressDto | null;
  /**
   * A user paused the import with Pause before this loop was made (`GET_JOB` `pausedByUser`: reload, other tab or user).
   * The loop starts as paused by the user and waits for Continue; that first start takes the import over, because the
   * stored token may not be the driver's (the server accepts a take over of a paused import at once).
   */
  pausedByUser?: boolean;
  /** Retry of failed requests. Default 6 attempts, 1 s doubling up to 30 s. */
  network?: Partial<BackoffOptions>;
  /** Retry after `transientError` / `busy` answers. Default 8 attempts, 2 s doubling up to 30 s. */
  server?: Partial<BackoffOptions>;
  /** Pause between two successful calls, to throttle the load on the server. Default 0. */
  delayBetweenCallsMs?: number;
  /** Wait between two take over attempts while another driver is active. Default 5 s. */
  takeOverPollMs?: number;
  /** Test seams. */
  newToken?: () => string;
  now?: () => number;
  sleep?: (ms: number, signal?: AbortSignal) => Promise<void>;
  random?: () => number;
}

export const DEFAULT_NETWORK_RETRY: BackoffOptions = { maxAttempts: 6, baseDelayMs: 1000, maxDelayMs: 30_000 };
export const DEFAULT_SERVER_RETRY: BackoffOptions = { maxAttempts: 8, baseDelayMs: 2000, maxDelayMs: 30_000 };
export const DEFAULT_TAKE_OVER_POLL_MS = 5000;

/** New driver token: a GUID without dashes (the server accepts only that form). */
export function newDriverToken(): string {
  const cryptoApi = globalThis.crypto;
  if (typeof cryptoApi?.randomUUID === 'function') {
    return cryptoApi.randomUUID().replace(/-/g, '');
  }

  const bytes = new Uint8Array(16);
  cryptoApi.getRandomValues(bytes);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;

  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

export class ProcessLoop {
  private readonly options: ProcessLoopOptions;
  private readonly network: BackoffOptions;
  private readonly server: BackoffOptions;
  private readonly now: () => number;
  private readonly sleep: (ms: number, signal?: AbortSignal) => Promise<void>;
  /** Aborts requests; fires only on dispose. */
  private readonly hard = new AbortController();
  /** Aborts waits (backoff, throttle); fires on pause and dispose. */
  private waits = new AbortController();
  private active: Promise<void> | null = null;
  private pauseRequested = false;
  private disposed = false;
  /** Paused by a user before this loop was made: the first start takes the import over. */
  private resumeTakesOver: boolean;
  private current: LoopSnapshot;

  constructor(options: ProcessLoopOptions) {
    this.options = options;
    this.network = { ...DEFAULT_NETWORK_RETRY, ...options.network };
    this.server = { ...DEFAULT_SERVER_RETRY, ...options.server };
    this.now = options.now ?? Date.now;
    this.sleep = options.sleep ?? defaultSleep;
    this.resumeTakesOver = options.pausedByUser ?? false;
    this.current = initialSnapshot(options.phase, options.initialProgress ?? null, this.resumeTakesOver);
  }

  get snapshot(): LoopSnapshot {
    return this.current;
  }

  /** The next `start()` takes the import over (it was paused by a user before this loop was made). */
  get startTakesOver(): boolean {
    return this.resumeTakesOver;
  }

  /** A call is in flight or about to be sent. */
  get isActive(): boolean {
    return this.active !== null;
  }

  /**
   * Starts or continues the loop. Returns `false` (and does nothing) while a run is active, after
   * the phase finished, or after `dispose()`.
   * @param takeOver The user chose to continue an import another tab or user left.
   */
  start(start: { takeOver?: boolean } = {}): boolean {
    if (this.disposed || this.active || this.current.status === 'finished') {
      return false;
    }

    const takeOver = (start.takeOver ?? false) || this.resumeTakesOver;
    this.resumeTakesOver = false;
    this.pauseRequested = false;
    this.waits = new AbortController();
    this.update({ status: 'running', pauseReason: null, retry: null, error: null, waitingForOther: false });
    const run = this.run(takeOver).finally(() => {
      if (this.active === run) {
        this.active = null;
      }
    });
    this.active = run;

    return true;
  }

  /**
   * Stops after the call in flight: its answer is still applied, no further call is sent.
   * A wait between calls ends at once.
   */
  pause(): void {
    if (!this.active || this.disposed || this.pauseRequested) {
      return;
    }

    this.pauseRequested = true;
    this.update({ status: 'pausing' });
    this.waits.abort();
  }

  /** Resolves when no call is in flight (immediately when the loop is not running). */
  whenIdle(): Promise<void> {
    return this.active ?? Promise.resolve();
  }

  /** Aborts the request in flight and silences the loop (page closed, step left). */
  dispose(): void {
    if (this.disposed) {
      return;
    }

    this.disposed = true;
    this.waits.abort();
    this.hard.abort();
  }

  private async run(initialTakeOver: boolean): Promise<void> {
    const { phase, call, tokens } = this.options;
    let takeOver = initialTakeOver;
    let serverRetries = 0;
    // Stored before the call: a tab reloaded while the call runs continues with it. A take over always
    // gets a new token (the stored one is no longer the driver's).
    if (takeOver || tokens.get() === null) {
      tokens.set((this.options.newToken ?? newDriverToken)());
    }

    for (;;) {
      if (this.disposed) {
        return;
      }
      if (this.pauseRequested) {
        this.pauseFor('user');

        return;
      }

      const request: ProcessNextRequest = { phase, driverToken: tokens.get(), takeOver };
      let answer: ProgressDto | undefined;
      try {
        await withRetry(
          async () => {
            answer = undefined;
            answer = await call(request, this.hard.signal);

            return answer;
          },
          {
            ...this.network,
            signal: this.waits.signal,
            sleep: this.sleep,
            random: this.options.random,
            onRetry: (info) => this.update({ retry: { kind: 'network', ...info } }),
          },
        );
      } catch (error) {
        if (this.disposed) {
          return;
        }
        if (answer === undefined) {
          // Either every attempt failed, or a pause ended the wait before the next attempt.
          this.pauseFor(error instanceof RetryExhaustedError ? 'network' : 'user');

          return;
        }
        // A pause was requested while the call was in flight; its answer is applied below.
      }
      if (this.disposed || answer === undefined) {
        return;
      }

      const result: ProgressDto = answer;
      this.current = { ...this.current, calls: this.current.calls + 1, lastAnswerAtMs: this.now() };

      switch (result.outcome) {
        case 'processed':
          tokens.set(result.driverToken);
          takeOver = false;
          serverRetries = 0;
          this.update({ progress: result, retry: null, error: null, waitingForOther: false, processedCalls: this.current.processedCalls + 1 });
          if ((this.options.delayBetweenCallsMs ?? 0) > 0 && !(await this.wait(this.options.delayBetweenCallsMs ?? 0))) {
            return;
          }
          break;

        case 'transientError':
        case 'busy': {
          if (result.driverToken) {
            tokens.set(result.driverToken);
          }

          serverRetries++;
          if (serverRetries >= this.server.maxAttempts) {
            this.pauseFor('server', result);

            return;
          }

          const delayMs = backoffDelay(serverRetries, this.server.baseDelayMs, this.server.maxDelayMs, this.options.random);
          this.update({
            progress: result,
            error: result.error,
            retry: { kind: 'server', attempt: serverRetries, maxAttempts: this.server.maxAttempts, delayMs },
          });
          if (!(await this.wait(delayMs))) {
            return;
          }
          break;
        }

        case 'phaseFinished':
          tokens.set(null);
          this.finish('phaseFinished', result);

          return;

        case 'activeElsewhere':
          // The user chose to continue here: ask again until the other driver has stopped long enough.
          if (takeOver) {
            this.update({ progress: result, retry: null, error: null, waitingForOther: true });
            if (!(await this.wait(this.options.takeOverPollMs ?? DEFAULT_TAKE_OVER_POLL_MS))) {
              return;
            }
            break;
          }

          this.pauseFor('activeElsewhere', result);

          return;

        case 'staleRequiresTakeOver':
          this.pauseFor('stale', result);

          return;

        case 'otherImportRunning':
          this.pauseFor('otherImportRunning', result);

          return;

        case 'interrupted':
          tokens.set(null);
          this.endInterrupted(result);

          return;

        case 'analysisExpired':
          tokens.set(null);
          this.finish('analysisExpired', result);

          return;

        case 'failed':
          tokens.set(null);
          // Failed with a job that is not Failed: nothing was written, the configuration must be fixed.
          this.finish(result.state === 'Failed' ? 'failed' : 'configError', result);

          return;

        case 'jobNotFound':
          tokens.set(null);
          this.finish('jobNotFound', null);

          return;

        case 'invalidState':
          this.finish('invalidState', result);

          return;

        case 'invalidRequest':
        default:
          this.finish('invalidState', null);

          return;
      }
    }
  }

  /** The job was cancelled, failed or changed by someone else during the call. */
  private endInterrupted(result: ProgressDto): void {
    const state: JobState = result.state;
    if (state === 'Cancelled') {
      this.finish('cancelled', result);
    } else if (state === 'Failed') {
      this.finish('failed', result);
    } else if (state === 'Analyzing' || state === 'Running' || state === 'Paused') {
      this.pauseFor('takenOver', result);
    } else {
      this.finish('invalidState', result);
    }
  }

  /** Waits unless paused or disposed meanwhile; `false` means the run ended. */
  private async wait(ms: number): Promise<boolean> {
    try {
      await this.sleep(ms, this.waits.signal);
    } catch {
      // The wait was cut short by a pause or dispose.
    }
    if (this.disposed) {
      return false;
    }
    if (this.pauseRequested) {
      this.pauseFor('user');

      return false;
    }

    return true;
  }

  private pauseFor(reason: PauseReason, result?: ProgressDto): void {
    if (reason === 'user') {
      this.releaseDriver();
    }

    this.update({
      status: 'paused',
      pauseReason: reason,
      retry: null,
      waitingForOther: false,
      ...(result && { progress: result, error: result.error }),
    });
  }

  /**
   * After a user pause: tells the server, so the import shows as paused at once (not after the heartbeat age).
   * Only when this tab drove the job (an answer with progress); fire and forget, a failure only means the server
   * finds out from the old heartbeat. Sent after the call in flight was answered, never beside it.
   */
  private releaseDriver(): void {
    const token = this.options.tokens.get();
    if (!this.options.release || this.disposed || token === null || this.current.processedCalls === 0) {
      return;
    }

    try {
      void this.options.release(token).catch(() => undefined);
    } catch {
      // Best effort.
    }
  }

  private finish(reason: FinishReason, result: ProgressDto | null): void {
    this.update({
      status: 'finished',
      finishReason: reason,
      pauseReason: null,
      retry: null,
      waitingForOther: false,
      ...(result && { progress: result, error: result.error }),
    });
  }

  private update(change: Partial<LoopSnapshot>): void {
    this.current = { ...this.current, ...change };
    if (!this.disposed) {
      this.options.onChange(this.current);
    }
  }
}

/**
 * Snapshot of a loop before its first start: idle, or paused by the user when a user paused the import with Pause
 * before the loop was made (it waits for Continue).
 */
export function initialSnapshot(phase: ProcessPhase, progress: ProgressDto | null, pausedByUser = false): LoopSnapshot {
  return {
    status: pausedByUser ? 'paused' : 'idle',
    phase,
    progress,
    pauseReason: pausedByUser ? 'user' : null,
    finishReason: null,
    retry: null,
    error: null,
    calls: 0,
    processedCalls: 0,
    lastAnswerAtMs: null,
    waitingForOther: false,
  };
}

/** `TokenStore` over a `Storage` (session storage); storage errors are ignored (the token is then per page load). */
export function storageTokenStore(storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> | undefined, key: string): TokenStore {
  let memory: string | null = null;

  return {
    get() {
      try {
        return storage?.getItem(key) ?? memory;
      } catch {
        return memory;
      }
    },
    set(token) {
      memory = token;
      try {
        if (token === null) {
          storage?.removeItem(key);
        } else {
          storage?.setItem(key, token);
        }
      } catch {
        // Private mode or a full storage: keep the token in memory.
      }
    },
  };
}
