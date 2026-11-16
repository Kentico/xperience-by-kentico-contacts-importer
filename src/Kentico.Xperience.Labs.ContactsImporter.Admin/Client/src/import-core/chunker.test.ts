import { describe, expect, it } from 'vitest';

import { sha256Hex } from './bytes';
import { Chunker } from './chunker';
import type { ChunkerOptions, EncodedChunk } from './chunker';
import type { ChunkRecord } from './contract';
import {
  decodeChunkData,
  fromBase64,
  gunzipBytes,
  randomSource,
  randomWord,
} from './test-helpers';

function makeRecords(count: number, seed = 1): ChunkRecord[] {
  const random = randomSource(seed);
  const records: ChunkRecord[] = [];
  for (let i = 0; i < count; i++) {
    const email = `${randomWord(random, 10)}@${randomWord(random, 8)}.com`;
    const record: ChunkRecord = {
      // gaps, as rejected rows leave them
      r: i * 2 + 1,
      k: email,
      v: { ContactEmail: email, ContactFirstName: randomWord(random, 6), ContactLastName: 'Žluťoučký 🐎' },
    };
    if (i % 50 === 0) {
      record.f = ['invalidEmail'];
    }
    records.push(record);
  }

  return records;
}

async function run(records: ChunkRecord[], options: Partial<ChunkerOptions> = {}): Promise<EncodedChunk[]> {
  const chunker = new Chunker({ jobId: 'job-1', ...options });
  const chunks: EncodedChunk[] = [];
  for (const record of records) {
    if (chunker.add(record)) {
      const chunk = await chunker.pump();
      if (chunk) {
        chunks.push(chunk);
      }
    }
  }
  const last = await chunker.finish();
  if (last) {
    chunks.push(last);
  }

  return chunks;
}

describe('Chunker', () => {
  it('writes records as gzipped NDJSON per contract', async () => {
    const records: ChunkRecord[] = [
      { r: 1, k: 'jane@example.com', v: { ContactEmail: 'jane@example.com', ContactFirstName: 'Jane' } },
      // property order and an empty flag list are normalized
      { f: [], v: { CustomerID: 'C2', ContactEmail: '' }, k: 'C2', r: 12 } as ChunkRecord,
      { r: 13, k: 'C3', v: { ContactEmail: 'nope' }, f: ['invalidEmail'] },
    ];
    const chunks = await run(records);
    expect(chunks).toHaveLength(1);

    const { payload } = chunks[0];
    expect(Object.keys(payload)).toEqual(['jobId', 'index', 'sha256', 'rows', 'data']);
    expect(payload).toMatchObject({ jobId: 'job-1', index: 0, rows: 3 });

    const gz = fromBase64(payload.data);
    expect(gz[0]).toBe(0x1f);
    expect(gz[1]).toBe(0x8b);
    expect(payload.sha256).toBe(await sha256Hex(gz));
    expect(payload.sha256).toMatch(/^[0-9a-f]{64}$/);
    expect(chunks[0].compressedBytes).toBe(gz.byteLength);

    const ndjson = await gunzipBytes(gz);
    expect(chunks[0].uncompressedBytes).toBe(ndjson.byteLength);
    // no BOM, \n separated, \n terminated
    expect(new TextDecoder('utf-8', { ignoreBOM: true }).decode(ndjson)).toBe(
      '{"r":1,"k":"jane@example.com","v":{"ContactEmail":"jane@example.com","ContactFirstName":"Jane"}}\n' +
        '{"r":12,"k":"C2","v":{"CustomerID":"C2","ContactEmail":""}}\n' +
        '{"r":13,"k":"C3","v":{"ContactEmail":"nope"},"f":["invalidEmail"]}\n',
    );
    expect(chunks[0]).toMatchObject({ firstRow: 1, lastRow: 13 });
  });

  it('returns nothing when there are no records', async () => {
    expect(await run([])).toEqual([]);
  });

  it('splits into chunks near the target size, in order, without losing records', async () => {
    const records = makeRecords(20_000);
    const target = 64 * 1024;
    const chunks = await run(records, { targetChunkBytes: target });
    expect(chunks.length).toBeGreaterThan(5);

    const decoded: ChunkRecord[] = [];
    let previousLastRow = 0;
    for (const [i, chunk] of chunks.entries()) {
      expect(chunk.payload.index).toBe(i);
      const gz = fromBase64(chunk.payload.data);
      expect(chunk.payload.sha256).toBe(await sha256Hex(gz));
      expect(gz.byteLength).toBe(chunk.compressedBytes);
      expect(chunk.compressedBytes).toBeLessThan(target * 1.25);
      if (i < chunks.length - 1) {
        expect(chunk.compressedBytes).toBeGreaterThan(target * 0.75);
      }

      const chunkRecords = await decodeChunkData(chunk.payload.data);
      expect(chunkRecords).toHaveLength(chunk.payload.rows);
      expect(chunkRecords[0].r).toBe(chunk.firstRow);
      expect(chunkRecords[chunkRecords.length - 1].r).toBe(chunk.lastRow);
      expect(chunk.firstRow).toBeGreaterThan(previousLastRow);
      previousLastRow = chunk.lastRow;
      decoded.push(...chunkRecords);
    }

    expect(decoded).toEqual(records);
    for (let i = 1; i < decoded.length; i++) {
      expect(decoded[i].r).toBeGreaterThan(decoded[i - 1].r);
    }
  });

  it('gives identical chunks for identical input (resume can skip uploaded chunks)', async () => {
    const records = makeRecords(8000, 7);
    const first = await run(records, { targetChunkBytes: 32 * 1024 });
    const second = await run(records, { targetChunkBytes: 32 * 1024 });
    expect(first.length).toBeGreaterThan(3);
    expect(second.map((c) => c.payload)).toEqual(first.map((c) => c.payload));
  });

  it('caps the uncompressed size of a chunk for highly compressible data', async () => {
    const records: ChunkRecord[] = [];
    for (let i = 1; i <= 5000; i++) {
      records.push({ r: i, k: 'same@example.com', v: { ContactEmail: 'same@example.com', ContactNotes: 'x'.repeat(200) } });
    }
    const cap = 100 * 1024;
    const chunks = await run(records, { targetChunkBytes: 64 * 1024, maxChunkUncompressedBytes: cap });
    expect(chunks.length).toBeGreaterThan(5);
    for (const chunk of chunks) {
      // the record that crosses the cap is still part of the chunk
      expect(chunk.uncompressedBytes).toBeLessThan(cap + 400);
    }
    expect(chunks.reduce((sum, chunk) => sum + chunk.payload.rows, 0)).toBe(5000);
  });

  it('requires strictly increasing row numbers', async () => {
    const chunker = new Chunker({ jobId: 'job-1' });
    chunker.add({ r: 5, k: 'a', v: { K: 'a' } });
    expect(() => chunker.add({ r: 5, k: 'b', v: { K: 'b' } })).toThrowError(/strictly increasing/);
    expect(() => chunker.add({ r: 4, k: 'b', v: { K: 'b' } })).toThrowError(/strictly increasing/);
    await chunker.abort();
  });

  it('handles records larger than its internal buffer', async () => {
    const big = '🐎'.repeat(100_000);
    const chunks = await run([
      { r: 1, k: 'a', v: { K: 'a', Notes: big } },
      { r: 2, k: 'b', v: { K: 'b', Notes: big } },
    ]);
    const decoded = (await Promise.all(chunks.map((c) => decodeChunkData(c.payload.data)))).flat();
    expect(decoded.map((record) => record.v.Notes)).toEqual([big, big]);
  });
});
