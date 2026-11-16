import type { GetReportRequest, GetReportResponse, ProcessPhase } from '../shared/commands';
import { withRetry } from '../upload/retry';
import type { RetryOptions } from '../upload/retry';

/**
 * Downloads a row report with the paged `GET_REPORT` page command and joins the pages into one
 * gzipped CSV file (single gzip member, UTF-8 with BOM, header line first).
 *
 * Memory in the browser: one page at a time (a few megabytes of CSV text) plus the gzipped
 * output collected so far, which is the size of the finished `.csv.gz` file.
 */

export interface ReportDownloadOptions {
  phase: ProcessPhase;
  /** Sends one `GET_REPORT`; `undefined` or a rejection is a failed request and is retried. */
  call: (request: GetReportRequest, signal?: AbortSignal) => Promise<GetReportResponse | undefined>;
  signal?: AbortSignal;
  /** Called after every page with the rows received so far. */
  onProgress?: (rows: number, pages: number) => void;
  /** Retry of failed requests (defaults of `withRetry`). */
  retry?: Pick<RetryOptions, 'maxAttempts' | 'baseDelayMs' | 'maxDelayMs' | 'sleep' | 'random'>;
}

export interface ReportFile {
  fileName: string;
  blob: Blob;
  /** Report rows (without the header). */
  rows: number;
  pages: number;
}

/** The server refused a page (no report, import deleted) or returned something unusable. */
export class ReportDownloadError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ReportDownloadError';
  }
}

/** Pages a report can have before the download gives up (2 MB of text each: far beyond any import). */
const MAX_PAGES = 100_000;

export async function downloadReport(options: ReportDownloadOptions): Promise<ReportFile> {
  const { phase, call, signal } = options;
  const encoder = new TextEncoder();
  const output = new CompressionStream('gzip');
  const writer = output.writable.getWriter();
  const collected = collect(output.readable);

  let request: GetReportRequest = { phase, part: 0, line: 0 };
  let fileName = '';
  let rows = 0;
  let pages = 0;

  try {
    for (;;) {
      signal?.throwIfAborted();
      const current = request;
      const page = await withRetry(() => call(current, signal), { ...options.retry, signal });
      if (page.code !== 'ok' || page.data === null) {
        throw new ReportDownloadError(page.message ?? '');
      }

      if (pages === 0) {
        fileName = page.fileName ?? `contact-import-${phase}-report.csv.gz`;
        // The BOM makes spreadsheets read the unpacked file as UTF-8.
        await writer.write(encoder.encode(`﻿${page.header ?? ''}\n`));
      }

      await writer.write(await gunzip(fromBase64(page.data)));
      rows += page.rows;
      pages++;
      options.onProgress?.(rows, pages);

      if (page.done) {
        break;
      }

      const advanced = page.nextPart > current.part || (page.nextPart === current.part && page.nextLine > current.line);
      if (!advanced || pages >= MAX_PAGES) {
        throw new ReportDownloadError('');
      }

      request = { phase, part: page.nextPart, line: page.nextLine };
    }

    await writer.close();
  } catch (error) {
    await writer.abort(error).catch(() => undefined);
    await collected.catch(() => undefined);
    throw error;
  }

  return { fileName, blob: new Blob(await collected, { type: 'application/gzip' }), rows, pages };
}

/** Saves a downloaded file from the browser. */
export function saveBlob(fileName: string, blob: Blob): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.style.display = 'none';
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

async function collect(readable: ReadableStream<Uint8Array>): Promise<Uint8Array<ArrayBuffer>[]> {
  const parts: Uint8Array<ArrayBuffer>[] = [];
  const reader = readable.getReader();
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      return parts;
    }

    parts.push(new Uint8Array(value));
  }
}

async function gunzip(bytes: Uint8Array<ArrayBuffer>): Promise<Uint8Array<ArrayBuffer>> {
  const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'));

  return new Uint8Array(await new Response(stream).arrayBuffer());
}

function fromBase64(text: string): Uint8Array<ArrayBuffer> {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }

  return bytes;
}
