import type { ChunkRecord } from './contract';
import type { FileLike, RecordSource, SourceRow } from './source';

export interface TestFile extends FileLike {
  /** Number of chunks the consumer pulled from the stream so far. */
  pulls: number;
  /** True when the stream was cancelled by the consumer. */
  cancelled: boolean;
}

export function utf8(text: string): Uint8Array {
  return new TextEncoder().encode(text);
}

/**
 * In-memory file whose stream hands out `chunkSize` bytes per pull (0 = everything at once)
 * and only when asked (no read-ahead), so tests can check chunk-boundary handling and laziness.
 */
export function makeFile(
  content: string | Uint8Array,
  name = 'test.csv',
  chunkSize = 0,
): TestFile {
  const bytes = typeof content === 'string' ? utf8(content) : content;
  const step = chunkSize > 0 ? chunkSize : Math.max(1, bytes.byteLength);
  const file: TestFile = {
    name,
    size: bytes.byteLength,
    pulls: 0,
    cancelled: false,
    slice: (start = 0, end = bytes.byteLength) => ({
      arrayBuffer: async () => bytes.slice(start, end).buffer as ArrayBuffer,
    }),
    stream: () => {
      let offset = 0;

      return new ReadableStream<Uint8Array>(
        {
          pull(controller) {
            if (offset >= bytes.byteLength) {
              controller.close();

              return;
            }
            file.pulls++;
            controller.enqueue(bytes.slice(offset, offset + step));
            offset += step;
          },
          cancel() {
            file.cancelled = true;
          },
        },
        { highWaterMark: 0 },
      );
    },
  };

  return file;
}

export async function collectRows(source: RecordSource): Promise<SourceRow[]> {
  const rows: SourceRow[] = [];
  for await (const row of source.rows()) {
    rows.push(row);
  }

  return rows;
}

export async function gzipBytes(bytes: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([bytes as BlobPart])
    .stream()
    .pipeThrough(new CompressionStream('gzip'));

  return new Uint8Array(await new Response(stream).arrayBuffer());
}

export async function gunzipBytes(bytes: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([bytes as BlobPart])
    .stream()
    .pipeThrough(new DecompressionStream('gzip'));

  return new Uint8Array(await new Response(stream).arrayBuffer());
}

export function fromBase64(data: string): Uint8Array {
  return Uint8Array.from(atob(data), (char) => char.charCodeAt(0));
}

/** Decodes an `UPLOAD_CHUNK` `data` value back into its records. */
export async function decodeChunkData(data: string): Promise<ChunkRecord[]> {
  const text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(
    await gunzipBytes(fromBase64(data)),
  );

  return text
    .split('\n')
    .filter((line) => line.length > 0)
    .map((line) => JSON.parse(line) as ChunkRecord);
}

/** Deterministic pseudo-random generator (mulberry32) for reproducible test data. */
export function randomSource(seed: number): () => number {
  let state = seed;

  return () => {
    state = (state + 0x6d2b79f5) | 0;
    let t = Math.imul(state ^ (state >>> 15), 1 | state);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;

    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function randomWord(random: () => number, length: number): string {
  let word = '';
  for (let i = 0; i < length; i++) {
    word += String.fromCharCode(97 + Math.floor(random() * 26));
  }

  return word;
}
