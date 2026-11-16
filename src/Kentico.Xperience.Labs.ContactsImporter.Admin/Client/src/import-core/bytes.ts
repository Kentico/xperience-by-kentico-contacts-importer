const HEX = Array.from({ length: 256 }, (_, i) => i.toString(16).padStart(2, '0'));

export function toHex(bytes: Uint8Array): string {
  let result = '';
  for (let i = 0; i < bytes.length; i++) {
    result += HEX[bytes[i]];
  }

  return result;
}

/** Base64 via `btoa`, in pieces so large inputs do not overflow the argument limit. */
export function toBase64Fallback(bytes: Uint8Array): string {
  const step = 0x8000;
  let binary = '';
  for (let i = 0; i < bytes.length; i += step) {
    binary += String.fromCharCode.apply(
      null,
      bytes.subarray(i, i + step) as unknown as number[],
    );
  }

  return btoa(binary);
}

/** Standard base64 (with padding). */
export function toBase64(bytes: Uint8Array): string {
  const native = (bytes as { toBase64?: () => string }).toBase64;

  return typeof native === 'function' ? native.call(bytes) : toBase64Fallback(bytes);
}

/** Lowercase hex SHA-256 (Web Crypto). */
export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', bytes as BufferSource);

  return toHex(new Uint8Array(digest));
}

export function concatBytes(parts: readonly Uint8Array[]): Uint8Array {
  let length = 0;
  for (const part of parts) {
    length += part.byteLength;
  }

  const result = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    result.set(part, offset);
    offset += part.byteLength;
  }

  return result;
}

/**
 * Incremental gzip (`CompressionStream`). Output is collected while input is written, so
 * memory holds only the compressed bytes.
 */
export class GzipWriter {
  private readonly writer: WritableStreamDefaultWriter<Uint8Array>;
  private readonly drained: Promise<void>;
  private readonly parts: Uint8Array[] = [];

  constructor() {
    const stream = new CompressionStream('gzip') as unknown as TransformStream<
      Uint8Array,
      Uint8Array
    >;
    this.writer = stream.writable.getWriter();
    const reader = stream.readable.getReader();
    this.drained = (async () => {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          return;
        }
        this.parts.push(value);
      }
    })();
    // a failure surfaces through write() / finish(); avoid an unhandled rejection meanwhile
    this.drained.catch(() => undefined);
  }

  /** Writes bytes; the caller must not reuse the buffer afterwards. */
  async write(bytes: Uint8Array): Promise<void> {
    await this.writer.write(bytes);
  }

  /** Ends the stream and returns the whole gzip file. */
  async finish(): Promise<Uint8Array> {
    await this.writer.close();
    await this.drained;

    return concatBytes(this.parts);
  }

  /** Releases the stream without producing output. */
  async abort(): Promise<void> {
    try {
      await this.writer.abort();
    } catch {
      // already closed or failed
    }
  }
}

/** One-shot gzip. */
export async function gzip(bytes: Uint8Array): Promise<Uint8Array> {
  const writer = new GzipWriter();
  await writer.write(bytes);

  return writer.finish();
}
