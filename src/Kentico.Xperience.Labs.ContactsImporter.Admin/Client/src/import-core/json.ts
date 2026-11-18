import { DEFAULT_MAX_RECORD_CHARS } from "./csv";
import { MAX_FLATTEN_DEPTH } from "./ndjson";
import { ImportError } from "./source";
import type { SourceRow } from "./source";
import type { TextFormatReader } from "./text-source";

export interface JsonArrayReaderOptions {
  /** Cap on the characters of one array item. */
  maxRecordChars?: number;
}

const Phase = {
  /** Before the opening `[`. */
  Start: 0,
  /** Inside the top-level array. */
  Items: 1,
  /** After the closing `]`. */
  Done: 2,
} as const;

// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u001f]/;
const NUMBER = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/y;

function isWhitespace(code: number): boolean {
  return code === 0x20 || code === 0x0a || code === 0x0d || code === 0x09;
}

/**
 * Reader of a JSON file whose top level is an array of objects (`[ {…}, {…} ]`), streaming: the
 * array is split into items as text arrives and only one item is held at a time.
 * - Same column rules as NDJSON: keys in first-seen order (later keys appended), nested objects
 *   flattened to dotted paths, arrays kept as compact JSON text.
 * - Values as text: numbers keep their source text (`1.50`, long IDs), `true` / `false`, `null` = empty.
 * - Row number = 1-based item index. An item that is not an object is yielded with `notAnObject`.
 * - Broken JSON is fatal (`invalidJson` with the item index): the stream cannot resynchronize.
 */
export class JsonArrayReader implements TextFormatReader {
  readonly format = "json" as const;
  readonly dynamicColumns = true;
  readonly columns: string[] = [];
  columnsReady = false;

  private readonly maxRecordChars: number;
  private readonly columnIndex = new Map<string, number>();
  private phase: number = Phase.Start;
  /** Text of the current item from earlier pieces. */
  private carry = "";
  private depth = 0;
  private inString = false;
  /** A backslash ended the previous piece inside a string. */
  private escapePending = false;
  /** Items completed so far. */
  private item = 0;
  /** A comma was seen after the last item (an item must follow). */
  private afterComma = false;

  constructor(options: JsonArrayReaderOptions = {}) {
    this.maxRecordChars = options.maxRecordChars ?? DEFAULT_MAX_RECORD_CHARS;
  }

  init(): void {
    // no dialect to detect
  }

  push(text: string, out: SourceRow[]): void {
    const length = text.length;
    let i = 0;

    if (this.phase === Phase.Start) {
      while (
        i < length &&
        (isWhitespace(text.charCodeAt(i)) || text.charCodeAt(i) === 0xfeff)
      ) {
        i++;
      }
      if (i === length) {
        return;
      }
      if (text.charCodeAt(i) !== 0x5b /* [ */) {
        throw new ImportError(
          "jsonNotArray",
          "The JSON file does not start with an array ([). For one object per line, use NDJSON.",
        );
      }
      this.phase = Phase.Items;
      i++;
    }

    let start = i;
    // Next backslash at or after `i` (-1 = none left); searched again only once passed, so a piece
    // without backslashes is not scanned to its end for every string.
    let backslash = -2;
    while (i < length && this.phase === Phase.Items) {
      if (this.escapePending) {
        this.escapePending = false;
        i++;
        continue;
      }
      if (this.inString) {
        const quote = text.indexOf('"', i);
        if (backslash !== -1 && backslash < i) {
          backslash = text.indexOf("\\", i);
        }
        if (backslash !== -1 && (quote === -1 || backslash < quote)) {
          if (backslash + 1 < length) {
            i = backslash + 2;
          } else {
            this.escapePending = true;
            i = length;
          }
          continue;
        }
        if (quote === -1) {
          i = length;
          break;
        }
        this.inString = false;
        i = quote + 1;
        continue;
      }

      const code = text.charCodeAt(i);
      if (code === 0x22 /* " */) {
        this.inString = true;
      } else if (code === 0x7b /* { */ || code === 0x5b /* [ */) {
        this.depth++;
      } else if (code === 0x7d /* } */ || code === 0x5d /* ] */) {
        if (this.depth === 0) {
          if (code === 0x7d) {
            throw this.invalid();
          }
          // end of the top-level array
          this.finishItem(this.carry + text.slice(start, i), true, out);
          this.carry = "";
          this.phase = Phase.Done;
          i++;
          start = i;
          break;
        }
        this.depth--;
      } else if (code === 0x2c /* , */ && this.depth === 0) {
        this.finishItem(this.carry + text.slice(start, i), false, out);
        this.carry = "";
        start = i + 1;
      }
      i++;
    }

    if (this.phase === Phase.Items) {
      if (start < length) {
        this.carry += start === 0 ? text : text.slice(start);
      }
      if (this.carry.length > this.maxRecordChars) {
        throw new ImportError(
          "recordTooLarge",
          `Item ${this.item + 1} of the JSON array is larger than ${this.maxRecordChars} characters.`,
          { item: this.item + 1, maxChars: this.maxRecordChars },
        );
      }

      return;
    }

    if (this.phase === Phase.Done) {
      for (let j = i; j < length; j++) {
        if (!isWhitespace(text.charCodeAt(j))) {
          throw new ImportError(
            "invalidJson",
            "Unexpected text after the end of the JSON array.",
            {
              item: this.item,
            },
          );
        }
      }
    }
  }

  end(): void {
    if (this.phase === Phase.Start) {
      throw new ImportError(
        "jsonNotArray",
        "The JSON file is empty or does not hold an array.",
      );
    }
    if (this.phase === Phase.Items) {
      throw new ImportError(
        "invalidJson",
        "The JSON array is not closed; the file ends too early.",
        {
          item: this.item + 1,
        },
      );
    }
    this.columnsReady = true;
  }

  private invalid(): ImportError {
    return new ImportError(
      "invalidJson",
      `Item ${this.item + 1} of the JSON array is not valid JSON.`,
      {
        item: this.item + 1,
      },
    );
  }

  private finishItem(raw: string, closing: boolean, out: SourceRow[]): void {
    const text = raw.trim();
    if (text.length === 0) {
      // `[]` is an empty array; `[,` / `,,` / `,]` are not JSON
      if (closing && this.item === 0 && !this.afterComma) {
        this.columnsReady = true;

        return;
      }
      throw this.invalid();
    }
    this.afterComma = !closing;
    this.item++;

    const parser = new JsonItemParser(text, this.columns, this.columnIndex);
    let row: SourceRow;
    try {
      row = parser.parseItem(this.item);
    } catch (error) {
      if (error instanceof JsonSyntaxError) {
        throw new ImportError(
          "invalidJson",
          `Item ${this.item} of the JSON array is not valid JSON.`,
          {
            item: this.item,
          },
        );
      }
      throw error;
    }
    out.push(row);
    this.columnsReady = true;
  }
}

class JsonSyntaxError extends Error {}

/** Recursive-descent parser of one complete array item that writes flattened values. */
class JsonItemParser {
  private readonly s: string;
  private readonly columns: string[];
  private readonly columnIndex: Map<string, number>;
  private i = 0;

  constructor(s: string, columns: string[], columnIndex: Map<string, number>) {
    this.s = s;
    this.columns = columns;
    this.columnIndex = columnIndex;
  }

  parseItem(rowNumber: number): SourceRow {
    this.skipWhitespace();
    if (this.s.charCodeAt(this.i) !== 0x7b) {
      this.compact();
      this.end();

      return { rowNumber, values: [], error: "notAnObject" };
    }

    const values: string[] = new Array<string>(this.columns.length).fill("");
    this.flattenObject("", values, 0);
    this.end();

    return { rowNumber, values };
  }

  private end(): void {
    this.skipWhitespace();
    if (this.i !== this.s.length) {
      throw new JsonSyntaxError();
    }
  }

  private fail(): never {
    throw new JsonSyntaxError();
  }

  private skipWhitespace(): void {
    const s = this.s;
    while (this.i < s.length && isWhitespace(s.charCodeAt(this.i))) {
      this.i++;
    }
  }

  private expect(code: number): void {
    this.skipWhitespace();
    if (this.s.charCodeAt(this.i) !== code) {
      this.fail();
    }
    this.i++;
  }

  private setValue(name: string, value: string, values: string[]): void {
    let index = this.columnIndex.get(name);
    if (index === undefined) {
      index = this.columns.length;
      this.columns.push(name);
      this.columnIndex.set(name, index);
    }
    while (values.length < index) {
      values.push("");
    }
    values[index] = value;
  }

  /** At `{`: flattens the object's members into `values`. */
  private flattenObject(prefix: string, values: string[], depth: number): void {
    this.i++; // {
    this.skipWhitespace();
    if (this.s.charCodeAt(this.i) === 0x7d) {
      this.i++;

      return;
    }

    for (;;) {
      this.skipWhitespace();
      if (this.s.charCodeAt(this.i) !== 0x22) {
        this.fail();
      }
      const name = prefix + this.parseString();
      this.expect(0x3a /* : */);
      this.skipWhitespace();

      const code = this.s.charCodeAt(this.i);
      if (code === 0x7b && depth < MAX_FLATTEN_DEPTH) {
        this.flattenObject(`${name}.`, values, depth + 1);
      } else {
        this.setValue(name, this.scalarText(), values);
      }

      this.skipWhitespace();
      const next = this.s.charCodeAt(this.i);
      this.i++;
      if (next === 0x2c) {
        continue;
      }
      if (next === 0x7d) {
        return;
      }
      this.fail();
    }
  }

  /** Text of a value as the import shows it: strings as is, arrays / deep objects as compact JSON. */
  private scalarText(): string {
    const code = this.s.charCodeAt(this.i);
    if (code === 0x22) {
      return this.parseString();
    }
    if (code === 0x6e /* n */) {
      this.literal("null");

      return "";
    }
    if (code === 0x74 /* t */) {
      this.literal("true");

      return "true";
    }
    if (code === 0x66 /* f */) {
      this.literal("false");

      return "false";
    }
    if (code === 0x5b || code === 0x7b) {
      return this.compact();
    }

    return this.parseNumber();
  }

  /** Any value as compact JSON text (numbers keep their source text). */
  private compact(): string {
    this.skipWhitespace();
    const s = this.s;
    const code = s.charCodeAt(this.i);
    if (code === 0x22) {
      return JSON.stringify(this.parseString());
    }
    if (code === 0x5b || code === 0x7b) {
      const object = code === 0x7b;
      const close = object ? 0x7d : 0x5d;
      this.i++;
      this.skipWhitespace();
      if (s.charCodeAt(this.i) === close) {
        this.i++;

        return object ? "{}" : "[]";
      }
      const parts: string[] = [];
      for (;;) {
        this.skipWhitespace();
        if (object) {
          if (s.charCodeAt(this.i) !== 0x22) {
            this.fail();
          }
          const key = JSON.stringify(this.parseString());
          this.expect(0x3a);
          parts.push(`${key}:${this.compact()}`);
        } else {
          parts.push(this.compact());
        }
        this.skipWhitespace();
        const next = s.charCodeAt(this.i);
        this.i++;
        if (next === 0x2c) {
          continue;
        }
        if (next === close) {
          break;
        }
        this.fail();
      }

      return object ? `{${parts.join(",")}}` : `[${parts.join(",")}]`;
    }
    if (code === 0x6e) {
      this.literal("null");

      return "null";
    }
    if (code === 0x74) {
      this.literal("true");

      return "true";
    }
    if (code === 0x66) {
      this.literal("false");

      return "false";
    }

    return this.parseNumber();
  }

  private literal(word: string): void {
    if (!this.s.startsWith(word, this.i)) {
      this.fail();
    }
    this.i += word.length;
  }

  private parseNumber(): string {
    NUMBER.lastIndex = this.i;
    const match = NUMBER.exec(this.s);
    if (!match || match[0].length === 0) {
      this.fail();
    }
    this.i += match[0].length;

    return match[0];
  }

  /** At `"`: the decoded string. */
  private parseString(): string {
    const s = this.s;
    const start = this.i + 1;
    let quote = s.indexOf('"', start);
    if (quote === -1) {
      this.fail();
    }
    const backslash = s.indexOf("\\", start);
    if (backslash === -1 || backslash > quote) {
      const value = s.slice(start, quote);
      this.checkControl(value);
      this.i = quote + 1;

      return value;
    }

    let result = "";
    let i = start;
    for (;;) {
      quote = s.indexOf('"', i);
      const escape = s.indexOf("\\", i);
      if (quote === -1) {
        this.fail();
      }
      if (escape === -1 || escape > quote) {
        const piece = s.slice(i, quote);
        this.checkControl(piece);
        result += piece;
        this.i = quote + 1;

        return result;
      }
      const piece = s.slice(i, escape);
      this.checkControl(piece);
      result += piece;
      const kind = s[escape + 1];
      i = escape + 2;
      switch (kind) {
        case '"':
          result += '"';
          break;
        case "\\":
          result += "\\";
          break;
        case "/":
          result += "/";
          break;
        case "b":
          result += "\b";
          break;
        case "f":
          result += "\f";
          break;
        case "n":
          result += "\n";
          break;
        case "r":
          result += "\r";
          break;
        case "t":
          result += "\t";
          break;
        case "u": {
          const hex = s.slice(i, i + 4);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) {
            this.fail();
          }
          result += String.fromCharCode(parseInt(hex, 16));
          i += 4;
          break;
        }
        default:
          this.fail();
      }
    }
  }

  /** Raw control characters are not allowed in JSON strings. */
  private checkControl(text: string): void {
    if (CONTROL.test(text)) {
      this.fail();
    }
  }
}
