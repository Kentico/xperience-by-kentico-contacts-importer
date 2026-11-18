import { concatBytes } from "./bytes";
import { ImportError } from "./source";
import type { FileLike } from "./source";

/** One file (or directory) of a zip archive, from the central directory. */
export interface ZipEntry {
  readonly name: string;
  /** 0 = stored, 8 = deflate; anything else is not supported. */
  readonly method: number;
  /** General purpose bit flags (bit 0 = encrypted). */
  readonly flags: number;
  readonly compressedSize: number;
  readonly uncompressedSize: number;
  readonly localHeaderOffset: number;
}

const EOCD_SIGNATURE = 0x06054b50;
const ZIP64_LOCATOR_SIGNATURE = 0x07064b50;
const ZIP64_EOCD_SIGNATURE = 0x06064b50;
const CENTRAL_SIGNATURE = 0x02014b50;
const LOCAL_SIGNATURE = 0x04034b50;
const EOCD_SIZE = 22;
const MAX_COMMENT = 0xffff;
const LOCAL_HEADER_SIZE = 30;
const CENTRAL_HEADER_SIZE = 46;
/** Central directory larger than this is not a contact file (and would not fit memory well). */
const MAX_CENTRAL_DIRECTORY_BYTES = 64 * 1024 * 1024;

/** Bytes read from the file per pull when streaming an entry. */
export const ZIP_READ_BYTES = 1024 * 1024;

export const ZIP_STORED = 0;
export const ZIP_DEFLATE = 8;

function invalid(message: string): ImportError {
  return new ImportError("invalidZip", message);
}

async function readRange(
  file: FileLike,
  start: number,
  end: number,
): Promise<DataView> {
  if (start < 0 || end > file.size || start > end) {
    throw invalid("A zip record points outside the file.");
  }
  const buffer = await file.slice(start, end).arrayBuffer();
  if (buffer.byteLength !== end - start) {
    throw invalid("The zip file is shorter than its directory says.");
  }

  return new DataView(buffer);
}

/** Little-endian unsigned 64-bit value as a number; fails above 2^53 (no such file fits a browser). */
function uint64(view: DataView, offset: number): number {
  const low = view.getUint32(offset, true);
  const high = view.getUint32(offset + 4, true);
  const value = high * 0x1_0000_0000 + low;
  if (!Number.isSafeInteger(value)) {
    throw invalid("A zip size or offset is too large.");
  }

  return value;
}

/** `true` when the bytes start with the local file header signature `PK\x03\x04` (or an empty zip). */
export function hasZipSignature(head: Uint8Array): boolean {
  return (
    head.length >= 4 &&
    head[0] === 0x50 &&
    head[1] === 0x4b &&
    ((head[2] === 0x03 && head[3] === 0x04) ||
      (head[2] === 0x05 && head[3] === 0x06))
  );
}

/**
 * Random-access zip reader over a `FileLike` (`File.slice`): reads the central directory (ZIP64
 * aware) and streams single entries, inflating deflate entries with the platform
 * `DecompressionStream('deflate-raw')`. Only the directory is held in memory.
 */
export class ZipArchive {
  readonly file: FileLike;
  readonly entries: readonly ZipEntry[];
  private readonly byName: Map<string, ZipEntry>;

  private constructor(file: FileLike, entries: ZipEntry[]) {
    this.file = file;
    this.entries = entries;
    this.byName = new Map();
    for (const entry of entries) {
      this.byName.set(entry.name, entry);
      const lower = entry.name.toLowerCase();
      if (!this.byName.has(lower)) {
        this.byName.set(lower, entry);
      }
    }
  }

  static async open(file: FileLike): Promise<ZipArchive> {
    if (file.size < EOCD_SIZE) {
      throw invalid("The file is too short to be a zip file.");
    }

    const tailStart = Math.max(0, file.size - EOCD_SIZE - MAX_COMMENT);
    const tail = await readRange(file, tailStart, file.size);
    let eocd = -1;
    for (let i = tail.byteLength - EOCD_SIZE; i >= 0; i--) {
      if (tail.getUint32(i, true) === EOCD_SIGNATURE) {
        eocd = i;
        break;
      }
    }
    if (eocd === -1) {
      throw invalid("The end of the zip central directory was not found.");
    }

    let count = tail.getUint16(eocd + 10, true);
    let size = tail.getUint32(eocd + 12, true);
    let offset = tail.getUint32(eocd + 16, true);

    if (count === 0xffff || size === 0xffffffff || offset === 0xffffffff) {
      const locatorAt = tailStart + eocd - 20;
      if (locatorAt >= 0) {
        const locator = await readRange(file, locatorAt, locatorAt + 20);
        if (locator.getUint32(0, true) === ZIP64_LOCATOR_SIGNATURE) {
          const recordAt = uint64(locator, 8);
          const record = await readRange(file, recordAt, recordAt + 56);
          if (record.getUint32(0, true) !== ZIP64_EOCD_SIGNATURE) {
            throw invalid(
              "The ZIP64 end of central directory record is damaged.",
            );
          }
          count = uint64(record, 32);
          size = uint64(record, 40);
          offset = uint64(record, 48);
        }
      }
    }

    if (size > MAX_CENTRAL_DIRECTORY_BYTES) {
      throw invalid("The zip central directory is too large.");
    }

    const directory = await readRange(file, offset, offset + size);
    const entries = readCentralDirectory(directory, count);

    return new ZipArchive(file, entries);
  }

  /** Entry by exact name, or by name ignoring case. */
  find(name: string): ZipEntry | undefined {
    return this.byName.get(name) ?? this.byName.get(name.toLowerCase());
  }

  /** Offset of the entry data (after the local header). */
  async dataOffset(entry: ZipEntry): Promise<number> {
    const header = await readRange(
      this.file,
      entry.localHeaderOffset,
      Math.min(this.file.size, entry.localHeaderOffset + LOCAL_HEADER_SIZE),
    );
    if (
      header.byteLength < LOCAL_HEADER_SIZE ||
      header.getUint32(0, true) !== LOCAL_SIGNATURE
    ) {
      throw invalid(
        `The local header of zip entry '${entry.name}' is damaged.`,
      );
    }

    const start =
      entry.localHeaderOffset +
      LOCAL_HEADER_SIZE +
      header.getUint16(26, true) +
      header.getUint16(28, true);
    if (start + entry.compressedSize > this.file.size) {
      throw invalid(`Zip entry '${entry.name}' goes past the end of the file.`);
    }

    return start;
  }

  /**
   * Decompressed bytes of an entry as a stream. `onRead` gets the number of compressed bytes each
   * time a piece of the file is read (progress).
   */
  async stream(
    entry: ZipEntry,
    onRead?: (compressedBytes: number) => void,
    readBytes: number = ZIP_READ_BYTES,
  ): Promise<ReadableStream<Uint8Array>> {
    checkEntry(entry);
    const start = await this.dataOffset(entry);
    const end = start + entry.compressedSize;
    const file = this.file;
    let position = start;

    const raw = new ReadableStream<Uint8Array>(
      {
        async pull(controller) {
          if (position >= end) {
            controller.close();

            return;
          }
          const next = Math.min(end, position + readBytes);
          const bytes = new Uint8Array(
            await file.slice(position, next).arrayBuffer(),
          );
          position = next;
          onRead?.(bytes.byteLength);
          controller.enqueue(bytes);
        },
      },
      { highWaterMark: 0 },
    );

    if (entry.method === ZIP_STORED) {
      return raw;
    }

    const inflated = raw.pipeThrough(
      new DecompressionStream("deflate-raw") as unknown as TransformStream<
        Uint8Array,
        Uint8Array
      >,
    );

    return withZipErrors(inflated, entry.name);
  }

  /** The whole decompressed entry (for small parts such as the XLSX workbook). */
  async readBytes(entry: ZipEntry): Promise<Uint8Array> {
    const reader = (await this.stream(entry)).getReader();
    const parts: Uint8Array[] = [];
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      parts.push(value);
    }

    return concatBytes(parts);
  }

  /**
   * The entry as a random-access file of its own. Stored entries are a view into this file (no
   * copy); deflated entries are decompressed once into a `Blob` (memory = uncompressed size).
   */
  async entryFile(entry: ZipEntry): Promise<FileLike> {
    checkEntry(entry);
    if (entry.method === ZIP_STORED) {
      const start = await this.dataOffset(entry);

      return sliceFile(this.file, start, entry.compressedSize, entry.name);
    }

    const reader = (await this.stream(entry)).getReader();
    const parts: Uint8Array[] = [];
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      parts.push(value);
    }
    const blob = new Blob(parts as BlobPart[]);

    return {
      name: entry.name,
      size: blob.size,
      stream: () => blob.stream(),
      slice: (start, end) => blob.slice(start, end),
    };
  }
}

function checkEntry(entry: ZipEntry): void {
  if ((entry.flags & 1) !== 0) {
    throw new ImportError(
      "zipEncrypted",
      `Zip entry '${entry.name}' is encrypted.`,
    );
  }
  if (entry.method !== ZIP_STORED && entry.method !== ZIP_DEFLATE) {
    throw new ImportError(
      "unsupportedCompression",
      `Zip entry '${entry.name}' uses compression method ${entry.method}; only stored and deflate are supported.`,
      { method: entry.method },
    );
  }
}

/** A window of a file as a file of its own. */
function sliceFile(
  file: FileLike,
  offset: number,
  size: number,
  name: string,
): FileLike {
  const view: FileLike = {
    name,
    size,
    slice: (start = 0, end = size) => {
      const from = offset + Math.max(0, Math.min(size, start));
      const to = offset + Math.max(0, Math.min(size, end));

      return file.slice(from, Math.max(from, to));
    },
    stream: () => {
      let position = 0;

      return new ReadableStream<Uint8Array>(
        {
          async pull(controller) {
            if (position >= size) {
              controller.close();

              return;
            }
            const next = Math.min(size, position + ZIP_READ_BYTES);
            const bytes = new Uint8Array(
              await view.slice(position, next).arrayBuffer(),
            );
            position = next;
            controller.enqueue(bytes);
          },
        },
        { highWaterMark: 0 },
      );
    },
  };

  return view;
}

/** Turns inflate failures (damaged data) into `invalidZip`. */
function withZipErrors(
  stream: ReadableStream<Uint8Array>,
  name: string,
): ReadableStream<Uint8Array> {
  const reader = stream.getReader();

  return new ReadableStream<Uint8Array>(
    {
      async pull(controller) {
        let result: ReadableStreamReadResult<Uint8Array>;
        try {
          result = await reader.read();
        } catch {
          controller.error(
            invalid(
              `Zip entry '${name}' could not be decompressed; the file is damaged.`,
            ),
          );

          return;
        }
        if (result.done) {
          controller.close();
        } else {
          controller.enqueue(result.value);
        }
      },
      async cancel(reason) {
        await reader.cancel(reason);
      },
    },
    { highWaterMark: 0 },
  );
}

function readCentralDirectory(view: DataView, count: number): ZipEntry[] {
  const entries: ZipEntry[] = [];
  const utf8 = new TextDecoder("utf-8");
  let at = 0;
  while (
    at + CENTRAL_HEADER_SIZE <= view.byteLength &&
    entries.length < count
  ) {
    if (view.getUint32(at, true) !== CENTRAL_SIGNATURE) {
      throw invalid("The zip central directory is damaged.");
    }

    const flags = view.getUint16(at + 8, true);
    const method = view.getUint16(at + 10, true);
    let compressedSize = view.getUint32(at + 20, true);
    let uncompressedSize = view.getUint32(at + 24, true);
    const nameLength = view.getUint16(at + 28, true);
    const extraLength = view.getUint16(at + 30, true);
    const commentLength = view.getUint16(at + 32, true);
    let localHeaderOffset = view.getUint32(at + 42, true);

    const nameStart = at + CENTRAL_HEADER_SIZE;
    const extraStart = nameStart + nameLength;
    const next = extraStart + extraLength + commentLength;
    if (next > view.byteLength) {
      throw invalid("The zip central directory is damaged.");
    }
    // Names without the UTF-8 flag are CP437; ASCII names (the usual case) read the same.
    const name = utf8.decode(
      new Uint8Array(view.buffer, view.byteOffset + nameStart, nameLength),
    );

    // ZIP64 extended information: 64-bit values for the fields set to 0xFFFFFFFF, in this order.
    for (let e = extraStart; e + 4 <= extraStart + extraLength;) {
      const id = view.getUint16(e, true);
      const length = view.getUint16(e + 2, true);
      if (id === 0x0001) {
        let field = e + 4;
        const fieldEnd = field + length;
        if (uncompressedSize === 0xffffffff && field + 8 <= fieldEnd) {
          uncompressedSize = uint64(view, field);
          field += 8;
        }
        if (compressedSize === 0xffffffff && field + 8 <= fieldEnd) {
          compressedSize = uint64(view, field);
          field += 8;
        }
        if (localHeaderOffset === 0xffffffff && field + 8 <= fieldEnd) {
          localHeaderOffset = uint64(view, field);
        }
      }
      e += 4 + length;
    }

    entries.push({
      name,
      method,
      flags,
      compressedSize,
      uncompressedSize,
      localHeaderOffset,
    });
    at = next;
  }

  if (entries.length < count) {
    throw invalid(
      "The zip central directory has fewer entries than its end record says.",
    );
  }

  return entries;
}

/** Directory entries and files that archivers add on their own (macOS resource forks, Finder / Explorer metadata). */
export function isZipJunk(name: string): boolean {
  if (name.endsWith("/") || name.endsWith("\\")) {
    return true;
  }
  const parts = name.split(/[\\/]/);
  if (parts.includes("__MACOSX")) {
    return true;
  }
  const base = parts[parts.length - 1];

  return (
    base === ".DS_Store" ||
    base === "Thumbs.db" ||
    base === "desktop.ini" ||
    base.startsWith("._")
  );
}

/** The one data file of a zip; fails when there is none or more than one. */
export function singleDataEntry(archive: ZipArchive): ZipEntry {
  const data = archive.entries.filter((entry) => !isZipJunk(entry.name));
  if (data.length === 0) {
    throw new ImportError("zipEmpty", "The zip file holds no data file.");
  }
  if (data.length > 1) {
    throw new ImportError(
      "zipMultipleEntries",
      `The zip file holds ${data.length} files; it must hold exactly one.`,
      { count: data.length },
    );
  }

  return data[0];
}

/** File name part of a zip entry path. */
export function baseName(path: string): string {
  const slash = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));

  return slash === -1 ? path : path.slice(slash + 1);
}
