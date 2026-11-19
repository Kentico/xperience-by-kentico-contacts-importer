import { describe, expect, it } from "vitest";

import { buildXlsx, buildZip, tableXlsx } from "./format-test-helpers";
import type { XlsxTestOptions } from "./format-test-helpers";
import { openRecordSource } from "./open";
import type { OpenSourceOptions } from "./open";
import { collectRows, makeFile } from "./test-helpers";
import { columnIndex, excelSerialToIso, isDateFormatCode } from "./xlsx";
import { XmlTokenizer, parseXmlAttributes } from "./xml";

async function readXlsx(
  options: XlsxTestOptions,
  open: OpenSourceOptions = {},
  chunkSize = 0,
) {
  const bytes = await buildXlsx(options);
  const source = await openRecordSource(
    makeFile(bytes, "contacts.xlsx", chunkSize),
    open,
  );
  const rows = await collectRows(source);

  return { source, rows, bytes };
}

function events(pieces: string[]): string[] {
  const out: string[] = [];
  const tokenizer = new XmlTokenizer({
    open: (name, raw, selfClosing) =>
      out.push(
        `<${name} ${JSON.stringify(parseXmlAttributes(raw))}${selfClosing ? "/" : ""}>`,
      ),
    close: (name) => out.push(`</${name}>`),
    text: (text) => {
      // text can arrive in pieces: join neighbours
      if (out.length > 0 && out[out.length - 1].startsWith("#")) {
        out[out.length - 1] += text;
      } else {
        out.push(`#${text}`);
      }
    },
  });
  for (const piece of pieces) {
    tokenizer.push(piece);
  }
  tokenizer.end();

  return out;
}

describe("XmlTokenizer", () => {
  it("reports tags, attributes, text, entities and CDATA; drops comments and prefixes", () => {
    expect(
      events([
        '<?xml version="1.0"?><!-- c --><x:a k="1 &amp; 2" x:t=\'q>"\'><b/>A &lt;&#233;&#x1F40E;&gt;<![CDATA[<raw>&amp;]]></x:a>',
      ]),
    ).toEqual([
      '<a {"k":"1 & 2","t":"q>\\""}>',
      "<b {}/>",
      "</b>",
      "#A <é🐎><raw>&amp;",
      "</a>",
    ]);
  });

  it("gives the same events when the text is split at any position", () => {
    const xml =
      '<?xml version="1.0"?><sst><si><t xml:space="preserve">a &amp; b</t></si><!-- x --><si><r><t>x</t></r><rPh><t>y</t></rPh></si><c r="A1" t="s"><v>12</v></c><![CDATA[z]]></sst>';
    const expected = events([xml]);
    for (let i = 0; i <= xml.length; i++) {
      expect(events([xml.slice(0, i), xml.slice(i)]), `split at ${i}`).toEqual(
        expected,
      );
    }
  });
});

describe("XLSX helpers", () => {
  it("converts column letters", () => {
    expect(
      ["A1", "Z9", "AA3", "az10", "XFD1048576", "1"].map(columnIndex),
    ).toEqual([0, 25, 26, 51, 16383, -1]);
  });

  it("detects date formats", () => {
    expect(
      [
        "dd/mm/yyyy",
        "yyyy-mm-dd hh:mm",
        "[$-409]d-mmm-yy",
        "h:mm AM/PM",
        "mmm",
        "[Red]d.m.yyyy;@",
      ].every(isDateFormatCode),
    ).toBe(true);
    expect(
      [
        "General",
        "0.00",
        "#,##0",
        '"Day" 0',
        "0.0E+00",
        "[Red]0.00",
        "\\d0",
        "_m0",
        "0%",
      ].some(isDateFormatCode),
    ).toBe(false);
  });

  it("converts serials in the 1900 date system, incl. its leap year bug", () => {
    expect(excelSerialToIso(1, false)).toBe("1900-01-01");
    expect(excelSerialToIso(59, false)).toBe("1900-02-28");
    expect(excelSerialToIso(60, false)).toBe("1900-02-29");
    expect(excelSerialToIso(61, false)).toBe("1900-03-01");
    expect(excelSerialToIso(45000, false)).toBe("2023-03-15");
    expect(excelSerialToIso(45000.5, false)).toBe("2023-03-15T12:00:00");
    expect(excelSerialToIso(45000.999999999, false)).toBe("2023-03-16");
    expect(excelSerialToIso(36526.75347222222, false)).toBe(
      "2000-01-01T18:05:00",
    );
    expect(excelSerialToIso(2958465, false)).toBe("9999-12-31");
    expect(excelSerialToIso(-1, false)).toBeNull();
    expect(excelSerialToIso(Number.NaN, false)).toBeNull();
  });

  it("converts serials in the 1904 date system", () => {
    expect(excelSerialToIso(0, true)).toBe("1904-01-01");
    expect(excelSerialToIso(43538, true)).toBe("2023-03-15");
  });
});

describe("openRecordSource: XLSX", () => {
  it("reads shared strings (rich text joined, phonetic left out) and numbers as stored", async () => {
    const { source, rows } = await readXlsx({
      sharedStrings: [
        "<si><t>Email</t></si>",
        "<si><t>Name</t></si>",
        "<si><t>ID</t></si>",
        "<si><t>a@b.co</t></si>",
        '<si><r><rPr><b/></rPr><t>An</t></r><r><t xml:space="preserve">n </t></r><rPh sb="0" eb="1"><t>アン</t></rPh></si>',
        "<si><t>line_x000D_\nbreak &amp; more</t></si>",
      ],
      sheets: [
        {
          name: "Contacts",
          rows:
            '<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>' +
            '<row r="2"><c r="A2" t="s"><v>3</v></c><c r="B2" t="s"><v>4</v></c><c r="C2"><v>12345678901234567890</v></c></row>' +
            '<row r="3"><c r="A3" t="s"><v>5</v></c><c r="C3"><v>1.5E-3</v></c></row>',
        },
      ],
    });
    expect(source.format).toBe("xlsx");
    expect(source.dynamicColumns).toBe(false);
    expect(source.info).toEqual({
      encoding: "utf-8",
      compressed: false,
      sheet: "Contacts",
      sheets: ["Contacts"],
    });
    expect(source.columns).toEqual(["Email", "Name", "ID"]);
    expect(rows).toEqual([
      { rowNumber: 1, values: ["a@b.co", "Ann ", "12345678901234567890"] },
      { rowNumber: 2, values: ["line\r\nbreak & more", "", "1.5E-3"] },
    ]);
  });

  it("reads inline strings, formula strings, booleans, errors and ISO date cells", async () => {
    const { rows } = await readXlsx({
      sheets: [
        {
          name: "S",
          rows:
            '<row r="1"><c r="A1" t="inlineStr"><is><t>A</t></is></c><c r="B1" t="inlineStr"><is><t>B</t></is></c><c r="C1" t="inlineStr"><is><t>C</t></is></c><c r="D1" t="inlineStr"><is><t>D</t></is></c><c r="E1" t="inlineStr"><is><t>E</t></is></c></row>' +
            '<row r="2"><c r="A2" t="inlineStr"><is><r><t>x</t></r><r><t>y</t></r></is></c><c r="B2" t="str"><f>A2&amp;"!"</f><v>xy!</v></c><c r="C2" t="b"><v>1</v></c><c r="D2" t="e"><v>#N/A</v></c><c r="E2" t="d"><v>2024-05-06T00:00:00Z</v></c></row>' +
            '<row r="3"><c r="C3" t="b"><v>0</v></c><c r="E3" t="d"><v>2024-05-06T07:08:09</v></c></row>',
        },
      ],
    });
    expect(rows).toEqual([
      { rowNumber: 1, values: ["xy", "xy!", "TRUE", "", "2024-05-06"] },
      { rowNumber: 2, values: ["", "", "FALSE", "", "2024-05-06T07:08:09"] },
    ]);
  });

  it("places sparse cells by reference, counts row gaps and skips empty rows", async () => {
    const { source, rows } = await readXlsx({
      sheets: [
        {
          name: "S",
          rows:
            '<row r="3"><c r="B3" t="inlineStr"><is><t>Email</t></is></c><c r="D3" t="inlineStr"><is><t>Email</t></is></c><c r="E3" s="0"/></row>' +
            '<row r="4"><c r="D4"><v>1</v></c></row>' +
            '<row r="5"><c r="A5" s="0"/></row>' +
            '<row r="8"><c r="A8"><v>2</v></c><c r="F8"><v>3</v></c></row>' +
            // no references: positions follow on
            "<row><c><v>4</v></c><c><v>5</v></c></row>",
        },
      ],
    });
    // empty header cells get names, as for CSV; trailing empty header cells are dropped
    expect(source.columns).toEqual([
      "Column 1",
      "Email",
      "Column 3",
      "Email (2)",
    ]);
    expect(rows).toEqual([
      { rowNumber: 1, values: ["", "", "", "1"] },
      { rowNumber: 5, values: ["2", "", "", "", "", "3"] },
      { rowNumber: 6, values: ["4", "5", "", ""] },
    ]);
  });

  it("converts date-formatted numbers (built-in and custom formats) to ISO", async () => {
    const { rows } = await readXlsx({
      numFmts:
        '<numFmt numFmtId="164" formatCode="dd/mm/yyyy\\ hh:mm"/><numFmt numFmtId="165" formatCode="&quot;Day&quot;\\ 0"/>',
      cellXfs: [0, 14, 164, 165, 22],
      sheets: [
        {
          name: "S",
          rows:
            '<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c><c r="B1" t="inlineStr"><is><t>b</t></is></c><c r="C1" t="inlineStr"><is><t>c</t></is></c><c r="D1" t="inlineStr"><is><t>d</t></is></c><c r="E1" t="inlineStr"><is><t>e</t></is></c></row>' +
            '<row r="2"><c r="A2" s="0"><v>45000</v></c><c r="B2" s="1"><v>45000</v></c><c r="C2" s="2"><v>45000.25</v></c><c r="D2" s="3"><v>45000</v></c><c r="E2" s="4"><v>60</v></c></row>',
        },
      ],
    });
    expect(rows).toEqual([
      {
        rowNumber: 1,
        values: [
          "45000",
          "2023-03-15",
          "2023-03-15T06:00:00",
          "45000",
          "1900-02-29",
        ],
      },
    ]);
  });

  it("uses the 1904 date system when the workbook says so", async () => {
    const { rows } = await readXlsx({
      date1904: true,
      sheets: [
        {
          name: "S",
          rows: '<row r="1"><c r="A1" t="inlineStr"><is><t>d</t></is></c></row><row r="2"><c r="A2" s="1"><v>0</v></c></row>',
        },
      ],
    });
    expect(rows).toEqual([{ rowNumber: 1, values: ["1904-01-01"] }]);
  });

  it("reads the first visible sheet by default and another sheet by name", async () => {
    const sheet = (value: string) =>
      `<row r="1"><c r="A1" t="inlineStr"><is><t>H</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>${value}</t></is></c></row>`;
    const options: XlsxTestOptions = {
      sheets: [
        { name: "Hidden", rows: sheet("hidden"), hidden: true },
        { name: "First", rows: sheet("first") },
        { name: "Second & more", rows: sheet("second") },
      ],
    };
    const first = await readXlsx(options);
    expect(first.source.info.sheets).toEqual([
      "Hidden",
      "First",
      "Second & more",
    ]);
    expect(first.source.info.sheet).toBe("First");
    expect(first.rows[0].values).toEqual(["first"]);

    const second = await readXlsx(options, { sheet: "second & MORE" });
    expect(second.source.info.sheet).toBe("Second & more");
    expect(second.rows[0].values).toEqual(["second"]);

    await expect(readXlsx(options, { sheet: "Nope" })).rejects.toMatchObject({
      code: "sheetNotFound",
      params: { sheet: "Nope" },
    });
  });

  it("reads namespace-prefixed sheet XML and stored packages", async () => {
    const bytes = await buildXlsx({
      method: 0,
      sheets: [
        {
          name: "S",
          rows: '<x:row r="1"><x:c r="A1" t="inlineStr"><x:is><x:t>H</x:t></x:is></x:c></x:row><x:row r="2"><x:c r="A2"><x:v>7</x:v></x:c></x:row>',
        },
      ],
    });
    const source = await openRecordSource(makeFile(bytes, "book.xlsx"));
    expect(source.columns).toEqual(["H"]);
    expect(await collectRows(source)).toEqual([
      { rowNumber: 1, values: ["7"] },
    ]);
  });

  it("streams a larger sheet with progress, the same with shared and inline strings", async () => {
    const table = [["Email", "Name"]];
    for (let i = 0; i < 5000; i++) {
      table.push([
        `user${i}@example.com`,
        i % 3 === 0 ? "" : `Name ${i % 100}`,
      ]);
    }
    const results = [];
    for (const shared of [true, false]) {
      const bytes = await tableXlsx(table, shared);
      const source = await openRecordSource(makeFile(bytes, "big.xlsx", 4096));
      const rows = await collectRows(source);
      expect(source.bytesRead).toBe(bytes.length);
      expect(source.totalBytes).toBe(bytes.length);
      results.push(rows);
    }
    expect(results[0]).toHaveLength(5000);
    expect(results[0][4999]).toEqual({
      rowNumber: 5000,
      values: ["user4999@example.com", "Name 99"],
    });
    expect(results[1]).toEqual(results[0]);
  });

  it("has no rows and no columns for an empty sheet", async () => {
    const { source, rows } = await readXlsx({
      sheets: [{ name: "S", rows: "" }],
    });
    expect(source.columns).toEqual([]);
    expect(rows).toEqual([]);
  });

  it("fails on files that are no XLSX workbook", async () => {
    await expect(
      openRecordSource(makeFile("Email\na@b.co", "renamed.xlsx")),
    ).rejects.toMatchObject({ code: "invalidXlsx" });
    const zipWithoutWorkbook = await buildZip([
      { name: "[Content_Types].xml", data: "<Types/>" },
      { name: "word/document.xml", data: "<document/>" },
    ]);
    await expect(
      openRecordSource(makeFile(zipWithoutWorkbook, "letter.xlsx")),
    ).rejects.toMatchObject({ code: "invalidXlsx" });
    await expect(
      openRecordSource(makeFile("\xd0\xcf\x11\xe0", "old.xls")),
    ).rejects.toMatchObject({
      code: "unsupportedFormat",
      params: { extension: "xls" },
    });
  });
});
