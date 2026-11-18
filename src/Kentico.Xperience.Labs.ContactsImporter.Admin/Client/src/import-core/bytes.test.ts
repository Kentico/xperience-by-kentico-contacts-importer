import { describe, expect, it } from "vitest";

import {
  GzipWriter,
  gzip,
  sha256Hex,
  toBase64,
  toBase64Fallback,
  toHex,
} from "./bytes";
import { fingerprintFile } from "./fingerprint";
import { fromBase64, gunzipBytes, makeFile, utf8 } from "./test-helpers";

describe("bytes", () => {
  it("encodes hex", () => {
    expect(toHex(new Uint8Array([0, 1, 15, 16, 255]))).toBe("00010f10ff");
  });

  it("hashes with SHA-256", async () => {
    expect(await sha256Hex(utf8("abc"))).toBe(
      "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
    );
    expect(await sha256Hex(new Uint8Array(0))).toBe(
      "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    );
  });

  it("encodes base64 (native and fallback agree, with padding)", () => {
    expect(toBase64(utf8(""))).toBe("");
    expect(toBase64(utf8("f"))).toBe("Zg==");
    expect(toBase64(utf8("fo"))).toBe("Zm8=");
    expect(toBase64(utf8("foo"))).toBe("Zm9v");
    expect(toBase64Fallback(utf8("fo"))).toBe("Zm8=");

    const bytes = new Uint8Array(200_001);
    for (let i = 0; i < bytes.length; i++) {
      bytes[i] = (i * 31 + 7) & 0xff;
    }
    const encoded = toBase64(bytes);
    expect(toBase64Fallback(bytes)).toBe(encoded);
    expect(fromBase64(encoded)).toEqual(bytes);
  });

  it("gzips in one shot and incrementally to the same content", async () => {
    const text = "line of text that repeats\n".repeat(5000);
    expect(
      new TextDecoder().decode(await gunzipBytes(await gzip(utf8(text)))),
    ).toBe(text);

    const writer = new GzipWriter();
    for (let i = 0; i < 5000; i++) {
      await writer.write(utf8("line of text that repeats\n"));
    }
    const compressed = await writer.finish();
    expect(compressed[0]).toBe(0x1f);
    expect(compressed[1]).toBe(0x8b);
    expect(new TextDecoder().decode(await gunzipBytes(compressed))).toBe(text);
  });
});

describe("fingerprintFile", () => {
  it("hashes size + whole content for small files", async () => {
    const file = makeFile("hello", "a.csv");
    expect(await fingerprintFile(file)).toBe(await sha256Hex(utf8("5:hello")));
  });

  it("hashes size + first and last edge for large files", async () => {
    const content = "0123456789abcdefghij";
    expect(await fingerprintFile(makeFile(content), 4)).toBe(
      await sha256Hex(utf8("20:0123ghij")),
    );
    // exactly two edges: whole file
    expect(await fingerprintFile(makeFile("01234567"), 4)).toBe(
      await sha256Hex(utf8("8:01234567")),
    );
  });

  it("changes with content at either end and with size, not with the name", async () => {
    const base = await fingerprintFile(
      makeFile("0123456789abcdefghij", "a.csv"),
      4,
    );
    expect(
      await fingerprintFile(makeFile("0123456789abcdefghij", "b.csv"), 4),
    ).toBe(base);
    expect(await fingerprintFile(makeFile("X123456789abcdefghij"), 4)).not.toBe(
      base,
    );
    expect(await fingerprintFile(makeFile("0123456789abcdefghiX"), 4)).not.toBe(
      base,
    );
    expect(
      await fingerprintFile(makeFile("0123456789Zabcdefghij"), 4),
    ).not.toBe(base);
    expect(base).toMatch(/^[0-9a-f]{64}$/);
  });

  it("reads at most two edges of a real Blob", async () => {
    const blob = new Blob([new Uint8Array(3 * 1024 * 1024 + 5)]);
    const sliced: [number, number][] = [];
    const file = {
      size: blob.size,
      stream: () => blob.stream() as ReadableStream<Uint8Array>,
      slice: (start = 0, end = blob.size) => {
        sliced.push([start, end]);

        return blob.slice(start, end);
      },
    };
    expect(await fingerprintFile(file)).toMatch(/^[0-9a-f]{64}$/);
    expect(sliced).toEqual([
      [0, 1024 * 1024],
      [blob.size - 1024 * 1024, blob.size],
    ]);
  });
});
