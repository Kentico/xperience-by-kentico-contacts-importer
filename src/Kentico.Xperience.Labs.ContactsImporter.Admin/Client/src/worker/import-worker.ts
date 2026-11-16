import type { WorkerEvent, WorkerRequest } from './protocol';
import { ImportWorkerSession } from './session';

/**
 * Web Worker entry: parses, maps and chunks the file off the main thread so the admin UI stays
 * responsive. Bundled into a string by build/inline-worker-loader.js and started by
 * `create-worker.ts`. All logic lives in `session.ts`.
 */
interface WorkerScope {
  postMessage(message: WorkerEvent): void;
  onmessage: ((event: MessageEvent<WorkerRequest>) => void) | null;
}

const scope = self as unknown as WorkerScope;
const session = new ImportWorkerSession((event) => scope.postMessage(event));

scope.onmessage = (event) => {
  void session.handle(event.data);
};
