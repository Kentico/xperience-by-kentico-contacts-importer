import { describe, expect, it } from 'vitest';

import type { TargetField, UploadChunkPayload } from '../import-core';
import { makeFile } from '../import-core/test-helpers';
import { Commands } from '../shared/commands';
import type {
  CommandName,
  CompleteUploadRequest,
  CompleteUploadResponse,
  ExecuteCommand,
  StatusDto,
  StoredChunkDto,
  TruncateChunksRequest,
  TruncateChunksResponse,
  UploadChunkResponse,
} from '../shared/commands';
import { ImportWorkerClient } from '../worker/client';
import type { PipelineConfig } from '../worker/protocol';
import { InProcessWorker, contactsCsv, noisyContactsCsv } from '../worker/test-worker';

import { UploadSessionError, startUploadSession } from './upload-session';
import type { UploadProgress, UploadSessionOptions } from './upload-session';

const FIELDS: TargetField[] = [
  { name: 'ContactEmail', dataType: 'text', maxLength: 254 },
  { name: 'ContactFirstName', dataType: 'text', maxLength: 1000 },
];

const PIPELINE: PipelineConfig = {
  jobId: '11111111-1111-4111-8111-111111111111',
  keyField: 'ContactEmail',
  mapping: [
    { source: 'Email', target: 'ContactEmail' },
    { source: 'First Name', target: 'ContactFirstName' },
  ],
  dateFormat: 'iso',
  fields: FIELDS,
  targetChunkBytes: 16 * 1024,
  maxChunkUncompressedBytes: 32 * 1024 * 1024,
};

/** In-memory stand-in for the server side of the upload commands. */
class FakeServer {
  readonly chunks = new Map<number, UploadChunkPayload>();
  readonly calls: { name: CommandName; data: unknown }[] = [];
  completed: CompleteUploadRequest | undefined;
  /** Hook to fail or change answers per call. */
  intercept: ((name: CommandName, data: unknown) => unknown) | undefined;
  /** How long one chunk upload takes. */
  uploadDelayMs = 1;
  /** Uploads running right now / at most. */
  active = 0;
  maxActive = 0;

  constructor(stored: UploadChunkPayload[] = []) {
    for (const chunk of stored) {
      this.chunks.set(chunk.index, chunk);
    }
  }

  get storedChunks(): StoredChunkDto[] {
    return [...this.chunks.values()].sort((a, b) => a.index - b.index).map((c) => ({ index: c.index, sha256: c.sha256, rows: c.rows }));
  }

  readonly execute: ExecuteCommand = async <TResult, TData = void>(name: CommandName, data?: TData): Promise<TResult | undefined> => {
    this.calls.push({ name, data });
    const intercepted = this.intercept?.(name, data);
    if (intercepted === 'fail') {
      return undefined;
    }
    if (intercepted !== undefined) {
      return intercepted as TResult;
    }

    switch (name) {
      case Commands.UploadChunk: {
        this.active++;
        this.maxActive = Math.max(this.maxActive, this.active);
        await new Promise<void>((resolve) => setTimeout(resolve, this.uploadDelayMs));
        this.active--;
        const payload = data as UploadChunkPayload;
        const existing = this.chunks.get(payload.index);
        this.chunks.set(payload.index, payload);

        return {
          code: existing?.sha256 === payload.sha256 ? 'alreadyStored' : 'stored',
          reason: null,
          message: null,
          rowNumber: null,
        } satisfies UploadChunkResponse as TResult;
      }
      case Commands.TruncateChunks: {
        const { fromIndex } = data as TruncateChunksRequest;
        let deleted = 0;
        for (const index of [...this.chunks.keys()]) {
          if (index >= fromIndex) {
            this.chunks.delete(index);
            deleted++;
          }
        }

        return { code: 'ok', message: null, deleted, chunks: this.storedChunks } satisfies TruncateChunksResponse as TResult;
      }
      case Commands.CompleteUpload: {
        this.completed = data as CompleteUploadRequest;
        const { profile } = this.completed;
        const rows = [...this.chunks.values()].reduce((sum, chunk) => sum + chunk.rows, 0);
        if (this.chunks.size !== profile.chunkCount || rows !== profile.rowsValid) {
          return { code: 'inconsistent', message: 'Chunks do not match.', missingChunks: [], status: null } satisfies CompleteUploadResponse as TResult;
        }

        const status: StatusDto = { state: 'Uploaded', chunks: this.storedChunks, profile, updatedUtc: '2026-11-16T12:00:00Z', error: null };

        return { code: 'ok', message: null, missingChunks: [], status } satisfies CompleteUploadResponse as TResult;
      }
      default:
        return undefined;
    }
  };

  count(name: CommandName): number {
    return this.calls.filter((call) => call.name === name).length;
  }
}

function session(server: FakeServer, csv: string, overrides: Partial<UploadSessionOptions> = {}) {
  return startUploadSession({
    worker: new ImportWorkerClient(new InProcessWorker()),
    execute: server.execute,
    file: makeFile(csv, 'contacts.csv', 8 * 1024),
    source: {},
    pipeline: PIPELINE,
    parallel: 3,
    storedChunks: server.storedChunks,
    retry: { sleep: () => Promise.resolve(), maxAttempts: 3 },
    ...overrides,
  });
}

describe('startUploadSession', () => {
  it('uploads all chunks with bounded parallelism and completes the upload', async () => {
    const server = new FakeServer();
    // Slower than chunk production, so uploads overlap.
    server.uploadDelayMs = 25;
    const snapshots: UploadProgress[] = [];

    const outcome = await session(server, noisyContactsCsv(500, 400), { onProgress: (progress) => snapshots.push(progress) }).done;

    expect(outcome.status.state).toBe('Uploaded');
    expect(outcome.result.profile).toMatchObject({ rowsRead: 500, rowsValid: 500, rowsRejected: 0 });
    expect(outcome.result.profile.chunkCount).toBeGreaterThan(4);
    expect(server.chunks.size).toBe(outcome.result.profile.chunkCount);
    expect([...server.chunks.keys()].sort((a, b) => a - b)).toEqual([...server.chunks.keys()].map((_, index) => index));
    expect(server.completed?.profile).toEqual(outcome.result.profile);
    expect(server.maxActive).toBeGreaterThan(1);
    expect(server.maxActive).toBeLessThanOrEqual(3);
    expect(outcome.stats).toMatchObject({ uploadedChunks: outcome.result.profile.chunkCount, skippedChunks: 0 });

    expect(snapshots.length).toBeGreaterThan(0);
    expect(snapshots[snapshots.length - 1].phase).toBe('completing');
    expect(snapshots.some((snapshot) => snapshot.pipeline.bytesRead > 0 && snapshot.pipeline.totalBytes > 0)).toBe(true);
  });

  it('counts rows skipped in the browser and never uploads them', async () => {
    const server = new FakeServer();
    const csv = `${contactsCsv(5)},No Email\nnot-an-email,Bad Email\n`;

    const outcome = await session(server, csv).done;

    expect(outcome.result.profile).toMatchObject({ rowsRead: 7, rowsValid: 5, rowsRejected: 2 });
    expect(outcome.result.rejectedRows.map((row) => [row.rowNumber, row.reason])).toEqual([
      [6, 'missingKey'],
      [7, 'invalidEmailKey'],
    ]);
    expect([...server.chunks.values()].reduce((rows, chunk) => rows + chunk.rows, 0)).toBe(5);
  });

  it('resume: sends nothing again when the server has every chunk', async () => {
    const csv = noisyContactsCsv(300, 400);
    const first = new FakeServer();
    await session(first, csv).done;

    const second = new FakeServer([...first.chunks.values()]);
    const outcome = await session(second, csv).done;

    expect(second.count(Commands.UploadChunk)).toBe(0);
    expect(second.count(Commands.TruncateChunks)).toBe(0);
    expect(outcome.stats).toMatchObject({ uploadedChunks: 0, skippedChunks: first.chunks.size });
    expect(outcome.status.state).toBe('Uploaded');
  });

  it('resume: uploads only the chunks the server is missing', async () => {
    const csv = noisyContactsCsv(300, 400);
    const first = new FakeServer();
    await session(first, csv).done;
    const all = [...first.chunks.values()];
    expect(all.length).toBeGreaterThan(3);

    // Chunk 1 and the last chunk never arrived.
    const second = new FakeServer(all.filter((chunk) => chunk.index !== 1 && chunk.index !== all.length - 1));
    const outcome = await session(second, csv).done;

    const uploaded = second.calls.filter((call) => call.name === Commands.UploadChunk).map((call) => (call.data as UploadChunkPayload).index);
    expect(uploaded.sort((a, b) => a - b)).toEqual([1, all.length - 1]);
    expect(second.count(Commands.TruncateChunks)).toBe(0);
    expect(outcome.stats.skippedChunks).toBe(all.length - 2);
  });

  it('resume from another browser: truncates at the first different chunk and uploads the rest', async () => {
    const csv = noisyContactsCsv(300, 400);
    const first = new FakeServer();
    await session(first, csv).done;
    const all = [...first.chunks.values()].sort((a, b) => a.index - b.index);

    // Same rows, but chunks 2.. were produced with other gzip bytes (other hashes), plus one extra stale chunk.
    const foreign = all.map((chunk) => (chunk.index >= 2 ? { ...chunk, sha256: `f${chunk.sha256.slice(1)}` } : chunk));
    foreign.push({ ...all[all.length - 1], index: all.length, sha256: 'stale' });
    const second = new FakeServer(foreign);
    const snapshots: UploadProgress[] = [];

    const outcome = await session(second, csv, { onProgress: (progress) => snapshots.push(progress) }).done;

    const truncations = second.calls.filter((call) => call.name === Commands.TruncateChunks).map((call) => (call.data as TruncateChunksRequest).fromIndex);
    expect(truncations).toEqual([2]);
    const uploaded = second.calls.filter((call) => call.name === Commands.UploadChunk).map((call) => (call.data as UploadChunkPayload).index);
    expect(uploaded.sort((a, b) => a - b)).toEqual(all.slice(2).map((chunk) => chunk.index));
    expect(outcome.stats.skippedChunks).toBe(2);
    expect(second.storedChunks.map((chunk) => chunk.sha256)).toEqual(all.map((chunk) => chunk.sha256));
    expect(snapshots.some((snapshot) => snapshot.truncatedFrom === 2)).toBe(true);
  });

  it('retries failed chunk requests and reports the retry in the progress', async () => {
    const server = new FakeServer();
    let failures = 2;
    server.intercept = (name) => (name === Commands.UploadChunk && failures-- > 0 ? 'fail' : undefined);
    const snapshots: UploadProgress[] = [];

    const outcome = await session(server, noisyContactsCsv(100, 400), { onProgress: (progress) => snapshots.push(progress) }).done;

    expect(outcome.status.state).toBe('Uploaded');
    expect(outcome.stats.retries).toBe(2);
    expect(snapshots.some((snapshot) => snapshot.retry !== null)).toBe(true);
  });

  it('fails with kind network when the server stays unreachable, and stops the worker', async () => {
    const server = new FakeServer();
    server.intercept = (name) => (name === Commands.UploadChunk ? 'fail' : undefined);

    const failure = await session(server, noisyContactsCsv(500, 400)).done.catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(UploadSessionError);
    expect((failure as UploadSessionError).kind).toBe('network');
    expect(server.count(Commands.CompleteUpload)).toBe(0);
    // The worker stopped early: far fewer upload attempts than chunks x attempts.
    expect(server.count(Commands.UploadChunk)).toBeLessThanOrEqual(3 * 3 + 3);
  });

  it('fails with kind rejected and the row number when the server refuses a chunk', async () => {
    const server = new FakeServer();
    server.intercept = (name) =>
      name === Commands.UploadChunk
        ? ({ code: 'rejected', reason: 'invalidRecord', message: 'A value is too long.', rowNumber: 17 } satisfies UploadChunkResponse)
        : undefined;

    const failure = await session(server, contactsCsv(50)).done.catch((error: unknown) => error);

    expect(failure).toMatchObject({ kind: 'rejected', serverMessage: 'A value is too long.', rowNumber: 17 });
  });

  it('fails with kind worker when the mapping does not fit the file', async () => {
    const server = new FakeServer();

    const failure = await session(server, 'Other,Columns\n1,2\n').done.catch((error: unknown) => error);

    expect(failure).toMatchObject({ kind: 'worker' });
    expect(server.calls).toEqual([]);
  });

  it('fails with kind noRows when no row can be imported and keeps the reject report', async () => {
    const server = new FakeServer();

    const failure = await session(server, 'Email,First Name\n,Nobody\nbad,Nobody\n').done.catch((error: unknown) => error);

    expect(failure).toMatchObject({ kind: 'noRows' });
    expect((failure as UploadSessionError).result?.rejectedRows).toHaveLength(2);
    expect(server.count(Commands.CompleteUpload)).toBe(0);
  });

  it('fails with kind complete when the server does not accept the finished upload', async () => {
    const server = new FakeServer();
    server.intercept = (name) =>
      name === Commands.CompleteUpload
        ? ({ code: 'inconsistent', message: 'Some chunks are not uploaded yet.', missingChunks: [0], status: null } satisfies CompleteUploadResponse)
        : undefined;

    const failure = await session(server, contactsCsv(10)).done.catch((error: unknown) => error);

    expect(failure).toMatchObject({ kind: 'complete', serverMessage: 'Some chunks are not uploaded yet.' });
  });

  it('retries COMPLETE_UPLOAD when the request fails', async () => {
    const server = new FakeServer();
    let failures = 1;
    server.intercept = (name) => (name === Commands.CompleteUpload && failures-- > 0 ? 'fail' : undefined);

    const outcome = await session(server, contactsCsv(10)).done;

    expect(outcome.status.state).toBe('Uploaded');
    expect(server.count(Commands.CompleteUpload)).toBe(2);
  });

  it('abort stops the pass with kind aborted and keeps stored chunks for a resume', async () => {
    const server = new FakeServer();
    const csv = noisyContactsCsv(600, 400);
    const running = session(server, csv, {
      onProgress: (progress) => {
        if (progress.stats.uploadedChunks >= 2) {
          running.abort();
        }
      },
    });

    const failure = await running.done.catch((error: unknown) => error);

    expect(failure).toMatchObject({ kind: 'aborted' });
    expect(server.count(Commands.CompleteUpload)).toBe(0);
    const storedBefore = server.chunks.size;
    expect(storedBefore).toBeGreaterThanOrEqual(2);

    // The next pass skips what is stored and finishes.
    const outcome = await session(server, csv).done;
    expect(outcome.status.state).toBe('Uploaded');
    expect(outcome.stats.skippedChunks).toBe(storedBefore);
  });
});
