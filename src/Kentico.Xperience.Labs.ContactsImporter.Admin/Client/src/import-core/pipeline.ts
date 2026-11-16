import { BloomFilter } from './bloom';
import type { BloomFilterOptions } from './bloom';
import { Chunker } from './chunker';
import type { EncodedChunk } from './chunker';
import type { ImportProfile } from './contract';
import { RowMapper } from './mapping';
import type { RejectReason, RowMapperOptions } from './mapping';
import type { RecordSource, RowErrorCode } from './source';

export const DEFAULT_MAX_REPORTED_ROWS = 100_000;

export interface ImportPipelineOptions extends RowMapperOptions {
  jobId: string;
  /** Target size of one gzipped chunk in bytes (before base64). Default 4 MiB. */
  targetChunkBytes?: number;
  /** Hard cap on the uncompressed NDJSON size of one chunk. Default 32 MiB. */
  maxChunkUncompressedBytes?: number;
  /** Sizing of the duplicate-key Bloom filter (default 10M keys at 1 %); `false` turns the check off. */
  bloom?: BloomFilterOptions | false;
  /** Cap on the rejected rows kept in memory (counts stay exact). Default 100,000. */
  maxRejectedRows?: number;
  /** Cap on the likely-duplicate row numbers kept in memory (count stays exact). Default 100,000. */
  maxSuspectRows?: number;
  /** Stops the pipeline: `chunks()` rejects with the signal's abort reason. */
  signal?: AbortSignal;
  /** Called after every source batch (about once per 64 KiB of input) and once at the end. */
  onProgress?: (progress: PipelineProgress) => void;
}

export interface RejectedRow {
  rowNumber: number;
  reason: RejectReason;
  /** Target field the reason applies to (`valueTooLong`, `invalidDate`). */
  field?: string;
  /** Parser error behind a `parseError`. */
  detail?: RowErrorCode;
}

export interface PipelineProgress {
  /** Bytes of the file read so far / file size. */
  bytesRead: number;
  totalBytes: number;
  /** Running totals in the `status.json` profile shape. */
  profile: ImportProfile;
  /** Uploaded rows flagged `invalidEmail` (email is not the key). */
  invalidEmailRows: number;
}

export interface PipelineResult extends PipelineProgress {
  /** True when the whole file was processed. */
  completed: boolean;
  /** Rows skipped client-side, in file order; capped at `maxRejectedRows`. Never sent to the server. */
  rejectedRows: RejectedRow[];
  rejectedRowsTruncated: boolean;
  /** Exact rejected row counts by reason. */
  rejectedByReason: Partial<Record<RejectReason, number>>;
  /** Row numbers whose key was probably seen earlier in the file; capped at `maxSuspectRows`. */
  suspectRows: number[];
  suspectRowsTruncated: boolean;
}

/**
 * Full pass over a file: parse -> map -> normalize -> duplicate check -> gzipped NDJSON chunks.
 *
 * Pull-based: work happens only while the consumer iterates `chunks()`, so a slow upload
 * slows parsing and memory use stays bounded (one chunk in progress + the Bloom filter).
 * Uses only streams / TextEncoder / CompressionStream / Web Crypto, so it runs in a Web Worker.
 */
export class ImportPipeline {
  private readonly source: RecordSource;
  private readonly options: ImportPipelineOptions;
  private readonly mapper: RowMapper;
  private readonly chunker: Chunker;
  private readonly bloom: BloomFilter | undefined;
  private readonly maxRejectedRows: number;
  private readonly maxSuspectRows: number;

  private rowsRead = 0;
  private rowsValid = 0;
  private rowsRejected = 0;
  private likelyDuplicates = 0;
  private chunkCount = 0;
  private invalidEmailRows = 0;
  private completed = false;
  private started = false;
  private readonly rejectedRows: RejectedRow[] = [];
  private readonly rejectedByReason: Partial<Record<RejectReason, number>> = {};
  private readonly suspectRows: number[] = [];

  /** Throws `ImportError` (`invalidConfig`) when the mapping does not fit the source. */
  constructor(source: RecordSource, options: ImportPipelineOptions) {
    this.source = source;
    this.options = options;
    this.mapper = new RowMapper(source.columns, options, source.dynamicColumns);
    this.chunker = new Chunker({
      jobId: options.jobId,
      targetChunkBytes: options.targetChunkBytes,
      maxChunkUncompressedBytes: options.maxChunkUncompressedBytes,
    });
    this.bloom = options.bloom === false ? undefined : new BloomFilter(options.bloom);
    this.maxRejectedRows = options.maxRejectedRows ?? DEFAULT_MAX_REPORTED_ROWS;
    this.maxSuspectRows = options.maxSuspectRows ?? DEFAULT_MAX_REPORTED_ROWS;
  }

  /** Chunks in file order (`payload.index` 0, 1, ...). Can be iterated once. */
  async *chunks(): AsyncGenerator<EncodedChunk, void, void> {
    if (this.started) {
      throw new Error('The pipeline was already run.');
    }
    this.started = true;

    const { signal, onProgress } = this.options;
    try {
      for await (const batch of this.source.batches()) {
        signal?.throwIfAborted();

        for (let i = 0; i < batch.length; i++) {
          const row = batch[i];
          this.rowsRead++;

          const mapped = this.mapper.map(row);
          if (!mapped.ok) {
            this.reject({
              rowNumber: row.rowNumber,
              reason: mapped.reason,
              ...(mapped.field !== undefined && { field: mapped.field }),
              ...(mapped.detail !== undefined && { detail: mapped.detail }),
            });
            continue;
          }

          const record = mapped.record;
          this.rowsValid++;
          if (record.f) {
            this.invalidEmailRows++;
          }
          if (this.bloom?.add(record.k)) {
            this.likelyDuplicates++;
            if (this.suspectRows.length < this.maxSuspectRows) {
              this.suspectRows.push(record.r);
            }
          }

          if (this.chunker.add(record)) {
            const chunk = await this.chunker.pump();
            if (chunk) {
              this.chunkCount++;
              yield chunk;
              signal?.throwIfAborted();
            }
          }
        }

        onProgress?.(this.progress());
      }

      const last = await this.chunker.finish();
      if (last) {
        this.chunkCount++;
        yield last;
      }
      this.completed = true;
      onProgress?.(this.progress());
    } finally {
      await this.chunker.abort();
      await this.source.cancel();
    }
  }

  /** Running totals; safe to call at any time. */
  progress(): PipelineProgress {
    return {
      bytesRead: this.source.bytesRead,
      totalBytes: this.source.totalBytes,
      profile: {
        rowsRead: this.rowsRead,
        rowsValid: this.rowsValid,
        rowsRejected: this.rowsRejected,
        likelyDuplicates: this.likelyDuplicates,
        chunkCount: this.chunkCount,
      },
      invalidEmailRows: this.invalidEmailRows,
    };
  }

  /** Totals plus the client-side reports; final once `chunks()` finished. */
  result(): PipelineResult {
    return {
      ...this.progress(),
      completed: this.completed,
      rejectedRows: this.rejectedRows,
      rejectedRowsTruncated: this.rowsRejected > this.rejectedRows.length,
      rejectedByReason: { ...this.rejectedByReason },
      suspectRows: this.suspectRows,
      suspectRowsTruncated: this.likelyDuplicates > this.suspectRows.length,
    };
  }

  private reject(row: RejectedRow): void {
    this.rowsRejected++;
    this.rejectedByReason[row.reason] = (this.rejectedByReason[row.reason] ?? 0) + 1;
    if (this.rejectedRows.length < this.maxRejectedRows) {
      this.rejectedRows.push(row);
    }
  }
}

export function createImportPipeline(
  source: RecordSource,
  options: ImportPipelineOptions,
): ImportPipeline {
  return new ImportPipeline(source, options);
}
