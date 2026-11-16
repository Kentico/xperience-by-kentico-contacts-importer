/**
 * Retry with exponential backoff for calls the browser drives (chunk uploads now, the
 * `PROCESS_NEXT` loop later). Only failed requests are retried; an answer from the server,
 * good or bad, is returned to the caller.
 */
export interface RetryOptions {
  /** Attempts in total, the first one included. Default 6. */
  maxAttempts?: number;
  /** Delay before the second attempt in ms; doubles with every attempt. Default 1000. */
  baseDelayMs?: number;
  /** Longest delay in ms. Default 30000. */
  maxDelayMs?: number;
  /** Stops waiting and retrying; the call rejects with the signal's abort reason. */
  signal?: AbortSignal;
  /** Called before each wait. `attempt` is the number of the attempt that failed. */
  onRetry?: (info: RetryInfo) => void;
  /** Test seams. */
  sleep?: (ms: number, signal?: AbortSignal) => Promise<void>;
  random?: () => number;
}

export interface RetryInfo {
  attempt: number;
  maxAttempts: number;
  delayMs: number;
}

export const DEFAULT_RETRY = { maxAttempts: 6, baseDelayMs: 1000, maxDelayMs: 30_000 } as const;

/** Thrown when every attempt failed. */
export class RetryExhaustedError extends Error {
  readonly attempts: number;

  constructor(attempts: number) {
    super(`The request failed ${attempts} times.`);
    this.name = 'RetryExhaustedError';
    this.attempts = attempts;
  }
}

/** Delay before the attempt after `attempt` failed: base * 2^(attempt-1), capped, with +/-25 % jitter. */
export function backoffDelay(
  attempt: number,
  baseDelayMs: number,
  maxDelayMs: number,
  random: () => number = Math.random,
): number {
  const exponential = Math.min(maxDelayMs, baseDelayMs * 2 ** Math.max(0, attempt - 1));
  const jitter = 0.75 + random() * 0.5;

  return Math.round(Math.min(maxDelayMs, exponential * jitter));
}

/** Resolves after `ms`; rejects with the abort reason when the signal fires first. */
export function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason);

      return;
    }

    const onAbort = () => {
      clearTimeout(timer);
      reject(signal?.reason);
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

/**
 * Runs `attempt` until it returns a value. `undefined` or a thrown error counts as a failed
 * request (that is how the admin's `executeCommand` reports network and server errors).
 * Rejects with `RetryExhaustedError` after `maxAttempts`, or with the abort reason.
 */
export async function withRetry<T>(
  attempt: (attemptNumber: number) => Promise<T | undefined>,
  options: RetryOptions = {},
): Promise<T> {
  const maxAttempts = Math.max(1, options.maxAttempts ?? DEFAULT_RETRY.maxAttempts);
  const baseDelayMs = options.baseDelayMs ?? DEFAULT_RETRY.baseDelayMs;
  const maxDelayMs = options.maxDelayMs ?? DEFAULT_RETRY.maxDelayMs;
  const wait = options.sleep ?? sleep;
  const { signal } = options;

  for (let number = 1; ; number++) {
    signal?.throwIfAborted();

    let result: T | undefined;
    try {
      result = await attempt(number);
    } catch {
      result = undefined;
    }
    signal?.throwIfAborted();

    if (result !== undefined) {
      return result;
    }
    if (number >= maxAttempts) {
      throw new RetryExhaustedError(number);
    }

    const delayMs = backoffDelay(number, baseDelayMs, maxDelayMs, options.random);
    options.onRetry?.({ attempt: number, maxAttempts, delayMs });
    await wait(delayMs, signal);
  }
}
