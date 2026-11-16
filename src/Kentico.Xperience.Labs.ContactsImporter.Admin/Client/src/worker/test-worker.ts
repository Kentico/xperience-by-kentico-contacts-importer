import type { WorkerLike } from './client';
import type { WorkerEvent, WorkerRequest } from './protocol';
import { ImportWorkerSession } from './session';

/**
 * Test double of the import worker: runs `ImportWorkerSession` in the test process and delivers
 * messages asynchronously, like a real worker. Records the traffic for assertions.
 */
export class InProcessWorker implements WorkerLike {
  onmessage: ((event: MessageEvent<WorkerEvent>) => void) | null = null;
  onerror: ((event: ErrorEvent) => void) | null = null;
  readonly requests: WorkerRequest[] = [];
  readonly events: WorkerEvent[] = [];
  terminated = false;

  private readonly session = new ImportWorkerSession((event) => {
    this.events.push(event);
    queueMicrotask(() => {
      if (!this.terminated) {
        this.onmessage?.({ data: event } as MessageEvent<WorkerEvent>);
      }
    });
  });

  postMessage(message: WorkerRequest): void {
    this.requests.push(message);
    queueMicrotask(() => {
      if (!this.terminated) {
        void this.session.handle(message);
      }
    });
  }

  terminate(): void {
    this.terminated = true;
  }
}

/** CSV with `rows` data rows: `Email,First Name` + `user{n}@example.com,Name {n} {padding}`. */
export function contactsCsv(rows: number, padding = ''): string {
  const lines = ['Email,First Name'];
  for (let i = 1; i <= rows; i++) {
    lines.push(`user${i}@example.com,Name ${i} ${padding}`);
  }

  return `${lines.join('\n')}\n`;
}

/** Text that gzip cannot shrink much, so small inputs still produce several chunks. */
export function noise(length: number, seed: number): string {
  let state = seed >>> 0 || 1;
  let text = '';
  while (text.length < length) {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    text += state.toString(36);
  }

  return text.slice(0, length);
}

/** CSV whose rows carry incompressible padding: about `bytesPerRow` gzipped bytes per row. */
export function noisyContactsCsv(rows: number, bytesPerRow: number): string {
  const lines = ['Email,First Name'];
  for (let i = 1; i <= rows; i++) {
    lines.push(`user${i}@example.com,${noise(bytesPerRow, i)}`);
  }

  return `${lines.join('\n')}\n`;
}
