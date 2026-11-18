import { describe, expect, it } from 'vitest';

import type { AggregatesDto, JobState, ProcessNextRequest, ProcessOutcome, ProgressDto } from '../shared/commands';

import { NO_COUNTS } from '../import/model';

import { ProcessLoop, storageTokenStore } from './process-loop';
import type { LoopSnapshot, ProcessLoopOptions, TokenStore } from './process-loop';

const ZERO: AggregatesDto = NO_COUNTS;

function answer(outcome: ProcessOutcome, rows = 0, extra: Partial<ProgressDto> = {}): ProgressDto {
  return {
    outcome,
    phase: 'execute',
    state: 'Running',
    driverToken: outcome === 'processed' ? 'token-1' : null,
    rowsProcessed: rows,
    rowsTotal: 100,
    aggregates: { ...ZERO, rows },
    analysis: null,
    analyzedUtc: null,
    error: null,
    ...extra,
  };
}

type Step = ProgressDto | 'fail' | 'throw' | ((request: ProcessNextRequest) => Promise<ProgressDto | undefined>);

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });

  return { promise, resolve };
}

function memoryTokens(initial: string | null = null): TokenStore & { value: string | null } {
  const store = {
    value: initial,
    get: () => store.value,
    set: (token: string | null) => {
      store.value = token;
    },
  };

  return store;
}

/** Loop over a script of answers. Sleeps resolve at once (or reject when aborted) and are recorded. */
function harness(steps: Step[], options: Partial<ProcessLoopOptions> = {}) {
  const requests: ProcessNextRequest[] = [];
  const snapshots: LoopSnapshot[] = [];
  const sleeps: number[] = [];
  const tokens = memoryTokens();
  let inFlight = 0;
  let maxInFlight = 0;
  let signal: AbortSignal | undefined;
  let made = 0;

  const loop = new ProcessLoop({
    phase: 'execute',
    tokens,
    call: async (request, callSignal) => {
      signal = callSignal;
      requests.push({ ...request });
      const step = steps.shift();
      inFlight++;
      maxInFlight = Math.max(maxInFlight, inFlight);
      try {
        if (step === undefined) {
          throw new Error('The loop sent more calls than the test scripted.');
        }
        if (step === 'fail') {
          return undefined;
        }
        if (step === 'throw') {
          throw new Error('network down');
        }

        return typeof step === 'function' ? await step(request) : step;
      } finally {
        inFlight--;
      }
    },
    onChange: (snapshot) => snapshots.push(snapshot),
    sleep: (ms, sleepSignal) => {
      sleeps.push(ms);

      return sleepSignal?.aborted ? Promise.reject(new Error('aborted')) : Promise.resolve();
    },
    random: () => 0.5,
    now: () => 1000 + requests.length,
    newToken: () => `new-${++made}`,
    ...options,
  });

  return { loop, requests, snapshots, sleeps, tokens, steps, maxInFlight: () => maxInFlight, signal: () => signal };
}

describe('ProcessLoop', () => {
  it('calls until the phase is finished, one call at a time, passing the driver token on', async () => {
    const h = harness([answer('processed', 40), answer('processed', 80), answer('phaseFinished', 100, { state: 'Completed' })]);

    expect(h.loop.start()).toBe(true);
    expect(h.loop.start()).toBe(false);
    await h.loop.whenIdle();

    expect(h.requests).toEqual([
      { phase: 'execute', driverToken: 'new-1', takeOver: false },
      { phase: 'execute', driverToken: 'token-1', takeOver: false },
      { phase: 'execute', driverToken: 'token-1', takeOver: false },
    ]);
    expect(h.maxInFlight()).toBe(1);
    expect(h.loop.snapshot).toMatchObject({ status: 'finished', finishReason: 'phaseFinished', calls: 3, processedCalls: 2, retry: null });
    expect(h.loop.snapshot.progress?.rowsProcessed).toBe(100);
    expect(h.tokens.value).toBeNull();
    expect(h.snapshots.map((s) => s.progress?.rowsProcessed ?? 0)).toEqual([0, 40, 80, 100]);
    expect(h.loop.start()).toBe(false);
  });

  it('retries failed requests with backoff and repeats the same request', async () => {
    const h = harness(['fail', 'throw', answer('processed', 10), answer('phaseFinished', 100)]);

    h.loop.start();
    await h.loop.whenIdle();

    expect(h.requests).toHaveLength(4);
    expect(h.requests[1]).toEqual(h.requests[0]);
    expect(h.requests[2]).toEqual(h.requests[0]);
    expect(h.sleeps).toEqual([1000, 2000]);
    const retries = h.snapshots.filter((s) => s.retry).map((s) => s.retry);
    expect(retries).toEqual([
      { kind: 'network', attempt: 1, maxAttempts: 6, delayMs: 1000 },
      { kind: 'network', attempt: 2, maxAttempts: 6, delayMs: 2000 },
    ]);
    expect(h.loop.snapshot).toMatchObject({ status: 'finished', calls: 2, retry: null });
  });

  it('pauses with the reason "network" when every attempt fails and continues on start', async () => {
    const h = harness(['fail', 'fail', 'fail', answer('phaseFinished', 100)], { network: { maxAttempts: 3 } });

    h.loop.start();
    await h.loop.whenIdle();
    expect(h.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'network', calls: 0 });
    expect(h.requests).toHaveLength(3);

    expect(h.loop.start()).toBe(true);
    await h.loop.whenIdle();
    expect(h.loop.snapshot).toMatchObject({ status: 'finished', finishReason: 'phaseFinished' });
  });

  it('waits and calls again after a temporary server error, keeping the driver token', async () => {
    const h = harness([
      answer('processed', 10),
      answer('transientError', 10, { error: 'temporary' }),
      answer('busy', 10, { error: 'busy' }),
      answer('processed', 20),
      answer('phaseFinished', 100),
    ]);

    h.loop.start();
    await h.loop.whenIdle();

    expect(h.sleeps).toEqual([2000, 4000]);
    expect(h.requests.map((r) => r.driverToken)).toEqual(['new-1', 'token-1', 'token-1', 'token-1', 'token-1']);
    expect(h.snapshots.filter((s) => s.retry?.kind === 'server').map((s) => [s.retry?.attempt, s.error])).toEqual([
      [1, 'temporary'],
      [2, 'busy'],
    ]);
    expect(h.loop.snapshot).toMatchObject({ status: 'finished', error: null });
  });

  it('pauses with the reason "server" when the temporary error does not go away', async () => {
    const h = harness(
      [answer('transientError', 5, { error: 'db' }), answer('transientError', 5, { error: 'db' }), answer('transientError', 5, { error: 'db' })],
      { server: { maxAttempts: 3 } },
    );

    h.loop.start();
    await h.loop.whenIdle();

    expect(h.requests).toHaveLength(3);
    expect(h.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'server', error: 'db' });
  });

  it('resets the temporary error count after a successful call', async () => {
    const steps: Step[] = [];
    for (let i = 0; i < 4; i++) {
      steps.push(answer('transientError', i), answer('processed', i));
    }
    steps.push(answer('phaseFinished', 100));
    const h = harness(steps, { server: { maxAttempts: 2 } });

    h.loop.start();
    await h.loop.whenIdle();

    expect(h.loop.snapshot.status).toBe('finished');
    expect(h.sleeps).toEqual([2000, 2000, 2000, 2000]);
  });

  it('pause lets the call in flight finish, applies its answer and sends nothing more', async () => {
    const gate = deferred<ProgressDto>();
    const h = harness([() => gate.promise, answer('processed', 80)]);

    h.loop.start();
    await Promise.resolve();
    h.loop.pause();
    expect(h.loop.snapshot.status).toBe('pausing');
    expect(h.signal()?.aborted).toBe(false);
    expect(h.loop.start()).toBe(false);

    gate.resolve(answer('processed', 40));
    await h.loop.whenIdle();

    expect(h.requests).toHaveLength(1);
    expect(h.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'user', calls: 1 });
    expect(h.loop.snapshot.progress?.rowsProcessed).toBe(40);
    expect(h.tokens.value).toBe('token-1');

    h.loop.start();
    await h.loop.whenIdle();
    expect(h.requests[1]).toEqual({ phase: 'execute', driverToken: 'token-1', takeOver: false });
  });

  it('after a user pause tells the server once, with the driver token, after the call in flight was answered', async () => {
    const gate = deferred<ProgressDto>();
    const released: string[] = [];
    let answeredBeforeRelease = false;
    const h = harness([answer('processed', 40), () => gate.promise], {
      release: (token) => {
        answeredBeforeRelease = h.loop.snapshot.calls === 2;
        released.push(token);

        return Promise.resolve();
      },
    });

    h.loop.start();
    await new Promise((resolve) => setTimeout(resolve, 0));
    h.loop.pause();
    expect(released).toEqual([]);
    gate.resolve(answer('processed', 80));
    await h.loop.whenIdle();

    expect(released).toEqual(['token-1']);
    expect(answeredBeforeRelease).toBe(true);
    expect(h.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'user' });
  });

  it('does not tell the server about a pause when this tab never drove the job, or for other pause reasons', async () => {
    const released: string[] = [];
    const release = (token: string) => {
      released.push(token);

      return Promise.resolve();
    };
    const neverDrove = harness(['fail', answer('processed', 10)], {
      release,
      sleep: (_ms, signal) =>
        new Promise<void>((_resolve, reject) => {
          signal?.addEventListener('abort', () => reject(new Error('aborted')));
        }),
    });
    neverDrove.loop.start();
    await new Promise((resolve) => setTimeout(resolve, 0));
    neverDrove.loop.pause();
    await neverDrove.loop.whenIdle();

    const stale = harness([answer('processed', 10), answer('activeElsewhere', 10)], { release });
    stale.loop.start();
    await stale.loop.whenIdle();

    expect(neverDrove.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'user' });
    expect(stale.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'activeElsewhere' });
    expect(released).toEqual([]);
  });

  it('a failed pause notice does not change the paused loop', async () => {
    const gate = deferred<ProgressDto>();
    const h = harness([() => gate.promise, answer('processed', 80)], { release: () => Promise.reject(new Error('offline')) });

    h.loop.start();
    await Promise.resolve();
    h.loop.pause();
    gate.resolve(answer('processed', 40));
    await h.loop.whenIdle();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(h.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'user' });
    expect(h.loop.start()).toBe(true);
    await h.loop.whenIdle();
    expect(h.requests[1]).toEqual({ phase: 'execute', driverToken: 'token-1', takeOver: false });
  });

  it('a pause during the last call still reports the finished phase', async () => {
    const gate = deferred<ProgressDto>();
    const h = harness([() => gate.promise]);

    h.loop.start();
    await Promise.resolve();
    h.loop.pause();
    gate.resolve(answer('phaseFinished', 100, { state: 'Completed' }));
    await h.loop.whenIdle();

    expect(h.loop.snapshot).toMatchObject({ status: 'finished', finishReason: 'phaseFinished' });
  });

  it('pause ends a retry wait at once', async () => {
    const wait = deferred<void>();
    const h = harness(['fail', answer('processed', 10)], {
      sleep: (_ms, signal) =>
        new Promise<void>((resolve, reject) => {
          signal?.addEventListener('abort', () => reject(new Error('aborted')));
          void wait.promise.then(resolve);
        }),
    });

    h.loop.start();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(h.loop.snapshot.retry?.kind).toBe('network');
    h.loop.pause();
    await h.loop.whenIdle();

    expect(h.requests).toHaveLength(1);
    expect(h.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'user', retry: null });
  });

  it('asks the user when another tab left the import, and takes over on request with a new token', async () => {
    const h = harness([answer('staleRequiresTakeOver', 30, { state: 'Paused' }), answer('processed', 50, { driverToken: 'new-2' }), answer('phaseFinished', 100)]);

    h.loop.start();
    await h.loop.whenIdle();
    expect(h.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'stale', calls: 1, processedCalls: 0 });
    expect(h.loop.snapshot.progress?.rowsProcessed).toBe(30);

    h.loop.start({ takeOver: true });
    await h.loop.whenIdle();
    expect(h.requests.map((r) => [r.driverToken, r.takeOver])).toEqual([
      ['new-1', false],
      ['new-2', true],
      ['new-2', false],
    ]);
    expect(h.loop.snapshot.status).toBe('finished');
  });

  it('pauses when another import holds the unfinished Run, keeps the token, and starts on Continue', async () => {
    const h = harness([
      answer('otherImportRunning', 0, { state: 'Confirmed', error: "Another import ('other.csv') was started and has not finished." }),
      answer('processed', 50, { driverToken: 'new-1' }),
      answer('phaseFinished', 100),
    ]);

    h.loop.start();
    await h.loop.whenIdle();
    expect(h.loop.snapshot).toMatchObject({
      status: 'paused',
      pauseReason: 'otherImportRunning',
      retry: null,
      calls: 1,
      error: "Another import ('other.csv') was started and has not finished.",
    });
    expect(h.loop.snapshot.progress?.state).toBe('Confirmed');
    expect(h.sleeps).toEqual([]);

    h.loop.start();
    await h.loop.whenIdle();
    expect(h.requests.map((r) => [r.driverToken, r.takeOver])).toEqual([
      ['new-1', false],
      ['new-1', false],
      ['new-1', false],
    ]);
    expect(h.loop.snapshot.status).toBe('finished');
  });

  it('pauses when another tab is active; "Continue here" waits and asks again until it can take over', async () => {
    const h = harness(
      [answer('activeElsewhere', 30), answer('activeElsewhere', 30), answer('activeElsewhere', 35), answer('processed', 50, { driverToken: 'new-2' }), answer('phaseFinished', 100)],
      { takeOverPollMs: 4000 },
    );

    h.loop.start();
    await h.loop.whenIdle();
    expect(h.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'activeElsewhere', waitingForOther: false });

    const waiting: boolean[] = [];
    const seen = h.snapshots.length;
    h.loop.start({ takeOver: true });
    await h.loop.whenIdle();
    waiting.push(...h.snapshots.slice(seen).map((s) => s.waitingForOther));

    expect(h.sleeps).toEqual([4000, 4000]);
    expect(waiting).toContain(true);
    expect(h.requests.map((r) => [r.driverToken, r.takeOver])).toEqual([
      ['new-1', false],
      ['new-2', true],
      ['new-2', true],
      ['new-2', true],
      ['new-2', false],
    ]);
    expect(h.loop.snapshot).toMatchObject({ status: 'finished', waitingForOther: false });
  });

  it('pause stops waiting for another tab', async () => {
    const wait = deferred<void>();
    const h = harness([answer('activeElsewhere', 30), answer('activeElsewhere', 30)], {
      sleep: (_ms, signal) =>
        new Promise<void>((resolve, reject) => {
          signal?.addEventListener('abort', () => reject(new Error('aborted')));
          void wait.promise.then(resolve);
        }),
    });

    h.loop.start();
    await h.loop.whenIdle();
    h.loop.start({ takeOver: true });
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(h.loop.snapshot.waitingForOther).toBe(true);

    h.loop.pause();
    await h.loop.whenIdle();
    expect(h.loop.snapshot).toMatchObject({ status: 'paused', pauseReason: 'user', waitingForOther: false });
    expect(h.requests).toHaveLength(2);
  });

  it('stores a new token before the first call, so a reload during it keeps the driver', async () => {
    const gate = deferred<ProgressDto>();
    const h = harness([() => gate.promise]);

    h.loop.start();
    await Promise.resolve();
    expect(h.tokens.value).toBe('new-1');
    expect(h.requests[0]?.driverToken).toBe('new-1');
    h.loop.dispose();
    gate.resolve(answer('processed', 10));
  });

  it.each<[JobState, Partial<LoopSnapshot>]>([
    ['Cancelled', { status: 'finished', finishReason: 'cancelled' }],
    ['Failed', { status: 'finished', finishReason: 'failed' }],
    ['Running', { status: 'paused', pauseReason: 'takenOver' }],
    ['Uploaded', { status: 'finished', finishReason: 'invalidState' }],
  ])('interrupted with the job %s', async (state, expected) => {
    const h = harness([answer('processed', 10), answer('interrupted', 20, { state })]);

    h.loop.start();
    await h.loop.whenIdle();

    expect(h.loop.snapshot).toMatchObject(expected);
    expect(h.tokens.value).toBeNull();
    expect(h.requests).toHaveLength(2);
  });

  it.each<[ProcessOutcome, string, string | null]>([
    ['failed', 'failed', 'boom'],
    ['analysisExpired', 'analysisExpired', null],
    ['invalidState', 'invalidState', null],
    ['jobNotFound', 'jobNotFound', null],
    ['invalidRequest', 'invalidState', null],
  ])('ends on %s', async (outcome, finishReason, error) => {
    const h = harness([answer('processed', 10), answer(outcome, 10, { error, state: outcome === 'failed' ? 'Failed' : 'Uploaded' })]);

    h.loop.start();
    await h.loop.whenIdle();

    expect(h.loop.snapshot).toMatchObject({ status: 'finished', finishReason, error });
    expect(h.requests).toHaveLength(2);
    expect(h.loop.start()).toBe(false);
  });

  it('reports a configuration problem when the failed call leaves the job at Uploaded', async () => {
    const h = harness([answer('failed', 0, { state: 'Uploaded', error: 'A mapped contact field no longer exists.' })]);

    h.loop.start();
    await h.loop.whenIdle();

    expect(h.loop.snapshot).toMatchObject({ status: 'finished', finishReason: 'configError', error: 'A mapped contact field no longer exists.' });
    expect(h.tokens.value).toBeNull();
  });

  it('ends on an outcome this client does not know', async () => {
    const h = harness([answer('somethingNew' as ProcessOutcome)]);

    h.loop.start();
    await h.loop.whenIdle();

    expect(h.loop.snapshot).toMatchObject({ status: 'finished', finishReason: 'invalidState' });
  });

  it('dispose aborts the request in flight and reports nothing more', async () => {
    const gate = deferred<ProgressDto | undefined>();
    const h = harness([() => gate.promise, answer('processed', 80)]);

    h.loop.start();
    await Promise.resolve();
    const before = h.snapshots.length;
    h.loop.dispose();
    expect(h.signal()?.aborted).toBe(true);
    gate.resolve(answer('processed', 40));
    await h.loop.whenIdle();

    expect(h.requests).toHaveLength(1);
    expect(h.snapshots).toHaveLength(before);
    expect(h.loop.start()).toBe(false);
  });

  it('continues after a reload as the same driver when the token was stored', async () => {
    const h = harness([answer('processed', 60), answer('phaseFinished', 100)]);
    h.tokens.set('stored-token');

    h.loop.start();
    await h.loop.whenIdle();

    expect(h.requests[0]).toEqual({ phase: 'execute', driverToken: 'stored-token', takeOver: false });
  });

  it('waits between calls when a delay is configured', async () => {
    const h = harness([answer('processed', 10), answer('processed', 20), answer('phaseFinished', 100)], { delayBetweenCallsMs: 250 });

    h.loop.start();
    await h.loop.whenIdle();

    expect(h.sleeps).toEqual([250, 250]);
    expect(h.loop.snapshot.status).toBe('finished');
  });

  it('starts from the progress of GET_JOB and records the time of answers', async () => {
    const h = harness([answer('processed', 70)], { initialProgress: answer('processed', 55) });
    expect(h.loop.snapshot).toMatchObject({ status: 'idle', calls: 0, lastAnswerAtMs: null });
    expect(h.loop.snapshot.progress?.rowsProcessed).toBe(55);

    h.steps.push(answer('phaseFinished', 100));
    h.loop.start();
    await h.loop.whenIdle();

    expect(h.loop.snapshot.lastAnswerAtMs).toBe(1002);
  });
});

describe('storageTokenStore', () => {
  it('keeps the token in the storage under its key', () => {
    const items = new Map<string, string>();
    const storage = {
      getItem: (key: string) => items.get(key) ?? null,
      setItem: (key: string, value: string) => void items.set(key, value),
      removeItem: (key: string) => void items.delete(key),
    };
    const store = storageTokenStore(storage, 'driver:job-1');

    expect(store.get()).toBeNull();
    store.set('abc');
    expect(items.get('driver:job-1')).toBe('abc');
    expect(storageTokenStore(storage, 'driver:job-1').get()).toBe('abc');
    store.set(null);
    expect(items.has('driver:job-1')).toBe(false);
    expect(store.get()).toBeNull();
  });

  it('falls back to memory when the storage throws or is missing', () => {
    const broken = {
      getItem: () => {
        throw new Error('denied');
      },
      setItem: () => {
        throw new Error('denied');
      },
      removeItem: () => {
        throw new Error('denied');
      },
    };
    for (const store of [storageTokenStore(broken, 'k'), storageTokenStore(undefined, 'k')]) {
      store.set('abc');
      expect(store.get()).toBe('abc');
      store.set(null);
      expect(store.get()).toBeNull();
    }
  });
});
