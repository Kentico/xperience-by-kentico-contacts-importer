import { describe, expect, it } from "vitest";

import { gzip, toBase64 } from "../import-core";
import type { GetReportRequest, GetReportResponse } from "../shared/commands";

import { ReportDownloadError, downloadReport } from "./report-download";

async function page(
  lines: string[],
  next: Partial<GetReportResponse> = {},
): Promise<GetReportResponse> {
  const text = lines.map((line) => `${line}\n`).join("");

  return {
    code: "ok",
    message: null,
    fileName: "contact-import-analyze-report.csv.gz",
    header: "row,outcome,reason,field",
    data: toBase64(await gzip(new TextEncoder().encode(text))),
    rows: lines.length,
    nextPart: 0,
    nextLine: 0,
    done: false,
    ...next,
  };
}

async function unpack(blob: Blob): Promise<string> {
  const stream = blob.stream().pipeThrough(new DecompressionStream("gzip"));

  return new TextDecoder("utf-8", { ignoreBOM: true }).decode(
    await new Response(stream).arrayBuffer(),
  );
}

const noWait = { sleep: () => Promise.resolve(), random: () => 0.5 };

describe("downloadReport", () => {
  it("joins the pages into one gzip file with a BOM and the header first", async () => {
    const pages = [
      await page(["2,created,,", "3,updated,,"], { nextPart: 2, nextLine: 2 }),
      await page(["4,skipped,invalidEmail,ContactEmail"], {
        nextPart: 4,
        nextLine: 1,
      }),
      await page([], { nextPart: 4, nextLine: 2147483647, done: true }),
    ];
    const requests: GetReportRequest[] = [];
    const progress: [number, number][] = [];

    const file = await downloadReport({
      phase: "analyze",
      call: (request) => {
        requests.push(request);

        return Promise.resolve(pages[requests.length - 1]);
      },
      onProgress: (rows, count) => progress.push([rows, count]),
    });

    expect(requests).toEqual([
      { phase: "analyze", part: 0, line: 0 },
      { phase: "analyze", part: 2, line: 2 },
      { phase: "analyze", part: 4, line: 1 },
    ]);
    expect(file.fileName).toBe("contact-import-analyze-report.csv.gz");
    expect(file.rows).toBe(3);
    expect(file.pages).toBe(3);
    expect(file.blob.type).toBe("application/gzip");
    expect(progress).toEqual([
      [2, 1],
      [3, 2],
      [3, 3],
    ]);
    expect(await unpack(file.blob)).toBe(
      "﻿row,outcome,reason,field\n2,created,,\n3,updated,,\n4,skipped,invalidEmail,ContactEmail\n",
    );
  });

  it("keeps non-ASCII text and large pages intact", async () => {
    const lines = Array.from(
      { length: 20_000 },
      (_, i) => `${i + 2},created,,,"Žluťoučký kůň ${i} 日本"`,
    );
    const file = await downloadReport({
      phase: "execute",
      call: async (request) =>
        request.part === 0
          ? page(lines.slice(0, 12_000), { nextPart: 12_002 })
          : page(lines.slice(12_000), { done: true }),
    });

    const text = await unpack(file.blob);
    expect(text.split("\n")).toHaveLength(20_002);
    expect(text.endsWith(`${lines[19_999]}\n`)).toBe(true);
    expect(file.rows).toBe(20_000);
  });

  it("retries a failed page request", async () => {
    let calls = 0;
    const file = await downloadReport({
      phase: "analyze",
      call: async () =>
        ++calls === 1 ? undefined : page(["2,created,,"], { done: true }),
      retry: noWait,
    });

    expect(calls).toBe(2);
    expect(file.rows).toBe(1);
  });

  it("fails with the server message when a page is refused", async () => {
    const refused: GetReportResponse = {
      ...(await page([])),
      code: "invalidState",
      message: "This report is not available.",
      data: null,
    };

    await expect(
      downloadReport({
        phase: "analyze",
        call: () => Promise.resolve(refused),
      }),
    ).rejects.toThrow(new ReportDownloadError("This report is not available."));
  });

  it("fails instead of looping when the server does not advance", async () => {
    const stuck = await page(["2,created,,"], { nextPart: 0, nextLine: 0 });

    await expect(
      downloadReport({ phase: "analyze", call: () => Promise.resolve(stuck) }),
    ).rejects.toBeInstanceOf(ReportDownloadError);
  });

  it("stops when aborted", async () => {
    const controller = new AbortController();
    const first = await page(["2,created,,"], { nextPart: 2, nextLine: 1 });

    const download = downloadReport({
      phase: "analyze",
      signal: controller.signal,
      call: () => {
        controller.abort(new Error("left the page"));

        return Promise.resolve(first);
      },
    });

    await expect(download).rejects.toThrow("left the page");
  });
});
