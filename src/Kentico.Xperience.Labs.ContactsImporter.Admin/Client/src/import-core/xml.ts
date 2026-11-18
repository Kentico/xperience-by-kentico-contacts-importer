/** Callbacks of `XmlTokenizer`. Element names come without a namespace prefix (`x:row` -> `row`). */
export interface XmlHandler {
  /** Start tag; `attributes` is the raw attribute text (see `parseXmlAttributes`). */
  open(name: string, attributes: string, selfClosing: boolean): void;
  close(name: string): void;
  /** Character data with entities decoded; one text node can arrive in several calls. */
  text(text: string): void;
}

const ENTITY = /&(#[xX][0-9a-fA-F]+|#[0-9]+|amp|lt|gt|quot|apos);/g;

function decodeEntity(_match: string, entity: string): string {
  switch (entity) {
    case "amp":
      return "&";
    case "lt":
      return "<";
    case "gt":
      return ">";
    case "quot":
      return '"';
    case "apos":
      return "'";
    default: {
      const code =
        entity[1] === "x" || entity[1] === "X"
          ? parseInt(entity.slice(2), 16)
          : parseInt(entity.slice(1), 10);

      return code >= 0 && code <= 0x10ffff ? String.fromCodePoint(code) : "";
    }
  }
}

export function decodeXmlEntities(text: string): string {
  return !text.includes("&") ? text : text.replace(ENTITY, decodeEntity);
}

/** Local name of a tag: no namespace prefix. */
function localName(qualified: string): string {
  const colon = qualified.indexOf(":");

  return colon === -1 ? qualified : qualified.slice(colon + 1);
}

/** Decoded attribute values by name (without namespace prefix); a missing attribute is `undefined`. */
export type XmlAttributes = Partial<Record<string, string>>;

/** Attributes of a raw attribute text as name -> decoded value (names without namespace prefix). */
export function parseXmlAttributes(raw: string): XmlAttributes {
  const result: XmlAttributes = {};
  const length = raw.length;
  let i = 0;
  while (i < length) {
    const code = raw.charCodeAt(i);
    if (code <= 32) {
      i++;
      continue;
    }
    const eq = raw.indexOf("=", i);
    if (eq === -1) {
      break;
    }
    const name = raw.slice(i, eq).trim();
    let q = eq + 1;
    while (q < length && raw.charCodeAt(q) <= 32) {
      q++;
    }
    const quote = raw[q];
    if (quote !== '"' && quote !== "'") {
      break;
    }
    const close = raw.indexOf(quote, q + 1);
    if (close === -1) {
      break;
    }
    result[localName(name)] = decodeXmlEntities(raw.slice(q + 1, close));
    i = close + 1;
  }

  return result;
}

/**
 * Small streaming XML tokenizer for the well-formed, DTD-free parts of an XLSX package: start / end
 * tags, character data (entities, CDATA), comments and processing instructions. Text can be pushed
 * in pieces split at any position. Not a validating parser: it trusts the producer's nesting.
 */
export class XmlTokenizer {
  private readonly handler: XmlHandler;
  private buffer = "";

  constructor(handler: XmlHandler) {
    this.handler = handler;
  }

  push(chunk: string): void {
    const text = this.buffer.length > 0 ? this.buffer + chunk : chunk;
    const consumed = this.parse(text);
    this.buffer = consumed < text.length ? text.slice(consumed) : "";
  }

  end(): void {
    if (this.buffer.length > 0) {
      const rest = this.buffer;
      this.buffer = "";
      // whatever is left is text, or a tag cut off by the end of the input (ignored)
      if (!rest.includes("<")) {
        this.handler.text(decodeXmlEntities(rest));
      }
    }
  }

  /** Handles complete tokens of `text`; returns where the unfinished rest starts. */
  private parse(text: string): number {
    const handler = this.handler;
    const length = text.length;
    let pos = 0;

    while (pos < length) {
      const lt = text.indexOf("<", pos);
      if (lt === -1) {
        // Text up to the end: keep a possibly cut entity for the next piece.
        const amp = text.lastIndexOf("&");
        const safeEnd = amp >= pos && !text.includes(";", amp) ? amp : length;
        if (safeEnd > pos) {
          handler.text(decodeXmlEntities(text.slice(pos, safeEnd)));
        }

        return safeEnd;
      }
      if (lt > pos) {
        handler.text(decodeXmlEntities(text.slice(pos, lt)));
      }
      if (lt + 1 >= length) {
        return lt;
      }

      const next = text.charCodeAt(lt + 1);
      if (next === 0x3f /* ? */) {
        const end = text.indexOf("?>", lt + 2);
        if (end === -1) {
          return lt;
        }
        pos = end + 2;
        continue;
      }
      if (next === 0x21 /* ! */) {
        if (length - lt < 9) {
          return lt;
        }
        if (text.startsWith("<!--", lt)) {
          const end = text.indexOf("-->", lt + 4);
          if (end === -1) {
            return lt;
          }
          pos = end + 3;
          continue;
        }
        if (text.startsWith("<![CDATA[", lt)) {
          const end = text.indexOf("]]>", lt + 9);
          if (end === -1) {
            return lt;
          }
          handler.text(text.slice(lt + 9, end));
          pos = end + 3;
          continue;
        }
        const end = text.indexOf(">", lt + 2);
        if (end === -1) {
          return lt;
        }
        pos = end + 1;
        continue;
      }
      if (next === 0x2f /* / */) {
        const end = text.indexOf(">", lt + 2);
        if (end === -1) {
          return lt;
        }
        handler.close(localName(text.slice(lt + 2, end).trim()));
        pos = end + 1;
        continue;
      }

      // Start tag: find the closing '>' outside attribute quotes.
      let i = lt + 1;
      let quote = 0;
      let end = -1;
      for (; i < length; i++) {
        const code = text.charCodeAt(i);
        if (quote !== 0) {
          if (code === quote) {
            quote = 0;
          }
        } else if (code === 0x22 || code === 0x27) {
          quote = code;
        } else if (code === 0x3e /* > */) {
          end = i;
          break;
        }
      }
      if (end === -1) {
        return lt;
      }

      const selfClosing = text.charCodeAt(end - 1) === 0x2f;
      const inner = text.slice(lt + 1, selfClosing ? end - 1 : end);
      let nameEnd = 0;
      while (nameEnd < inner.length && inner.charCodeAt(nameEnd) > 32) {
        nameEnd++;
      }
      const name = localName(inner.slice(0, nameEnd));
      handler.open(name, inner.slice(nameEnd), selfClosing);
      if (selfClosing) {
        handler.close(name);
      }
      pos = end + 1;
    }

    return pos;
  }
}

/** Decodes the `_xHHHH_` escapes OOXML uses in strings for characters XML cannot hold (e.g. `_x000D_`). */
export function decodeOoxmlEscapes(text: string): string {
  return !text.includes("_x")
    ? text
    : text.replace(/_x([0-9A-Fa-f]{4})_/g, (_m, hex: string) =>
        String.fromCharCode(parseInt(hex, 16)),
      );
}
