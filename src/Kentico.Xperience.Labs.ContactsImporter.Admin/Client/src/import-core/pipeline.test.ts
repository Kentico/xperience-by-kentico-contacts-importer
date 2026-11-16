import { describe, expect, it } from 'vitest';

import type { EncodedChunk } from './chunker';
import type { ChunkRecord, TargetField } from './contract';
import { openRecordSource } from './open';
import type { OpenSourceOptions } from './open';
import { createImportPipeline } from './pipeline';
import type { ImportPipelineOptions, PipelineProgress } from './pipeline';
import { ImportError } from './source';
import {
  decodeChunkData,
  fromBase64,
  gunzipBytes,
  makeFile,
  randomSource,
  randomWord,
} from './test-helpers';

const FIELDS: TargetField[] = [
  { name: 'ContactEmail', caption: 'Email', dataType: 'text', maxLength: 254 },
  { name: 'ContactFirstName', caption: 'First name', dataType: 'text', maxLength: 100 },
  { name: 'ContactBirthday', caption: 'Birthday', dataType: 'datetime' },
  { name: 'CustomerID', caption: 'Customer ID', dataType: 'text', maxLength: 10 },
];

const EMAIL_KEY: ImportPipelineOptions = {
  jobId: '3f0e8a0e-5b7b-4f0a-9d5e-0a3b7f0c1d2e',
  keyField: 'ContactEmail',
  mapping: [
    { source: 'Email', target: 'ContactEmail' },
    { source: 'First Name', target: 'ContactFirstName' },
  ],
};

async function runPipeline(
  content: string,
  options: ImportPipelineOptions,
  file: { name?: string; chunkSize?: number; open?: OpenSourceOptions } = {},
) {
  const source = await openRecordSource(
    makeFile(content, file.name ?? 'contacts.csv', file.chunkSize ?? 0),
    file.open,
  );
  const pipeline = createImportPipeline(source, options);
  const chunks: EncodedChunk[] = [];
  for await (const chunk of pipeline.chunks()) {
    chunks.push(chunk);
  }
  const records: ChunkRecord[] = [];
  for (const chunk of chunks) {
    records.push(...(await decodeChunkData(chunk.payload.data)));
  }

  return { pipeline, chunks, records, result: pipeline.result() };
}

function bigCsv(rows: number, seed = 3): string {
  const random = randomSource(seed);
  const lines = ['Email,First Name,Notes'];
  for (let i = 0; i < rows; i++) {
    lines.push(`${randomWord(random, 10)}${i}@${randomWord(random, 6)}.com,${randomWord(random, 7)},"note, ${randomWord(random, 20)}"`);
  }

  return lines.join('\r\n');
}

describe('import pipeline', () => {
  it('maps, normalizes and chunks a file; only mapped columns leave', async () => {
    const csv =
      'Email,First Name,Customer,Secret\n' +
      ' Jane@Example.COM ,  Jane ,C1,s3cret-1\n' +
      'not-an-email,Bob,C2,s3cret-2\n' +
      ' ,NoKey,C3,s3cret-3\n' +
      'bob@example.com,,C4,s3cret-4\n' +
      'JANE@example.com,Dup,C5,s3cret-5\n';
    const { chunks, records, result } = await runPipeline(csv, EMAIL_KEY);

    expect(records).toEqual([
      { r: 1, k: 'jane@example.com', v: { ContactEmail: 'jane@example.com', ContactFirstName: 'Jane' } },
      { r: 4, k: 'bob@example.com', v: { ContactEmail: 'bob@example.com', ContactFirstName: '' } },
      { r: 5, k: 'jane@example.com', v: { ContactEmail: 'jane@example.com', ContactFirstName: 'Dup' } },
    ]);

    expect(chunks).toHaveLength(1);
    expect(chunks[0].payload).toMatchObject({ jobId: EMAIL_KEY.jobId, index: 0, rows: 3 });
    const ndjson = new TextDecoder().decode(await gunzipBytes(fromBase64(chunks[0].payload.data)));
    expect(ndjson.split('\n')[0]).toBe(
      '{"r":1,"k":"jane@example.com","v":{"ContactEmail":"jane@example.com","ContactFirstName":"Jane"}}',
    );
    expect(ndjson).not.toContain('s3cret');
    expect(ndjson).not.toContain('C1');

    expect(result.completed).toBe(true);
    expect(result.profile).toEqual({
      rowsRead: 5,
      rowsValid: 3,
      rowsRejected: 2,
      likelyDuplicates: 1,
      chunkCount: 1,
    });
    expect(result.rejectedRows).toEqual([
      { rowNumber: 2, reason: 'invalidEmailKey' },
      { rowNumber: 3, reason: 'missingKey' },
    ]);
    expect(result.rejectedByReason).toEqual({ invalidEmailKey: 1, missingKey: 1 });
    expect(result.rejectedRowsTruncated).toBe(false);
    expect(result.suspectRows).toEqual([5]);
    expect(result.suspectRowsTruncated).toBe(false);
    expect(result.invalidEmailRows).toBe(0);
    expect(result.bytesRead).toBe(result.totalBytes);
  });

  it('flags an invalid email instead of skipping the row when email is not the key (D34)', async () => {
    const csv =
      'Customer,Email\n' +
      ' c1 ,Jane@Example.com\n' +
      'C2,not-an-email\n' +
      'C3,\n' +
      ',orphan@example.com\n' +
      `${'x'.repeat(451)},long@example.com\n`;
    const { records, result } = await runPipeline(csv, {
      jobId: 'job',
      keyField: 'CustomerID',
      mapping: [
        { source: 'Customer', target: 'CustomerID' },
        { source: 'Email', target: 'ContactEmail' },
      ],
    });

    expect(records).toEqual([
      { r: 1, k: 'c1', v: { CustomerID: 'c1', ContactEmail: 'jane@example.com' } },
      { r: 2, k: 'C2', v: { CustomerID: 'C2', ContactEmail: 'not-an-email' }, f: ['invalidEmail'] },
      { r: 3, k: 'C3', v: { CustomerID: 'C3', ContactEmail: '' } },
    ]);
    expect(result.invalidEmailRows).toBe(1);
    expect(result.rejectedRows).toEqual([
      { rowNumber: 4, reason: 'missingKey' },
      { rowNumber: 5, reason: 'keyTooLong' },
    ]);
  });

  it('converts dates to ISO and rejects rows with invalid dates or too long values', async () => {
    const csv =
      'Email;First Name;Born\n' +
      'a@example.com;Ann;31.1.1990\n' +
      'b@example.com;Bob;1990-02-28T10:00\n' +
      'c@example.com;Cid;31.2.1990\n' +
      'd@example.com;Dan;\n' +
      `e@example.com;${'E'.repeat(101)};1.1.1990\n` +
      'bad;Fay;99.99.1990\n';
    const { records, result } = await runPipeline(csv, {
      ...EMAIL_KEY,
      mapping: [...EMAIL_KEY.mapping, { source: 'Born', target: 'ContactBirthday' }],
      fields: FIELDS,
      dateFormat: 'dmy',
    });

    expect(records.map((record) => [record.r, record.v.ContactBirthday])).toEqual([
      [1, '1990-01-31'],
      [2, '1990-02-28T10:00:00'],
      [4, ''],
    ]);
    expect(result.rejectedRows).toEqual([
      { rowNumber: 3, reason: 'invalidDate', field: 'ContactBirthday' },
      { rowNumber: 5, reason: 'valueTooLong', field: 'ContactFirstName' },
      // the key problem wins over other problems of the row
      { rowNumber: 6, reason: 'invalidEmailKey' },
    ]);
  });

  it('rejects rows whose record would exceed the server record size limit', async () => {
    const csv = `Email,First Name
a@example.com,${'🐎'.repeat(300_000)}
b@example.com,${'x'.repeat(900_000)}
`;
    const { records, result } = await runPipeline(csv, EMAIL_KEY, { open: { maxRecordChars: 2_000_000 } });
    expect(records.map((record) => record.r)).toEqual([2]);
    expect(result.rejectedRows).toEqual([{ rowNumber: 1, reason: 'recordTooLarge' }]);
  });

  it('reports unparsable rows as rejected', async () => {
    const ndjson = '{"email":"a@example.com"}\n{broken\n\n[1]\n{"email":"b@example.com","name":"Bea"}\n';
    const { records, result } = await runPipeline(
      ndjson,
      {
        jobId: 'job',
        keyField: 'ContactEmail',
        mapping: [
          { source: 'email', target: 'ContactEmail' },
          // column that first appears in a later row
          { source: 'name', target: 'ContactFirstName' },
        ],
      },
      { name: 'contacts.ndjson' },
    );

    expect(records).toEqual([
      { r: 1, k: 'a@example.com', v: { ContactEmail: 'a@example.com', ContactFirstName: '' } },
      { r: 5, k: 'b@example.com', v: { ContactEmail: 'b@example.com', ContactFirstName: 'Bea' } },
    ]);
    expect(result.rejectedRows).toEqual([
      { rowNumber: 2, reason: 'parseError', detail: 'invalidJson' },
      { rowNumber: 4, reason: 'parseError', detail: 'notAnObject' },
    ]);
    expect(result.profile).toMatchObject({ rowsRead: 4, rowsValid: 2, rowsRejected: 2 });
  });

  it('treats ragged rows as empty values', async () => {
    const { records, result } = await runPipeline('First Name,Email\nAnn\nBob,b@example.com,extra\n', EMAIL_KEY);
    expect(records).toEqual([
      { r: 2, k: 'b@example.com', v: { ContactEmail: 'b@example.com', ContactFirstName: 'Bob' } },
    ]);
    expect(result.rejectedRows).toEqual([{ rowNumber: 1, reason: 'missingKey' }]);
  });

  it('validates the configuration against the source', async () => {
    const open = () => openRecordSource(makeFile('Email,First Name\na@b.co,Ann\n'));
    const expectInvalid = async (options: ImportPipelineOptions) => {
      const source = await open();
      expect(() => createImportPipeline(source, options)).toThrowError(ImportError);
      await source.cancel();
    };

    await expectInvalid({ ...EMAIL_KEY, mapping: [] });
    await expectInvalid({ ...EMAIL_KEY, keyField: 'CustomerID' });
    await expectInvalid({ ...EMAIL_KEY, mapping: [...EMAIL_KEY.mapping, { source: 'Missing', target: 'CustomerID' }] });
    await expectInvalid({ ...EMAIL_KEY, mapping: [...EMAIL_KEY.mapping, { source: 'Email', target: 'ContactEmail' }] });
    await expectInvalid({ ...EMAIL_KEY, mapping: [...EMAIL_KEY.mapping, { source: 'Email', target: 'Not A Field' }] });
    await expectInvalid({ ...EMAIL_KEY, fields: [{ name: 'ContactEmail' }] });
  });

  it('caps the in-memory reports but keeps exact counts', async () => {
    const lines = ['Email,First Name'];
    for (let i = 0; i < 50; i++) {
      lines.push('dup@example.com,Dup', 'invalid,Bad');
    }
    const { result } = await runPipeline(lines.join('\n'), {
      ...EMAIL_KEY,
      maxRejectedRows: 10,
      maxSuspectRows: 5,
    });

    expect(result.profile).toMatchObject({ rowsRead: 100, rowsValid: 50, rowsRejected: 50, likelyDuplicates: 49 });
    expect(result.rejectedRows).toHaveLength(10);
    expect(result.rejectedRowsTruncated).toBe(true);
    expect(result.rejectedByReason).toEqual({ invalidEmailKey: 50 });
    expect(result.suspectRows).toEqual([3, 5, 7, 9, 11]);
    expect(result.suspectRowsTruncated).toBe(true);
  });

  it('can run without the duplicate check', async () => {
    const { result } = await runPipeline('Email,First Name\na@b.co,A\na@b.co,B\n', { ...EMAIL_KEY, bloom: false });
    expect(result.profile).toMatchObject({ rowsValid: 2, likelyDuplicates: 0 });
    expect(result.suspectRows).toEqual([]);
  });

  it('produces the same chunks whatever the stream chunking, in file order', async () => {
    const rows = 12_000;
    const csv = bigCsv(rows);
    const options: ImportPipelineOptions = { ...EMAIL_KEY, targetChunkBytes: 48 * 1024 };

    const whole = await runPipeline(csv, options);
    expect(whole.chunks.length).toBeGreaterThan(4);
    expect(whole.result.profile).toEqual({
      rowsRead: rows,
      rowsValid: rows,
      rowsRejected: 0,
      likelyDuplicates: whole.result.suspectRows.length,
      chunkCount: whole.chunks.length,
    });
    // random 10-letter local parts + row index: no real duplicates, only Bloom false positives
    expect(whole.result.profile.likelyDuplicates).toBeLessThan(5);

    expect(whole.records).toHaveLength(rows);
    whole.records.forEach((record, i) => expect(record.r).toBe(i + 1));
    whole.chunks.forEach((chunk, i) => expect(chunk.payload.index).toBe(i));
    expect(whole.chunks.reduce((sum, chunk) => sum + chunk.payload.rows, 0)).toBe(rows);

    for (const chunkSize of [777, 4096]) {
      const other = await runPipeline(csv, options, { chunkSize, open: { sampleBytes: 1024 } });
      expect(other.chunks.map((c) => c.payload.sha256), `chunk size ${chunkSize}`).toEqual(
        whole.chunks.map((c) => c.payload.sha256),
      );
    }
  });

  it('is pull-based: reads the file only as chunks are consumed, and reports progress', async () => {
    const csv = bigCsv(12_000, 11);
    const file = makeFile(csv, 'contacts.csv', 4096);
    const source = await openRecordSource(file, { sampleBytes: 4096 });
    const progress: PipelineProgress[] = [];
    const pipeline = createImportPipeline(source, {
      ...EMAIL_KEY,
      targetChunkBytes: 48 * 1024,
      onProgress: (p) => progress.push(p),
    });
    expect(file.pulls).toBeLessThanOrEqual(2);

    const chunks = pipeline.chunks();
    const first = await chunks.next();
    expect(first.done).toBe(false);
    const afterFirst = pipeline.progress();
    expect(afterFirst.profile.chunkCount).toBe(1);
    expect(afterFirst.bytesRead).toBeGreaterThan(0);
    expect(afterFirst.bytesRead).toBeLessThan(file.size / 2);
    const pullsAfterFirst = file.pulls;

    // nothing is read while the consumer is busy (e.g. uploading)
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(file.pulls).toBe(pullsAfterFirst);

    let count = 1;
    for (;;) {
      const next = await chunks.next();
      if (next.done) {
        break;
      }
      count++;
    }
    const result = pipeline.result();
    expect(result.completed).toBe(true);
    expect(result.profile.chunkCount).toBe(count);
    expect(result.bytesRead).toBe(file.size);

    expect(progress.length).toBeGreaterThan(10);
    for (let i = 1; i < progress.length; i++) {
      expect(progress[i].bytesRead).toBeGreaterThanOrEqual(progress[i - 1].bytesRead);
      expect(progress[i].profile.rowsRead).toBeGreaterThanOrEqual(progress[i - 1].profile.rowsRead);
    }
    expect(progress[progress.length - 1].profile).toEqual(result.profile);
  });

  it('stops and releases the file when the consumer stops early', async () => {
    const file = makeFile(bigCsv(12_000, 5), 'contacts.csv', 4096);
    const pipeline = createImportPipeline(await openRecordSource(file, { sampleBytes: 4096 }), {
      ...EMAIL_KEY,
      targetChunkBytes: 48 * 1024,
    });
    for await (const chunk of pipeline.chunks()) {
      expect(chunk.payload.index).toBe(0);
      break;
    }

    expect(file.cancelled).toBe(true);
    expect(pipeline.result().completed).toBe(false);
  });

  it('stops when aborted', async () => {
    const file = makeFile(bigCsv(12_000, 5), 'contacts.csv', 4096);
    const controller = new AbortController();
    const pipeline = createImportPipeline(await openRecordSource(file, { sampleBytes: 4096 }), {
      ...EMAIL_KEY,
      targetChunkBytes: 48 * 1024,
      signal: controller.signal,
    });

    const consume = async () => {
      for await (const chunk of pipeline.chunks()) {
        if (chunk.payload.index === 1) {
          controller.abort();
        }
      }
    };
    await expect(consume()).rejects.toMatchObject({ name: 'AbortError' });
    expect(file.cancelled).toBe(true);
    expect(pipeline.result().completed).toBe(false);
    expect(pipeline.result().profile.chunkCount).toBe(2);
  });

  it('can be run only once', async () => {
    const source = await openRecordSource(makeFile('Email,First Name\na@b.co,Ann\n'));
    const pipeline = createImportPipeline(source, EMAIL_KEY);
    for await (const chunk of pipeline.chunks()) {
      expect(chunk.payload.rows).toBe(1);
    }
    await expect(pipeline.chunks().next()).rejects.toThrowError(/already run/);
  });

  it('streams a larger file through a gzip source', async () => {
    const rows = 100_000;
    const random = randomSource(21);
    let produced = 0;
    const encoder = new TextEncoder();
    const raw = new ReadableStream<Uint8Array>({
      pull(controller) {
        if (produced === 0) {
          controller.enqueue(encoder.encode('Email,First Name\n'));
        }
        if (produced >= rows) {
          controller.close();

          return;
        }
        let text = '';
        for (let i = 0; i < 1000; i++, produced++) {
          text += `user${produced}@${randomWord(random, 6)}.com,${randomWord(random, 8)}\n`;
        }
        controller.enqueue(encoder.encode(text));
      },
    });
    const compressor = new CompressionStream('gzip') as unknown as TransformStream<Uint8Array, Uint8Array>;
    const gz = new Uint8Array(await new Response(raw.pipeThrough(compressor)).arrayBuffer());

    const source = await openRecordSource(makeFile(gz, 'contacts.csv.gz', 16 * 1024));
    const pipeline = createImportPipeline(source, { ...EMAIL_KEY, targetChunkBytes: 256 * 1024 });
    let rowsInChunks = 0;
    let lastRow = 0;
    for await (const chunk of pipeline.chunks()) {
      rowsInChunks += chunk.payload.rows;
      expect(chunk.firstRow).toBe(lastRow + 1);
      lastRow = chunk.lastRow;
      expect(chunk.compressedBytes).toBeLessThan(256 * 1024 * 1.25);
    }

    const result = pipeline.result();
    expect(rowsInChunks).toBe(rows);
    expect(lastRow).toBe(rows);
    expect(result.profile).toMatchObject({ rowsRead: rows, rowsValid: rows, rowsRejected: 0 });
    expect(result.profile.chunkCount).toBeGreaterThan(3);
    // 100k unique keys in a filter sized for 10M: practically no false positives
    expect(result.profile.likelyDuplicates).toBeLessThan(10);
    expect(result.bytesRead).toBe(gz.byteLength);
  });
});
