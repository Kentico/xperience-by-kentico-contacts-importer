import { GzipWriter, gzip, sha256Hex, toBase64 } from "./bytes";
import type { ChunkRecord, UploadChunkPayload } from "./contract";
import { ImportError } from "./source";

/** Matches the server default `ContactsImporter:ChunkSizeBytes`; the server accepts up to twice its setting. */
export const DEFAULT_TARGET_CHUNK_BYTES = 4 * 1024 * 1024;
export const DEFAULT_MAX_CHUNK_UNCOMPRESSED_BYTES = 32 * 1024 * 1024;

const WRITE_THRESHOLD = 256 * 1024;
const INITIAL_BUFFER = 64 * 1024;

export interface ChunkerOptions {
  jobId: string;
  /** Target size of one gzipped chunk in bytes (before base64). Default 4 MiB. */
  targetChunkBytes?: number;
  /** Hard cap on the uncompressed NDJSON size of one chunk. Default 32 MiB. */
  maxChunkUncompressedBytes?: number;
  /**
   * Uncompressed bytes gzipped once at the start to estimate the compression ratio.
   * Default: 256 KiB or `targetChunkBytes`, whichever is smaller.
   */
  probeBytes?: number;
}

export interface EncodedChunk {
  /** `UPLOAD_CHUNK` payload, exactly per contract. */
  payload: UploadChunkPayload;
  /** Size of the gzipped chunk (the base64 `data` is 4/3 of this). */
  compressedBytes: number;
  /** Size of the NDJSON before gzip. */
  uncompressedBytes: number;
  /** Row number of the first / last record in the chunk. */
  firstRow: number;
  lastRow: number;
}

/**
 * Turns chunk records into gzipped NDJSON chunks of roughly `targetChunkBytes`.
 *
 * Chunk boundaries are decided per record from uncompressed byte counts and the measured
 * compression ratio (a probe for the first chunk, then the previous chunk), never from stream
 * timing. The same records and options therefore give the same chunks, which lets a resumed
 * upload skip chunks the server already has.
 *
 * Usage: `if (chunker.add(record)) { const chunk = await chunker.pump(); ... }`, then `finish()`.
 */
export class Chunker {
  private readonly jobId: string;
  private readonly targetBytes: number;
  private readonly maxUncompressedBytes: number;
  private readonly probeBytes: number;
  private readonly encoder = new TextEncoder();

  private buffer = new Uint8Array(INITIAL_BUFFER);
  private used = 0;
  private gz: GzipWriter | undefined;
  /** Uncompressed byte limit of the current chunk; unknown until the probe ran. */
  private limit: number | undefined;
  private full = false;
  private index = 0;
  private rows = 0;
  private uncompressedBytes = 0;
  private firstRow = 0;
  private lastRow = 0;

  constructor(options: ChunkerOptions) {
    this.jobId = options.jobId;
    this.targetBytes = options.targetChunkBytes ?? DEFAULT_TARGET_CHUNK_BYTES;
    this.maxUncompressedBytes =
      options.maxChunkUncompressedBytes ?? DEFAULT_MAX_CHUNK_UNCOMPRESSED_BYTES;
    if (!(this.targetBytes > 0) || !(this.maxUncompressedBytes > 0)) {
      throw new ImportError("invalidConfig", "Chunk sizes must be positive.");
    }
    this.probeBytes = Math.min(
      options.probeBytes ?? Math.min(WRITE_THRESHOLD, this.targetBytes),
      this.maxUncompressedBytes,
    );
  }

  /** Number of chunks completed so far (= index of the chunk being filled). */
  get chunkCount(): number {
    return this.index;
  }

  /**
   * Appends a record to the current chunk. Returns true when `pump()` must be awaited before
   * the next `add` (buffered data needs writing, or the chunk is full).
   */
  add(record: ChunkRecord): boolean {
    if (this.full) {
      throw new ImportError(
        "invalidConfig",
        "Chunk is full: await pump() before adding records.",
      );
    }
    if (!(record.r > this.lastRow)) {
      throw new ImportError(
        "invalidConfig",
        `Row numbers must be strictly increasing (got ${record.r} after ${this.lastRow}).`,
      );
    }

    const ordered: ChunkRecord = { r: record.r, k: record.k, v: record.v };
    if (record.f && record.f.length > 0) {
      ordered.f = record.f;
    }
    if (record.c) {
      ordered.c =
        record.c.d === undefined
          ? { a: record.c.a }
          : { a: record.c.a, d: record.c.d };
    }
    const line = `${JSON.stringify(ordered)}\n`;

    // a UTF-16 code unit takes at most 3 bytes in UTF-8
    const needed = this.used + line.length * 3;
    if (needed > this.buffer.length) {
      const grown = new Uint8Array(Math.max(needed, this.buffer.length * 2));
      grown.set(this.buffer.subarray(0, this.used));
      this.buffer = grown;
    }
    const { written } = this.encoder.encodeInto(
      line,
      this.buffer.subarray(this.used),
    );
    this.used += written;
    this.uncompressedBytes += written;

    if (this.rows === 0) {
      this.firstRow = record.r;
    }
    this.rows++;
    this.lastRow = record.r;

    if (this.limit === undefined) {
      return this.uncompressedBytes >= this.probeBytes;
    }
    if (this.uncompressedBytes >= this.limit) {
      this.full = true;
    }

    return this.full || this.used >= WRITE_THRESHOLD;
  }

  /** Writes buffered records to the compressor; returns the chunk when it is complete. */
  async pump(): Promise<EncodedChunk | null> {
    if (this.limit === undefined && this.uncompressedBytes >= this.probeBytes) {
      const probe = await gzip(this.buffer.slice(0, this.used));
      const limit = this.setLimit(this.used / probe.byteLength);
      if (this.uncompressedBytes >= limit) {
        this.full = true;
      }
    }

    if (this.full) {
      return this.close();
    }
    if (this.limit !== undefined && this.used >= WRITE_THRESHOLD) {
      await this.writePending();
    }

    return null;
  }

  /** Completes the last chunk. Returns null when there are no records left. */
  async finish(): Promise<EncodedChunk | null> {
    if (this.rows === 0) {
      return null;
    }

    return this.close();
  }

  /** Releases the compressor without producing output. */
  async abort(): Promise<void> {
    if (this.gz) {
      await this.gz.abort();
      this.gz = undefined;
    }
  }

  private setLimit(ratio: number): number {
    const estimate = Math.floor(this.targetBytes * Math.max(1, ratio));
    const limit = Math.min(estimate, this.maxUncompressedBytes);
    this.limit = limit;

    return limit;
  }

  private async writePending(): Promise<void> {
    if (this.used === 0) {
      return;
    }
    this.gz ??= new GzipWriter();

    const bytes = this.buffer.slice(0, this.used);
    this.used = 0;
    await this.gz.write(bytes);
  }

  private async close(): Promise<EncodedChunk> {
    await this.writePending();
    const gz = this.gz;
    if (!gz) {
      throw new Error("The chunk has no records.");
    }
    this.gz = undefined;
    const compressed = await gz.finish();
    const chunk: EncodedChunk = {
      payload: {
        jobId: this.jobId,
        index: this.index,
        sha256: await sha256Hex(compressed),
        rows: this.rows,
        data: toBase64(compressed),
      },
      compressedBytes: compressed.byteLength,
      uncompressedBytes: this.uncompressedBytes,
      firstRow: this.firstRow,
      lastRow: this.lastRow,
    };

    this.setLimit(this.uncompressedBytes / compressed.byteLength);
    this.index++;
    this.rows = 0;
    this.uncompressedBytes = 0;
    this.firstRow = 0;
    this.full = false;

    return chunk;
  }
}
