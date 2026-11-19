import { describe, expect, it } from "vitest";

import { JsonArrayReader } from "./json";
import { openRecordSource } from "./open";
import type { SourceRow } from "./source";
import { collectRows, gzipBytes, makeFile, utf8 } from "./test-helpers";

async function read(
  content: string | Uint8Array,
  name = "test.json",
  chunkSize = 0,
) {
  const source = await openRecordSource(makeFile(content, name, chunkSize), {
    sampleBytes: 4,
  });
  const rows = await collectRows(source);

  return { source, rows };
}

/** Feeds the reader the text in the given pieces. */
function readPieces(pieces: string[]): {
  columns: string[];
  rows: SourceRow[];
} {
  const reader = new JsonArrayReader();
  const rows: SourceRow[] = [];
  reader.init();
  for (const piece of pieces) {
    reader.push(piece, rows);
  }
  reader.end();

  return { columns: reader.columns, rows };
}

describe("openRecordSource: JSON array", () => {
  it("reads an array of objects", async () => {
    const { source, rows } = await read(
      '[{"email":"a@b.co","name":"Ann"},\n {"email":"c@d.co","name":"Cid"}]',
    );
    expect(source.format).toBe("json");
    expect(source.dynamicColumns).toBe(true);
    expect(source.columns).toEqual(["email", "name"]);
    expect(rows).toEqual([
      { rowNumber: 1, values: ["a@b.co", "Ann"] },
      { rowNumber: 2, values: ["c@d.co", "Cid"] },
    ]);
  });

  it("keeps the source text of numbers and converts other values like NDJSON", async () => {
    const { source, rows } = await read(
      '[{"id":12345678901234567890,"price":1.50,"exp":1E+21,"neg":-0.0,"ok":true,"no":false,"none":null,' +
        '"address":{"city":"Brno","geo":{"lat":49.20}},"tags":["a", 1.0, {"x" : null}],"empty":{}}]',
    );
    expect(source.columns).toEqual([
      "id",
      "price",
      "exp",
      "neg",
      "ok",
      "no",
      "none",
      "address.city",
      "address.geo.lat",
      "tags",
    ]);
    expect(rows).toEqual([
      {
        rowNumber: 1,
        values: [
          "12345678901234567890",
          "1.50",
          "1E+21",
          "-0.0",
          "true",
          "false",
          "",
          "Brno",
          "49.20",
          '["a",1.0,{"x":null}]',
        ],
      },
    ]);
  });

  it("appends keys first seen in later items", async () => {
    const { source, rows } = await read(
      '[{"a":"1"},{"b":"2"},{"c":"3","a":"4"}]',
    );
    expect(source.columns).toEqual(["a", "b", "c"]);
    expect(rows.map((row) => row.values)).toEqual([
      ["1"],
      ["", "2"],
      ["4", "", "3"],
    ]);
  });

  it("decodes escapes, unicode and a byte order mark", async () => {
    const text =
      '[{"name":"Kůň \\"Šemík\\"\\n\\t\\/\\\\","emoji":"\\ud83d\\ude00 🐎","jp":"\\u65e5\\u672c"}]';
    const bytes = new Uint8Array([0xef, 0xbb, 0xbf, ...utf8(text)]);
    const { rows } = await read(bytes);
    expect(rows).toEqual([
      { rowNumber: 1, values: ['Kůň "Šemík"\n\t/\\', "😀 🐎", "日本"] },
    ]);
  });

  it("flags items that are not objects, numbering rows by item", async () => {
    const { rows } = await read(
      '[{"a":"1"}, 5, "text", null, [1,2], {"a":"2"}]',
    );
    expect(rows).toEqual([
      { rowNumber: 1, values: ["1"] },
      { rowNumber: 2, values: [], error: "notAnObject" },
      { rowNumber: 3, values: [], error: "notAnObject" },
      { rowNumber: 4, values: [], error: "notAnObject" },
      { rowNumber: 5, values: [], error: "notAnObject" },
      { rowNumber: 6, values: ["2"] },
    ]);
  });

  it("reads an empty array", async () => {
    const { source, rows } = await read(" [ ] \n");
    expect(source.columns).toEqual([]);
    expect(rows).toEqual([]);
  });

  it.each([
    ['[{"a":1},{"a":}]', 2],
    ['[{"a":1},]', 2],
    ['[,{"a":1}]', 1],
    ['[{"a":1}{"a":2}]', 1],
    ['[{"a":"x\ny"}]', 1],
    ['[{"a":tru}]', 1],
    ['[{"a":1}] x', 1],
    ['[{"a":1},{"a":2}', 2],
    ['[{"a":"\\x"}]', 1],
  ])("fails on broken JSON %j at item %i", async (text, item) => {
    await expect(read(text)).rejects.toMatchObject({
      code: "invalidJson",
      params: { item },
    });
  });

  it("tells when the file is not an array (e.g. NDJSON saved as .json)", async () => {
    await expect(read('{"a":1}\n{"a":2}\n')).rejects.toMatchObject({
      code: "jsonNotArray",
    });
    await expect(read("   ")).rejects.toMatchObject({ code: "jsonNotArray" });
  });

  it("gives the same rows whatever the stream chunk size", async () => {
    const text =
      '[{"email":"žluťoučký@example.com","n":{"x":"a,b]"}},\r\n{"email":"日本@example.jp","q":"\\"}"}]';
    const expected = [
      { rowNumber: 1, values: ["žluťoučký@example.com", "a,b]"] },
      { rowNumber: 2, values: ["日本@example.jp", "", '"}'] },
    ];
    for (const chunkSize of [0, 1, 2, 3, 5, 16]) {
      expect(
        (await read(utf8(text), "test.json", chunkSize)).rows,
        `chunk size ${chunkSize}`,
      ).toEqual(expected);
    }
  });

  it("gives the same rows when the text is split at any position", () => {
    const text =
      '[ {"a":"x\\"y\\\\","b":{"c":[1,{"d":"]"}]}} , 7 ,{"a":"\\u00e9"} ]';
    const expected = readPieces([text]);
    expect(expected.rows).toHaveLength(3);
    for (let i = 0; i <= text.length; i++) {
      for (let j = i; j <= text.length; j += 7) {
        expect(
          readPieces([text.slice(0, i), text.slice(i, j), text.slice(j)]),
          `split at ${i}, ${j}`,
        ).toEqual(expected);
      }
    }
  });

  it("flattens to a depth of 16 and keeps deeper objects as JSON text", async () => {
    let nested = '{"z":1}';
    for (let i = 0; i < 17; i++) {
      nested = `{"k":${nested}}`;
    }
    const { source, rows } = await read(`[${nested}]`);
    expect(source.columns).toEqual([`${"k.".repeat(16)}k`]);
    expect(rows[0].values).toEqual(['{"z":1}']);
  });

  it("reads gzipped JSON", async () => {
    const items: string[] = [];
    for (let i = 0; i < 1000; i++) {
      items.push(JSON.stringify({ email: `user${i}@example.com`, n: i }));
    }
    const { source, rows } = await read(
      await gzipBytes(utf8(`[${items.join(",\n")}]`)),
      "x.json.gz",
      256,
    );
    expect(source.info.compressed).toBe(true);
    expect(rows).toHaveLength(1000);
    expect(rows[999]).toEqual({
      rowNumber: 1000,
      values: ["user999@example.com", "999"],
    });
  });

  it("fails on an item over the size cap", async () => {
    const source = await openRecordSource(
      makeFile(`[{"a":"1"},{"a":"${"x".repeat(5000)}"}]`, "big.json", 100),
      {
        maxRecordChars: 1000,
        sampleBytes: 4,
      },
    );
    await expect(collectRows(source)).rejects.toMatchObject({
      code: "recordTooLarge",
      params: { item: 2, maxChars: 1000 },
    });
  });
});
