import type {
  EncodedChunk,
  FileLike,
  OpenSourceOptions,
  PipelineProgress,
  PipelineResult,
  SampleProfile,
  TargetField,
} from '../import-core';

import { ImportWorkerError } from './protocol';
import type { PipelineConfig, WorkerEvent, WorkerRequest } from './protocol';

/** The part of `Worker` the client uses; tests pass a fake that runs `ImportWorkerSession` in-process. */
export interface WorkerLike {
  postMessage(message: WorkerRequest): void;
  onmessage: ((event: MessageEvent<WorkerEvent>) => void) | null;
  onerror: ((event: ErrorEvent) => void) | null;
  terminate(): void;
}

export interface ProfileResult {
  profile: SampleProfile;
  fingerprint: string;
}

export interface RunOptions {
  source: OpenSourceOptions;
  pipeline: PipelineConfig;
  /** Most chunks handed to `onChunk` and not yet acknowledged. */
  window: number;
  progressIntervalMs?: number;
  /**
   * Called for every chunk, in file order. Call `acknowledge(chunk.payload.index)` on the run
   * once the chunk is uploaded or skipped; the worker waits for that before producing more.
   */
  onChunk: (chunk: EncodedChunk) => void;
  onProgress?: (progress: PipelineProgress) => void;
}

/** A full pass in progress. */
export interface ImportRun {
  /** Resolves with the totals when the whole file was processed; rejects with `ImportWorkerError`. */
  readonly done: Promise<PipelineResult>;
  acknowledge(index: number): void;
  /** Stops the pass; `done` rejects with code `aborted`. */
  abort(): void;
}

interface Pending {
  resolve: (event: WorkerEvent) => void;
  reject: (error: Error) => void;
  onChunk?: (chunk: EncodedChunk) => void;
  onProgress?: (progress: PipelineProgress) => void;
}

/**
 * Page side of the worker protocol (`protocol.ts`): turns requests into promises and routes
 * events by request ID.
 */
export class ImportWorkerClient {
  private readonly worker: WorkerLike;
  private readonly pending = new Map<number, Pending>();
  private nextRequestId = 1;
  private disposed = false;

  constructor(worker: WorkerLike) {
    this.worker = worker;
    worker.onmessage = (event) => this.receive(event.data);
    worker.onerror = (event) => {
      event.preventDefault?.();
      this.failAll(new ImportWorkerError('failed', event.message ?? ''));
    };
  }

  /** Profiles the first rows of the file (columns, sample, mapping suggestions) and fingerprints it. */
  async profile(
    file: FileLike,
    source: OpenSourceOptions,
    targetFields: TargetField[],
    maxRows?: number,
  ): Promise<ProfileResult> {
    const requestId = this.nextRequestId++;
    const event = await this.request(requestId, {
      type: 'profile',
      requestId,
      file,
      source,
      targetFields,
      maxRows,
    });
    if (event.type !== 'profiled') {
      throw new ImportWorkerError('failed', `Unexpected worker answer '${event.type}'.`);
    }

    return { profile: event.profile, fingerprint: event.fingerprint };
  }

  /** Starts the full pass over the file. */
  run(file: FileLike, options: RunOptions): ImportRun {
    const requestId = this.nextRequestId++;
    const finished = this.request(
      requestId,
      {
        type: 'start',
        requestId,
        file,
        source: options.source,
        pipeline: options.pipeline,
        window: options.window,
        progressIntervalMs: options.progressIntervalMs,
      },
      { onChunk: options.onChunk, onProgress: options.onProgress },
    );

    const done = finished.then((event) => {
      if (event.type !== 'done') {
        throw new ImportWorkerError('failed', `Unexpected worker answer '${event.type}'.`);
      }

      return event.result;
    });

    return {
      done,
      acknowledge: (index) => this.send({ type: 'ack', requestId, index }),
      abort: () => this.send({ type: 'abort', requestId }),
    };
  }

  /** Terminates the worker; open requests reject with code `aborted`. */
  dispose(): void {
    if (this.disposed) {
      return;
    }

    this.disposed = true;
    this.worker.terminate();
    this.failAll(new ImportWorkerError('aborted', 'The import worker was stopped.'));
  }

  private request(
    requestId: number,
    message: WorkerRequest,
    handlers: Pick<Pending, 'onChunk' | 'onProgress'> = {},
  ): Promise<WorkerEvent> {
    if (this.disposed) {
      return Promise.reject(new ImportWorkerError('aborted', 'The import worker was stopped.'));
    }

    return new Promise<WorkerEvent>((resolve, reject) => {
      this.pending.set(requestId, { resolve, reject, ...handlers });
      this.worker.postMessage(message);
    });
  }

  private send(message: WorkerRequest): void {
    if (!this.disposed) {
      this.worker.postMessage(message);
    }
  }

  private receive(event: WorkerEvent): void {
    const pending = this.pending.get(event.requestId);
    if (!pending) {
      return;
    }

    switch (event.type) {
      case 'chunk':
        pending.onChunk?.(event.chunk);
        break;
      case 'progress':
        pending.onProgress?.(event.progress);
        break;
      case 'error':
        this.pending.delete(event.requestId);
        pending.reject(new ImportWorkerError(event.code, event.message, event.params));
        break;
      default:
        this.pending.delete(event.requestId);
        pending.resolve(event);
        break;
    }
  }

  private failAll(error: Error): void {
    const pending = [...this.pending.values()];
    this.pending.clear();
    for (const request of pending) {
      request.reject(error);
    }
  }
}
