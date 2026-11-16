import type { EncodedChunk, UploadChunkPayload } from '../import-core';
import type { TruncateChunksResponse, UploadChunkResponse } from '../shared/commands';

import { RetryExhaustedError, withRetry } from './retry';
import type { RetryOptions } from './retry';

/**
 * Upload transport: sends chunks with bounded parallelism, retries failed requests with backoff,
 * skips chunks the server already has (resume) and can be aborted.
 *
 * It does not queue: the caller hands over at most a few chunks at a time (the worker's
 * acknowledgement window) and waits for each `add` to settle before releasing more.
 */
export interface ChunkUploaderOptions {
  /** Sends one chunk (`UPLOAD_CHUNK`). `undefined` or a thrown error = failed request, retried. */
  send: (payload: UploadChunkPayload, signal: AbortSignal) => Promise<UploadChunkResponse | undefined>;
  /** Deletes stored chunks from an index up (`TRUNCATE_CHUNKS`). `undefined` = failed request, retried. */
  truncate: (fromIndex: number, signal: AbortSignal) => Promise<TruncateChunksResponse | undefined>;
  /** Uploads running at the same time. Default 3. */
  parallel?: number;
  /** Chunks the server already has (index and hash), from `GET_JOB` / `SAVE_CONFIG`. */
  storedChunks?: Iterable<{ index: number; sha256: string }>;
  /** Backoff settings; the uploader supplies the signal. */
  retry?: Omit<RetryOptions, 'signal' | 'onRetry'>;
  onEvent?: (event: UploadEvent) => void;
}

export type UploadEvent =
  | { type: 'uploaded'; index: number; bytes: number; rows: number }
  | { type: 'skipped'; index: number; bytes: number; rows: number }
  | { type: 'retry'; index: number; attempt: number; maxAttempts: number; delayMs: number }
  | { type: 'truncated'; fromIndex: number; deleted: number };

export interface UploadStats {
  /** Chunks sent and stored in this run. */
  uploadedChunks: number;
  /** Chunks the server already had with the same hash. */
  skippedChunks: number;
  /** Gzipped bytes sent / skipped. */
  uploadedBytes: number;
  skippedBytes: number;
  /** Failed requests that were retried. */
  retries: number;
}

export type UploadErrorCode =
  /** The server refused the chunk (validation, state); retrying the same chunk cannot help. */
  | 'rejected'
  /** Every attempt of a request failed. */
  | 'network'
  /** The server refused to delete stale chunks. */
  | 'truncateFailed'
  | 'aborted';

export class UploadError extends Error {
  readonly code: UploadErrorCode;
  /** Chunk the error belongs to, when it belongs to one. */
  readonly index?: number;
  /** Server's reject reason (camelCase `ChunkRejectReason`). */
  readonly reason?: string;
  /** Source row number the server named. */
  readonly rowNumber?: number;

  constructor(
    code: UploadErrorCode,
    message: string,
    details: { index?: number; reason?: string; rowNumber?: number } = {},
  ) {
    super(message);
    this.name = 'UploadError';
    this.code = code;
    this.index = details.index;
    this.reason = details.reason;
    this.rowNumber = details.rowNumber;
  }
}

const ROW_ORDER_REASON = 'rowOrder';

export class ChunkUploader {
  private readonly options: ChunkUploaderOptions;
  private readonly parallel: number;
  private readonly stored = new Map<number, string>();
  private readonly controller = new AbortController();
  private readonly tasks: Promise<void>[] = [];
  private readonly waiting: (() => void)[] = [];
  private active = 0;
  private truncation: Promise<void> | undefined;
  private failure: UploadError | undefined;
  private readonly counters: UploadStats = {
    uploadedChunks: 0,
    skippedChunks: 0,
    uploadedBytes: 0,
    skippedBytes: 0,
    retries: 0,
  };

  constructor(options: ChunkUploaderOptions) {
    this.options = options;
    this.parallel = Math.max(1, options.parallel ?? 3);
    for (const chunk of options.storedChunks ?? []) {
      this.stored.set(chunk.index, chunk.sha256.toLowerCase());
    }
  }

  get stats(): UploadStats {
    return { ...this.counters };
  }

  get signal(): AbortSignal {
    return this.controller.signal;
  }

  /**
   * Hands over the next chunk; call in file order. Resolves when the chunk is stored on the
   * server (uploaded now or already there). Rejects with `UploadError` when the upload failed
   * or was aborted; the first failure aborts all other chunks.
   */
  add(chunk: EncodedChunk): Promise<void> {
    if (this.failure) {
      return Promise.reject(this.failure);
    }

    // Resume decisions are made here, synchronously and in file order, so a chunk can never
    // be uploaded before a truncation that an earlier chunk asked for.
    const { index, sha256 } = chunk.payload;
    const stored = this.stored.get(index);
    if (stored === sha256) {
      this.counters.skippedChunks++;
      this.counters.skippedBytes += chunk.compressedBytes;
      this.emit({ type: 'skipped', index, bytes: chunk.compressedBytes, rows: chunk.payload.rows });

      return Promise.resolve();
    }
    if (stored !== undefined) {
      // First difference from the stored upload (another browser gzips differently): the stored
      // chunks from here on belong to other chunk boundaries and must go.
      this.startTruncation(index);
    }

    const task = this.upload(chunk).catch((error: unknown) => {
      throw this.fail(toUploadError(error, index));
    });
    this.tasks.push(task);
    // The caller awaits either this promise or `finish`; never leave the other one unhandled.
    task.catch(() => undefined);

    return task;
  }

  /** Resolves when every added chunk is stored; rejects with the first `UploadError`. */
  async finish(): Promise<UploadStats> {
    await Promise.allSettled(this.tasks);
    if (this.failure) {
      throw this.failure;
    }

    return this.stats;
  }

  /** Stops all uploads. Pending `add` promises reject with code `aborted`. */
  abort(): void {
    this.fail(new UploadError('aborted', 'The upload was stopped.'));
  }

  private async upload(chunk: EncodedChunk): Promise<void> {
    const { signal } = this.controller;
    const { payload } = chunk;
    const { index } = payload;

    await this.acquire();
    try {
      // Checked after the slot is free: a truncation may have started while this chunk waited.
      await this.truncation;

      let response = await this.sendWithRetry(payload);
      if (response.code === 'rejected' && response.reason === ROW_ORDER_REASON && this.hasStoredAbove(index)) {
        // A stale chunk above this one (from other chunk boundaries) overlaps its rows.
        if (!this.truncation) {
          this.startTruncation(index + 1);
        }
        await this.truncation;
        response = await this.sendWithRetry(payload);
      }

      if (response.code === 'rejected') {
        throw new UploadError('rejected', response.message ?? 'The server rejected the uploaded data.', {
          index,
          reason: response.reason ?? undefined,
          rowNumber: response.rowNumber ?? undefined,
        });
      }

      signal.throwIfAborted();
      this.counters.uploadedChunks++;
      this.counters.uploadedBytes += chunk.compressedBytes;
      this.emit({ type: 'uploaded', index, bytes: chunk.compressedBytes, rows: payload.rows });
    } finally {
      this.release();
    }
  }

  private sendWithRetry(payload: UploadChunkPayload): Promise<UploadChunkResponse> {
    const { signal } = this.controller;

    return withRetry(() => this.options.send(payload, signal), {
      ...this.options.retry,
      signal,
      onRetry: (info) => {
        this.counters.retries++;
        this.emit({ type: 'retry', index: payload.index, ...info });
      },
    });
  }

  private hasStoredAbove(index: number): boolean {
    for (const storedIndex of this.stored.keys()) {
      if (storedIndex > index) {
        return true;
      }
    }

    return false;
  }

  private startTruncation(fromIndex: number): void {
    for (const storedIndex of [...this.stored.keys()]) {
      if (storedIndex >= fromIndex) {
        this.stored.delete(storedIndex);
      }
    }

    const { signal } = this.controller;
    const previous = this.truncation ?? Promise.resolve();
    const truncation = previous.then(async () => {
      const response = await withRetry(() => this.options.truncate(fromIndex, signal), {
        ...this.options.retry,
        signal,
      });
      if (response.code !== 'ok') {
        throw new UploadError('truncateFailed', response.message ?? 'Stored data could not be replaced.');
      }

      this.emit({ type: 'truncated', fromIndex, deleted: response.deleted });
    });
    // Awaited by the uploads that depend on it; a failure reaches them there.
    truncation.catch(() => undefined);
    this.truncation = truncation;
  }

  private acquire(): Promise<void> {
    const { signal } = this.controller;
    if (signal.aborted) {
      return Promise.reject(signal.reason);
    }
    if (this.active < this.parallel) {
      this.active++;

      return Promise.resolve();
    }

    return new Promise<void>((resolve, reject) => {
      const onAbort = () => {
        const position = this.waiting.indexOf(enter);
        if (position !== -1) {
          this.waiting.splice(position, 1);
        }
        reject(signal.reason);
      };
      const enter = () => {
        signal.removeEventListener('abort', onAbort);
        this.active++;
        resolve();
      };
      this.waiting.push(enter);
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }

  private release(): void {
    this.active--;
    this.waiting.shift()?.();
  }

  private fail(error: UploadError): UploadError {
    if (!this.failure) {
      this.failure = error;
      this.controller.abort(error);
    }

    return this.failure;
  }

  private emit(event: UploadEvent): void {
    this.options.onEvent?.(event);
  }
}

function toUploadError(error: unknown, index: number): UploadError {
  if (error instanceof UploadError) {
    return error;
  }
  if (error instanceof RetryExhaustedError) {
    return new UploadError('network', error.message, { index });
  }

  return new UploadError('network', error instanceof Error ? error.message : String(error), { index });
}
