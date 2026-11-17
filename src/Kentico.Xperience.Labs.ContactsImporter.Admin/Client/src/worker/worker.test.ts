import { describe, expect, it } from 'vitest';

import type { EncodedChunk, TargetField } from '../import-core';
import { fingerprintFile } from '../import-core';
import { decodeChunkData, makeFile } from '../import-core/test-helpers';

import { ImportWorkerClient } from './client';
import { ImportWorkerError } from './protocol';
import type { PipelineConfig, WorkerEvent } from './protocol';
import { ImportWorkerSession } from './session';
import { InProcessWorker, contactsCsv, noisyContactsCsv } from './test-worker';

const FIELDS: TargetField[] = [
  { name: 'ContactEmail', dataType: 'text', maxLength: 254 },
  { name: 'ContactFirstName', dataType: 'text', maxLength: 1000 },
];

function pipelineConfig(overrides: Partial<PipelineConfig> = {}): PipelineConfig {
  return {
    jobId: '11111111-1111-4111-8111-111111111111',
    keyField: 'ContactEmail',
    mapping: [
      { source: 'Email', target: 'ContactEmail' },
      { source: 'First Name', target: 'ContactFirstName' },
    ],
    dateFormat: 'iso',
    fields: FIELDS,
    targetChunkBytes: 4 * 1024 * 1024,
    maxChunkUncompressedBytes: 32 * 1024 * 1024,
    ...overrides,
  };
}

/** Lets queued microtasks and stream reads run. */
async function settle(turns = 50): Promise<void> {
  for (let i = 0; i < turns; i++) {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
}

describe('ImportWorkerSession', () => {
  it('answers a profile request with the sample profile and the file fingerprint', async () => {
    const events: WorkerEvent[] = [];
    const session = new ImportWorkerSession((event) => events.push(event));
    const file = makeFile(contactsCsv(5), 'contacts.csv');

    await session.handle({ type: 'profile', requestId: 7, file, source: {}, targetFields: FIELDS });

    expect(events).toHaveLength(1);
    const [event] = events;
    expect(event.type).toBe('profiled');
    if (event.type === 'profiled') {
      expect(event.requestId).toBe(7);
      expect(event.profile.columns.map((column) => column.name)).toEqual(['Email', 'First Name']);
      expect(event.profile.rowsSampled).toBe(5);
      expect(event.profile.suggestions).toEqual([
        { source: 'Email', target: 'ContactEmail' },
        { source: 'First Name', target: 'ContactFirstName' },
      ]);
      expect(event.fingerprint).toBe(await fingerprintFile(file));
    }
    expect(file.cancelled || file.pulls > 0).toBe(true);
  });

  it('reports an unsupported file as an error event with the import core code', async () => {
    const events: WorkerEvent[] = [];
    const session = new ImportWorkerSession((event) => events.push(event));

    await session.handle({ type: 'profile', requestId: 1, file: makeFile('x', 'contacts.xlsx'), source: {}, targetFields: [] });

    expect(events).toEqual([expect.objectContaining({ type: 'error', requestId: 1, code: 'unsupportedFormat' })]);
  });

  it('reports a mapping that does not fit the file as invalidConfig', async () => {
    const events: WorkerEvent[] = [];
    const session = new ImportWorkerSession((event) => events.push(event));

    await session.handle({
      type: 'start',
      requestId: 2,
      file: makeFile(contactsCsv(3)),
      source: {},
      pipeline: pipelineConfig({ mapping: [{ source: 'Missing', target: 'ContactEmail' }] }),
      window: 2,
    });

    expect(events).toEqual([expect.objectContaining({ type: 'error', requestId: 2, code: 'invalidConfig' })]);
  });

  it('passes the consent columns of the job to the pipeline: every uploaded record has its consent part', async () => {
    const events: WorkerEvent[] = [];
    const session = new ImportWorkerSession((event) => {
      events.push(event);
      if (event.type === 'chunk') {
        void session.handle({ type: 'ack', requestId: event.requestId, index: event.chunk.payload.index });
      }
    });

    await session.handle({
      type: 'start',
      requestId: 4,
      file: makeFile('Email,First Name,Opt in\na@example.com,Ann,yes\nb@example.com,Bob,\n'),
      source: {},
      pipeline: pipelineConfig({ consentMapping: { agreed: 'Opt in', date: null } }),
      window: 2,
    });

    const chunk = events.find((event) => event.type === 'chunk');
    expect(chunk).toBeDefined();
    if (chunk?.type === 'chunk') {
      expect((await decodeChunkData(chunk.chunk.payload.data)).map((record) => record.c)).toEqual([{ a: true }, { a: false }]);
    }
  });

  it('sends chunks in order, then progress and done with the totals', async () => {
    const events: WorkerEvent[] = [];
    const session = new ImportWorkerSession((event) => {
      events.push(event);
      if (event.type === 'chunk') {
        void session.handle({ type: 'ack', requestId: event.requestId, index: event.chunk.payload.index });
      }
    });

    await session.handle({
      type: 'start',
      requestId: 3,
      file: makeFile(noisyContactsCsv(300, 400)),
      source: {},
      pipeline: pipelineConfig({ targetChunkBytes: 16 * 1024 }),
      window: 2,
      progressIntervalMs: 0,
    });

    const chunks = events.filter((event) => event.type === 'chunk');
    expect(chunks.length).toBeGreaterThan(2);
    expect(chunks.map((event) => (event.type === 'chunk' ? event.chunk.payload.index : -1))).toEqual(chunks.map((_, index) => index));
    const last = events[events.length - 1];
    expect(last.type).toBe('done');
    if (last.type === 'done') {
      expect(last.result.completed).toBe(true);
      expect(last.result.profile).toMatchObject({ rowsRead: 300, rowsValid: 300, rowsRejected: 0, chunkCount: chunks.length });
    }
    expect(events.some((event) => event.type === 'progress')).toBe(true);
  });

  it('stops producing chunks until the page acknowledges them (backpressure)', async () => {
    const events: WorkerEvent[] = [];
    const session = new ImportWorkerSession((event) => events.push(event));
    const file = makeFile(noisyContactsCsv(600, 400), 'contacts.csv', 8 * 1024);
    const chunkCount = () => events.filter((event) => event.type === 'chunk').length;

    const finished = session.handle({
      type: 'start',
      requestId: 4,
      file,
      source: {},
      pipeline: pipelineConfig({ targetChunkBytes: 16 * 1024 }),
      window: 2,
    });

    await settle();
    // Two unacknowledged chunks: the worker waits and stops reading the file.
    expect(chunkCount()).toBe(2);
    const pullsWhileWaiting = file.pulls;
    await settle();
    expect(chunkCount()).toBe(2);
    expect(file.pulls).toBe(pullsWhileWaiting);
    expect(file.pulls * 8 * 1024).toBeLessThan(file.size);

    // One acknowledgement frees one slot.
    await session.handle({ type: 'ack', requestId: 4, index: 0 });
    await settle();
    expect(chunkCount()).toBe(3);

    // Acknowledge everything from now on and let the pass finish.
    let acknowledged = 1;
    while (!events.some((event) => event.type === 'done' || event.type === 'error')) {
      while (acknowledged < chunkCount()) {
        await session.handle({ type: 'ack', requestId: 4, index: acknowledged++ });
      }
      await settle(5);
    }
    await finished;

    expect(events[events.length - 1].type).toBe('done');
    expect(chunkCount()).toBeGreaterThan(5);
  });

  it('aborts a pass that waits for an acknowledgement and releases the file', async () => {
    const events: WorkerEvent[] = [];
    const session = new ImportWorkerSession((event) => events.push(event));
    const file = makeFile(noisyContactsCsv(600, 400), 'contacts.csv', 8 * 1024);

    const finished = session.handle({
      type: 'start',
      requestId: 5,
      file,
      source: {},
      pipeline: pipelineConfig({ targetChunkBytes: 16 * 1024 }),
      window: 1,
    });
    await settle();
    await session.handle({ type: 'abort', requestId: 5 });
    await finished;

    expect(events[events.length - 1]).toMatchObject({ type: 'error', requestId: 5, code: 'aborted' });
    expect(events.filter((event) => event.type === 'chunk')).toHaveLength(1);
    expect(file.cancelled).toBe(true);
  });

  it('ignores acknowledgements and aborts of unknown requests', async () => {
    const events: WorkerEvent[] = [];
    const session = new ImportWorkerSession((event) => events.push(event));

    await session.handle({ type: 'ack', requestId: 99, index: 0 });
    await session.handle({ type: 'abort', requestId: 99 });

    expect(events).toEqual([]);
  });
});

describe('ImportWorkerClient', () => {
  it('profiles a file through the worker', async () => {
    const client = new ImportWorkerClient(new InProcessWorker());

    const { profile, fingerprint } = await client.profile(makeFile(contactsCsv(3)), {}, FIELDS);

    expect(profile.rowsSampled).toBe(3);
    expect(fingerprint).toMatch(/^[0-9a-f]{64}$/);
  });

  it('rejects with ImportWorkerError carrying the worker error code', async () => {
    const client = new ImportWorkerClient(new InProcessWorker());

    const failure = await client.profile(makeFile('x', 'contacts.pdf'), {}, FIELDS).catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(ImportWorkerError);
    expect((failure as ImportWorkerError).code).toBe('unsupportedFormat');
  });

  it('runs a full pass: chunks arrive in order, never more than the window unacknowledged', async () => {
    const worker = new InProcessWorker();
    const client = new ImportWorkerClient(worker);
    const chunks: EncodedChunk[] = [];
    let maxUnacknowledged = 0;
    let unacknowledged = 0;

    const run = client.run(makeFile(noisyContactsCsv(400, 400)), {
      source: {},
      pipeline: pipelineConfig({ targetChunkBytes: 16 * 1024 }),
      window: 3,
      onChunk: (chunk) => {
        chunks.push(chunk);
        unacknowledged++;
        maxUnacknowledged = Math.max(maxUnacknowledged, unacknowledged);
        // Acknowledge later, like an upload that takes a moment.
        setTimeout(() => {
          unacknowledged--;
          run.acknowledge(chunk.payload.index);
        }, 1);
      },
    });
    const result = await run.done;

    expect(result.completed).toBe(true);
    expect(result.profile.rowsValid).toBe(400);
    expect(chunks.map((chunk) => chunk.payload.index)).toEqual(chunks.map((_, index) => index));
    expect(chunks.reduce((rows, chunk) => rows + chunk.payload.rows, 0)).toBe(400);
    expect(maxUnacknowledged).toBeLessThanOrEqual(3);
  });

  it('abort rejects done with code aborted', async () => {
    const client = new ImportWorkerClient(new InProcessWorker());

    const run = client.run(makeFile(noisyContactsCsv(400, 400)), {
      source: {},
      pipeline: pipelineConfig({ targetChunkBytes: 16 * 1024 }),
      window: 1,
      onChunk: () => run.abort(),
    });
    const failure = await run.done.catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(ImportWorkerError);
    expect((failure as ImportWorkerError).code).toBe('aborted');
  });

  it('dispose terminates the worker and rejects open requests', async () => {
    const worker = new InProcessWorker();
    const client = new ImportWorkerClient(worker);

    const pending = client.profile(makeFile(contactsCsv(3)), {}, FIELDS).catch((error: unknown) => error);
    client.dispose();

    expect(worker.terminated).toBe(true);
    expect(await pending).toMatchObject({ code: 'aborted' });
    await expect(client.profile(makeFile(contactsCsv(3)), {}, FIELDS)).rejects.toMatchObject({ code: 'aborted' });
  });

  it('fails open requests when the worker script crashes', async () => {
    const worker = new InProcessWorker();
    const client = new ImportWorkerClient(worker);

    const pending = client.profile(makeFile(contactsCsv(3)), {}, FIELDS).catch((error: unknown) => error);
    worker.onerror?.({ message: 'boom', preventDefault: () => undefined } as unknown as ErrorEvent);

    expect(await pending).toMatchObject({ code: 'failed', message: 'boom' });
  });
});
