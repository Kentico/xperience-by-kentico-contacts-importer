import { describe, expect, it } from 'vitest';

import type { EncodedChunk, UploadChunkPayload } from '../import-core';
import type { TruncateChunksResponse, UploadChunkResponse } from '../shared/commands';

import { RateEstimator, estimateTotalRows, percent } from './progress';
import { RetryExhaustedError, backoffDelay, sleep, withRetry } from './retry';
import { ChunkUploader, UploadError } from './uploader';
import type { ChunkUploaderOptions, UploadEvent } from './uploader';

const STORED: UploadChunkResponse = { code: 'stored', reason: null, message: null, rowNumber: null };
const noSleep = () => Promise.resolve();

function chunk(index: number, sha256 = `sha-${index}`, rows = 10): EncodedChunk {
  return {
    payload: { jobId: 'job', index, sha256, rows, data: `data-${index}` },
    compressedBytes: 100,
    uncompressedBytes: 400,
    firstRow: index * rows + 1,
    lastRow: (index + 1) * rows,
  };
}

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void } {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });

  return { promise, resolve, reject };
}

async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) {
    await Promise.resolve();
  }
}

function truncateOk(deleted = 0): TruncateChunksResponse {
  return { code: 'ok', message: null, deleted, chunks: [] };
}

function uploader(options: Partial<ChunkUploaderOptions> & { log?: string[] } = {}): ChunkUploader {
  const log = options.log ?? [];

  return new ChunkUploader({
    send: (payload) => {
      log.push(`send ${payload.index}`);

      return Promise.resolve(STORED);
    },
    truncate: (fromIndex) => {
      log.push(`truncate ${fromIndex}`);

      return Promise.resolve(truncateOk());
    },
    retry: { sleep: noSleep, random: () => 0.5 },
    ...options,
  });
}

describe('backoffDelay', () => {
  it('doubles per attempt, is capped and jitters by 25 %', () => {
    const middle = () => 0.5;

    expect(backoffDelay(1, 1000, 30_000, middle)).toBe(1000);
    expect(backoffDelay(2, 1000, 30_000, middle)).toBe(2000);
    expect(backoffDelay(4, 1000, 30_000, middle)).toBe(8000);
    expect(backoffDelay(10, 1000, 30_000, middle)).toBe(30_000);
    expect(backoffDelay(1, 1000, 30_000, () => 0)).toBe(750);
    expect(backoffDelay(1, 1000, 30_000, () => 1)).toBe(1250);
    expect(backoffDelay(10, 1000, 30_000, () => 1)).toBe(30_000);
  });
});

describe('sleep', () => {
  it('rejects with the abort reason when aborted while waiting', async () => {
    const controller = new AbortController();
    const waiting = sleep(60_000, controller.signal);
    controller.abort(new Error('stop'));

    await expect(waiting).rejects.toThrow('stop');
    await expect(sleep(1, controller.signal)).rejects.toThrow('stop');
  });
});

describe('withRetry', () => {
  it('returns the first answer without waiting', async () => {
    const delays: number[] = [];

    const result = await withRetry(() => Promise.resolve('answer'), {
      sleep: (ms) => {
        delays.push(ms);

        return Promise.resolve();
      },
    });

    expect(result).toBe('answer');
    expect(delays).toEqual([]);
  });

  it('retries undefined results and thrown errors with growing delays', async () => {
    const delays: number[] = [];
    const retries: number[] = [];
    let calls = 0;

    const result = await withRetry(
      () => {
        calls++;
        if (calls === 1) {
          return Promise.resolve(undefined);
        }
        if (calls === 2) {
          return Promise.reject(new Error('network'));
        }

        return Promise.resolve('answer');
      },
      {
        baseDelayMs: 100,
        random: () => 0.5,
        onRetry: (info) => retries.push(info.attempt),
        sleep: (ms) => {
          delays.push(ms);

          return Promise.resolve();
        },
      },
    );

    expect(result).toBe('answer');
    expect(calls).toBe(3);
    expect(delays).toEqual([100, 200]);
    expect(retries).toEqual([1, 2]);
  });

  it('gives up after maxAttempts', async () => {
    let calls = 0;

    const failure = await withRetry(
      () => {
        calls++;

        return Promise.resolve(undefined);
      },
      { maxAttempts: 3, sleep: noSleep },
    ).catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(RetryExhaustedError);
    expect((failure as RetryExhaustedError).attempts).toBe(3);
    expect(calls).toBe(3);
  });

  it('stops when aborted between attempts', async () => {
    const controller = new AbortController();
    let calls = 0;

    const failure = await withRetry(
      () => {
        calls++;
        controller.abort(new Error('stopped'));

        return Promise.resolve(undefined);
      },
      { signal: controller.signal, sleep: noSleep },
    ).catch((error: unknown) => error);

    expect((failure as Error).message).toBe('stopped');
    expect(calls).toBe(1);
  });
});

describe('ChunkUploader', () => {
  it('uploads every chunk and reports stats and events', async () => {
    const events: UploadEvent[] = [];
    const log: string[] = [];
    const target = uploader({ log, onEvent: (event) => events.push(event) });

    await Promise.all([target.add(chunk(0)), target.add(chunk(1)), target.add(chunk(2))]);
    const stats = await target.finish();

    expect(log).toEqual(['send 0', 'send 1', 'send 2']);
    expect(stats).toEqual({ uploadedChunks: 3, skippedChunks: 0, uploadedBytes: 300, skippedBytes: 0, retries: 0 });
    expect(events.map((event) => event.type)).toEqual(['uploaded', 'uploaded', 'uploaded']);
  });

  it('never runs more uploads at once than the parallel limit', async () => {
    const gates: ReturnType<typeof deferred<UploadChunkResponse>>[] = [];
    let active = 0;
    let maxActive = 0;
    const target = uploader({
      parallel: 2,
      send: async () => {
        active++;
        maxActive = Math.max(maxActive, active);
        const gate = deferred<UploadChunkResponse>();
        gates.push(gate);
        const response = await gate.promise;
        active--;

        return response;
      },
    });

    const added = [0, 1, 2, 3, 4].map((index) => target.add(chunk(index)));
    await settle();
    expect(gates).toHaveLength(2);

    while (gates.length < 5 || active > 0) {
      gates.filter((_, index) => index < gates.length).forEach((gate) => gate.resolve(STORED));
      await settle();
    }
    await Promise.all(added);

    expect(maxActive).toBe(2);
    expect((await target.finish()).uploadedChunks).toBe(5);
  });

  it('retries a failed request and counts the retry', async () => {
    const events: UploadEvent[] = [];
    let calls = 0;
    const target = uploader({
      onEvent: (event) => events.push(event),
      send: () => {
        calls++;

        return calls < 3 ? Promise.resolve(undefined) : Promise.resolve(STORED);
      },
    });

    await target.add(chunk(0));

    expect(calls).toBe(3);
    expect(target.stats.retries).toBe(2);
    expect(events.filter((event) => event.type === 'retry')).toHaveLength(2);
    expect(events[events.length - 1].type).toBe('uploaded');
  });

  it('fails with code network when every attempt fails, and stops the other chunks', async () => {
    const sent: number[] = [];
    const target = uploader({
      parallel: 1,
      retry: { maxAttempts: 2, sleep: noSleep },
      send: (payload) => {
        sent.push(payload.index);

        return Promise.resolve(undefined);
      },
    });

    const first = target.add(chunk(0)).catch((error: unknown) => error);
    const second = target.add(chunk(1)).catch((error: unknown) => error);

    expect(await first).toMatchObject({ code: 'network', index: 0 });
    expect(await second).toMatchObject({ code: 'network', index: 0 });
    await expect(target.finish()).rejects.toBeInstanceOf(UploadError);
    await expect(target.add(chunk(2))).rejects.toMatchObject({ code: 'network' });
    expect(sent).toEqual([0, 0]);
  });

  it('does not retry a chunk the server rejected and reports reason and row', async () => {
    let calls = 0;
    const target = uploader({
      send: () => {
        calls++;

        return Promise.resolve<UploadChunkResponse>({ code: 'rejected', reason: 'invalidRecord', message: 'Value too long.', rowNumber: 42 });
      },
    });

    const failure = await target.add(chunk(0)).catch((error: unknown) => error);

    expect(calls).toBe(1);
    expect(failure).toBeInstanceOf(UploadError);
    expect(failure).toMatchObject({ code: 'rejected', reason: 'invalidRecord', rowNumber: 42, index: 0, serverMessage: 'Value too long.' });
  });

  it('abort rejects waiting and running chunks with code aborted and passes the signal to send', async () => {
    let seenSignal: AbortSignal | undefined;
    const target = uploader({
      parallel: 1,
      send: (_payload: UploadChunkPayload, signal: AbortSignal) => {
        seenSignal = signal;

        return new Promise<UploadChunkResponse | undefined>((resolve) => {
          signal.addEventListener('abort', () => resolve(undefined));
        });
      },
    });

    const running = target.add(chunk(0)).catch((error: unknown) => error);
    const waiting = target.add(chunk(1)).catch((error: unknown) => error);
    await settle();
    target.abort();

    expect(await running).toMatchObject({ code: 'aborted' });
    expect(await waiting).toMatchObject({ code: 'aborted' });
    expect(seenSignal?.aborted).toBe(true);
    await expect(target.finish()).rejects.toMatchObject({ code: 'aborted' });
  });

  describe('resume', () => {
    it('skips chunks whose hash matches the stored chunk and uploads the missing ones', async () => {
      const log: string[] = [];
      const events: UploadEvent[] = [];
      const target = uploader({
        log,
        onEvent: (event) => events.push(event),
        storedChunks: [
          { index: 0, sha256: 'sha-0' },
          { index: 1, sha256: 'SHA-1' },
          { index: 3, sha256: 'sha-3' },
        ],
      });

      for (let index = 0; index < 5; index++) {
        await target.add(chunk(index));
      }

      // 2 was never stored (parallel uploads leave gaps), 4 is new; nothing is truncated.
      expect(log).toEqual(['send 2', 'send 4']);
      expect(target.stats).toMatchObject({ skippedChunks: 3, uploadedChunks: 2, skippedBytes: 300, uploadedBytes: 200 });
      expect(events.filter((event) => event.type === 'skipped').map((event) => event.index)).toEqual([0, 1, 3]);
    });

    it('truncates from the first chunk whose hash differs, then uploads everything from there', async () => {
      const log: string[] = [];
      const events: UploadEvent[] = [];
      const target = uploader({
        log,
        onEvent: (event) => events.push(event),
        truncate: (fromIndex) => {
          log.push(`truncate ${fromIndex}`);

          return Promise.resolve(truncateOk(3));
        },
        storedChunks: [
          { index: 0, sha256: 'sha-0' },
          { index: 1, sha256: 'other-browser-1' },
          { index: 2, sha256: 'sha-2' },
          { index: 3, sha256: 'other-browser-3' },
        ],
      });

      for (let index = 0; index < 4; index++) {
        await target.add(chunk(index));
      }

      // Chunk 2 has the stored hash by coincidence, but it was deleted by the truncation.
      expect(log).toEqual(['truncate 1', 'send 1', 'send 2', 'send 3']);
      expect(target.stats).toMatchObject({ skippedChunks: 1, uploadedChunks: 3 });
      expect(events).toContainEqual({ type: 'truncated', fromIndex: 1, deleted: 3 });
    });

    it('waits for the truncation before uploading later chunks, also in parallel', async () => {
      const log: string[] = [];
      const truncation = deferred<TruncateChunksResponse>();
      const target = uploader({
        log,
        parallel: 3,
        truncate: (fromIndex) => {
          log.push(`truncate ${fromIndex}`);

          return truncation.promise;
        },
        storedChunks: [
          { index: 0, sha256: 'other-0' },
          { index: 1, sha256: 'other-1' },
        ],
      });

      const added = [target.add(chunk(0)), target.add(chunk(1)), target.add(chunk(2))];
      await settle();
      expect(log).toEqual(['truncate 0']);

      truncation.resolve(truncateOk(2));
      await Promise.all(added);

      expect(log).toEqual(['truncate 0', 'send 0', 'send 1', 'send 2']);
    });

    it('fails with truncateFailed when the server refuses to delete stale chunks', async () => {
      const log: string[] = [];
      const target = uploader({
        log,
        truncate: () => Promise.resolve<TruncateChunksResponse>({ code: 'invalidState', message: 'Not uploading.', deleted: 0, chunks: [] }),
        storedChunks: [{ index: 0, sha256: 'other-0' }],
      });

      const failure = await target.add(chunk(0)).catch((error: unknown) => error);

      expect(failure).toMatchObject({ code: 'truncateFailed', serverMessage: 'Not uploading.' });
      expect(log).toEqual([]);
    });

    it('removes stale chunks above a missing chunk when the server reports overlapping rows', async () => {
      const log: string[] = [];
      let truncated = false;
      const target = uploader({
        log,
        send: (payload) => {
          log.push(`send ${payload.index}`);

          return Promise.resolve<UploadChunkResponse>(
            payload.index === 0 && !truncated
              ? { code: 'rejected', reason: 'rowOrder', message: 'Rows overlap.', rowNumber: 10 }
              : STORED,
          );
        },
        truncate: (fromIndex) => {
          log.push(`truncate ${fromIndex}`);
          truncated = true;

          return Promise.resolve(truncateOk(1));
        },
        // Chunk 0 is missing; chunk 1 is left from an upload with other chunk boundaries.
        storedChunks: [{ index: 1, sha256: 'other-1' }],
      });

      await target.add(chunk(0));

      expect(log).toEqual(['send 0', 'truncate 1', 'send 0']);
    });

    it('reports a row order rejection as is when nothing stale is stored above', async () => {
      const target = uploader({
        send: () => Promise.resolve<UploadChunkResponse>({ code: 'rejected', reason: 'rowOrder', message: 'Rows overlap.', rowNumber: 10 }),
      });

      await expect(target.add(chunk(0))).rejects.toMatchObject({ code: 'rejected', reason: 'rowOrder' });
    });
  });
});

describe('RateEstimator', () => {
  it('has no estimate before two samples', () => {
    const estimator = new RateEstimator();
    estimator.add(0, 0);

    expect(estimator.estimate(1000)).toEqual({ ratePerSecond: 0, etaSeconds: null });
  });

  it('computes rate and time left over the window', () => {
    const estimator = new RateEstimator(10_000);
    estimator.add(0, 0);
    estimator.add(1000, 100);
    estimator.add(2000, 200);

    expect(estimator.estimate(1000)).toEqual({ ratePerSecond: 100, etaSeconds: 8 });
  });

  it('forgets samples older than the window, so a slowdown shows', () => {
    const estimator = new RateEstimator(5000);
    estimator.add(0, 0);
    estimator.add(1000, 1000);
    estimator.add(10_000, 1100);
    estimator.add(11_000, 1200);

    const estimate = estimator.estimate(2200);

    expect(estimate.ratePerSecond).toBe(100);
    expect(estimate.etaSeconds).toBe(10);
  });

  it('starts over when progress goes back (a new pass)', () => {
    const estimator = new RateEstimator();
    estimator.add(0, 0);
    estimator.add(1000, 500);
    estimator.add(2000, 0);

    expect(estimator.estimate(1000).etaSeconds).toBeNull();
  });

  it('has no time left estimate while nothing moves', () => {
    const estimator = new RateEstimator();
    estimator.add(0, 100);
    estimator.add(1000, 100);

    expect(estimator.estimate(1000)).toEqual({ ratePerSecond: 0, etaSeconds: null });
  });
});

describe('progress helpers', () => {
  it('estimates the row total from the bytes read', () => {
    expect(estimateTotalRows(100, 1000, 10_000)).toBe(1000);
    expect(estimateTotalRows(100, 10_000, 10_000)).toBe(100);
    expect(estimateTotalRows(0, 0, 10_000)).toBe(0);
  });

  it('clamps the percentage to 0-100', () => {
    expect(percent(50, 200)).toBe(25);
    expect(percent(300, 200)).toBe(100);
    expect(percent(5, 0)).toBe(0);
  });
});
