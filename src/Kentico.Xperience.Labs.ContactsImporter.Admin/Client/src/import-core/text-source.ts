import { concatBytes } from './bytes';
import { ImportError } from './source';
import type {
  FileLike,
  RecordSource,
  SourceFormat,
  SourceInfo,
  SourceRow,
} from './source';

/** Format-specific part of a text source: turns decoded text into rows. */
export interface TextFormatReader {
  readonly format: SourceFormat;
  readonly dynamicColumns: boolean;
  readonly columns: string[];
  /** True once `columns` is known (header parsed, or end of input). */
  readonly columnsReady: boolean;
  /** Called once, before any `push`, with the start of the text. `complete` = it is the whole file. */
  init(sample: string, complete: boolean): void;
  push(text: string, out: SourceRow[]): void;
  end(out: SourceRow[]): void;
}

export interface TextSourceOptions {
  /** Encoding label for `TextDecoder` (e.g. `windows-1252`). Default / `auto`: BOM, then UTF-8 validity. */
  encoding?: string;
  /** The file is gzip-compressed. */
  compressed: boolean;
  /** Bytes read up front for encoding and dialect detection. Default 64 KiB. */
  sampleBytes?: number;
}

export const DEFAULT_SAMPLE_BYTES = 65_536;

/**
 * Detects the text encoding from the first bytes of a file: byte order mark first, then
 * UTF-8 when the bytes are valid UTF-8, otherwise windows-1252.
 */
export function detectEncoding(head: Uint8Array, complete: boolean): string {
  if (head.length >= 3 && head[0] === 0xef && head[1] === 0xbb && head[2] === 0xbf) {
    return 'utf-8';
  }
  if (head.length >= 2 && head[0] === 0xff && head[1] === 0xfe) {
    return 'utf-16le';
  }
  if (head.length >= 2 && head[0] === 0xfe && head[1] === 0xff) {
    return 'utf-16be';
  }

  try {
    // stream: true tolerates a multi-byte sequence cut off at the end of the sample
    new TextDecoder('utf-8', { fatal: true }).decode(head, { stream: !complete });

    return 'utf-8';
  } catch {
    return 'windows-1252';
  }
}

/** Streaming text-based record source: file bytes -> (gunzip) -> text decoder -> format reader. */
export class TextRecordSource implements RecordSource {
  readonly totalBytes: number;
  bytesRead = 0;

  private readonly reader: ReadableStreamDefaultReader<Uint8Array>;
  private readonly formatReader: TextFormatReader;
  private readonly options: TextSourceOptions;
  private readonly describe: (reader: TextFormatReader) => Partial<SourceInfo>;
  private decoder: TextDecoder | undefined;
  private encoding = 'utf-8';
  private pending: SourceRow[] = [];
  private eof = false;
  private consumed = false;
  private cancelled = false;

  private constructor(
    file: FileLike,
    formatReader: TextFormatReader,
    options: TextSourceOptions,
    describe: (reader: TextFormatReader) => Partial<SourceInfo>,
  ) {
    this.totalBytes = file.size;
    this.formatReader = formatReader;
    this.options = options;
    this.describe = describe;

    let stream = file.stream();
    if (options.compressed) {
      const counter = new TransformStream<Uint8Array, Uint8Array>({
        transform: (chunk, controller) => {
          this.bytesRead += chunk.byteLength;
          controller.enqueue(chunk);
        },
      });
      stream = stream
        .pipeThrough(counter)
        .pipeThrough(
          new DecompressionStream('gzip') as unknown as TransformStream<
            Uint8Array,
            Uint8Array
          >,
        );
    }
    this.reader = stream.getReader();
  }

  /** Opens the file and reads far enough to know the columns. */
  static async open(
    file: FileLike,
    formatReader: TextFormatReader,
    options: TextSourceOptions,
    describe: (reader: TextFormatReader) => Partial<SourceInfo> = () => ({}),
  ): Promise<TextRecordSource> {
    const source = new TextRecordSource(file, formatReader, options, describe);
    try {
      await source.start();
    } catch (error) {
      await source.cancel();
      throw error;
    }

    return source;
  }

  get format(): SourceFormat {
    return this.formatReader.format;
  }

  get columns(): readonly string[] {
    return this.formatReader.columns;
  }

  get dynamicColumns(): boolean {
    return this.formatReader.dynamicColumns;
  }

  get info(): SourceInfo {
    return {
      encoding: this.encoding,
      compressed: this.options.compressed,
      ...this.describe(this.formatReader),
    };
  }

  async *batches(): AsyncGenerator<SourceRow[], void, void> {
    if (this.consumed) {
      throw new ImportError(
        'sourceConsumed',
        'The record source was already read. Open a new source for another pass.',
      );
    }
    this.consumed = true;

    try {
      if (this.pending.length > 0) {
        const first = this.pending;
        this.pending = [];
        yield first;
      }

      while (!this.eof && !this.cancelled) {
        const batch: SourceRow[] = [];
        await this.readNext(batch);
        if (batch.length > 0) {
          yield batch;
        }
      }
    } finally {
      await this.cancel();
    }
  }

  async *rows(): AsyncGenerator<SourceRow, void, void> {
    for await (const batch of this.batches()) {
      yield* batch;
    }
  }

  async cancel(): Promise<void> {
    if (this.cancelled) {
      return;
    }
    this.cancelled = true;
    this.pending = [];
    if (!this.eof) {
      try {
        await this.reader.cancel();
      } catch {
        // the stream already failed; nothing left to release
      }
    }
  }

  private async readBytes(): Promise<Uint8Array | undefined> {
    const { done, value } = await this.reader.read();
    if (done) {
      this.eof = true;

      return undefined;
    }
    if (!this.options.compressed) {
      this.bytesRead += value.byteLength;
    }

    return value;
  }

  private async start(): Promise<void> {
    const sampleBytes = Math.max(4, this.options.sampleBytes ?? DEFAULT_SAMPLE_BYTES);
    const parts: Uint8Array[] = [];
    let length = 0;
    while (length < sampleBytes) {
      const bytes = await this.readBytes();
      if (!bytes) {
        break;
      }
      if (bytes.byteLength > 0) {
        parts.push(bytes);
        length += bytes.byteLength;
      }
    }

    const head = concatBytes(parts);
    const requested = this.options.encoding;
    this.encoding =
      requested && requested.toLowerCase() !== 'auto'
        ? requested
        : detectEncoding(head, this.eof);
    try {
      this.decoder = new TextDecoder(this.encoding);
    } catch {
      throw new ImportError(
        'unsupportedEncoding',
        `Encoding '${this.encoding}' is not supported.`,
      );
    }
    this.encoding = this.decoder.encoding;

    let text = this.decoder.decode(head, { stream: true });
    if (this.eof) {
      text += this.decoder.decode();
    }
    this.formatReader.init(text, this.eof);
    this.formatReader.push(text, this.pending);
    if (this.eof) {
      this.formatReader.end(this.pending);
    }

    while (!this.formatReader.columnsReady && !this.eof) {
      await this.readNext(this.pending);
    }
  }

  private async readNext(out: SourceRow[]): Promise<void> {
    const decoder = this.decoder as TextDecoder;
    const bytes = await this.readBytes();
    if (bytes) {
      const text = decoder.decode(bytes, { stream: true });
      if (text.length > 0) {
        this.formatReader.push(text, out);
      }

      return;
    }

    const tail = decoder.decode();
    if (tail.length > 0) {
      this.formatReader.push(tail, out);
    }
    this.formatReader.end(out);
  }
}
