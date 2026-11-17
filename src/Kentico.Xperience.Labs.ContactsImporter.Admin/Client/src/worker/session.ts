import {
  ImportError,
  createImportPipeline,
  fingerprintFile,
  openRecordSource,
  profileSample,
} from '../import-core';
import type { ImportErrorParams, PipelineProgress } from '../import-core';

import type {
  ProfileRequest,
  StartRequest,
  WorkerErrorCode,
  WorkerEvent,
  WorkerRequest,
} from './protocol';

const DEFAULT_PROGRESS_INTERVAL_MS = 200;

interface ActiveRun {
  abort: AbortController;
  /** Chunks sent and not acknowledged. */
  inFlight: number;
  /** Resolves the wait for an acknowledgement. */
  wake: (() => void) | undefined;
}

/**
 * Worker side of the protocol (`protocol.ts`), independent of the Worker global so it runs in
 * unit tests: give it a function that delivers events to the page and feed it requests.
 */
export class ImportWorkerSession {
  private readonly post: (event: WorkerEvent) => void;
  private readonly now: () => number;
  private readonly runs = new Map<number, ActiveRun>();

  constructor(post: (event: WorkerEvent) => void, now: () => number = () => Date.now()) {
    this.post = post;
    this.now = now;
  }

  /** Handles one request. The returned promise settles when the request finished (tests await it). */
  handle(request: WorkerRequest): Promise<void> {
    switch (request.type) {
      case 'profile':
        return this.guard(request.requestId, () => this.profile(request));
      case 'start':
        return this.guard(request.requestId, () => this.start(request));
      case 'ack':
        this.acknowledge(request.requestId);

        return Promise.resolve();
      case 'abort':
        this.abort(request.requestId);

        return Promise.resolve();
      default:
        return Promise.resolve();
    }
  }

  private async profile(request: ProfileRequest): Promise<void> {
    const source = await openRecordSource(request.file, request.source);
    let profile;
    try {
      profile = await profileSample(source, {
        maxRows: request.maxRows,
        targetFields: request.targetFields,
      });
    } finally {
      await source.cancel();
    }

    const fingerprint = await fingerprintFile(request.file);
    this.post({ type: 'profiled', requestId: request.requestId, profile, fingerprint });
  }

  private async start(request: StartRequest): Promise<void> {
    const { requestId } = request;
    const run: ActiveRun = { abort: new AbortController(), inFlight: 0, wake: undefined };
    this.runs.set(requestId, run);

    const interval = request.progressIntervalMs ?? DEFAULT_PROGRESS_INTERVAL_MS;
    let lastProgress = -Infinity;
    const onProgress = (progress: PipelineProgress) => {
      const time = this.now();
      if (time - lastProgress >= interval) {
        lastProgress = time;
        this.post({ type: 'progress', requestId, progress });
      }
    };

    try {
      const source = await openRecordSource(request.file, request.source);
      const pipeline = createImportPipeline(source, {
        ...request.pipeline,
        signal: run.abort.signal,
        onProgress,
      });
      const window = Math.max(1, request.window);

      for await (const chunk of pipeline.chunks()) {
        run.inFlight++;
        this.post({ type: 'chunk', requestId, chunk });

        // Backpressure: stop pulling the pipeline until the page caught up.
        while (run.inFlight >= window && !run.abort.signal.aborted) {
          await new Promise<void>((resolve) => {
            run.wake = resolve;
          });
        }
        run.abort.signal.throwIfAborted();
      }

      this.post({ type: 'progress', requestId, progress: pipeline.progress() });
      this.post({ type: 'done', requestId, result: pipeline.result() });
    } finally {
      this.runs.delete(requestId);
    }
  }

  private acknowledge(requestId: number): void {
    const run = this.runs.get(requestId);
    if (!run) {
      return;
    }

    run.inFlight = Math.max(0, run.inFlight - 1);
    this.wake(run);
  }

  private abort(requestId: number): void {
    const run = this.runs.get(requestId);
    if (!run) {
      return;
    }

    run.abort.abort(new DOMException('The import was stopped.', 'AbortError'));
    this.wake(run);
  }

  private wake(run: ActiveRun): void {
    const wake = run.wake;
    run.wake = undefined;
    wake?.();
  }

  private async guard(requestId: number, work: () => Promise<void>): Promise<void> {
    try {
      await work();
    } catch (error) {
      this.post({ type: 'error', requestId, ...describeError(error) });
    }
  }
}

function describeError(error: unknown): { code: WorkerErrorCode; message: string; params?: ImportErrorParams } {
  if (error instanceof ImportError) {
    return { code: error.code, message: error.message, params: error.params };
  }
  if (error instanceof Error && error.name === 'AbortError') {
    return { code: 'aborted', message: error.message };
  }

  // Parser messages never include row values, so the message is safe to show.
  return { code: 'failed', message: error instanceof Error ? error.message : String(error) };
}
