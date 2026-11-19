import type { WorkerLike } from "./client";
import workerSource from "./import-worker?inline-worker";

/**
 * Starts the import worker from the worker bundle inlined in this module.
 *
 * The Xperience admin loads a client module as a single file, so the worker cannot be a second
 * file; its code is a string (build/inline-worker-loader.js) run from a Blob URL.
 */
export function createImportWorker(): WorkerLike {
  const url = URL.createObjectURL(
    new Blob([workerSource], { type: "text/javascript" }),
  );
  try {
    return new Worker(url, { name: "contacts-importer" });
  } finally {
    // The worker keeps its own reference to the script once constructed.
    URL.revokeObjectURL(url);
  }
}
