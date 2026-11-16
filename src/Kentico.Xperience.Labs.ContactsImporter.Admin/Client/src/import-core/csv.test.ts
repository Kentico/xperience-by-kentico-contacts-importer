import { describe, expect, it } from 'vitest';

import { CsvParser, detectDelimiter, normalizeHeaders } from './csv';
import type { CsvParserOptions, CsvRawRecord } from './csv';
import { ImportError } from './source';

function parsePieces(pieces: string[], options?: CsvParserOptions): CsvRawRecord[] {
  const parser = new CsvParser(options);
  const out: CsvRawRecord[] = [];
  for (const piece of pieces) {
    parser.push(piece, out);
  }
  parser.end(out);

  return out;
}

function fields(text: string, options?: CsvParserOptions): string[][] {
  return parsePieces([text], options).map((record) => record.fields);
}

describe('CsvParser', () => {
  it('parses plain records', () => {
    expect(fields('a,b,c\n1,2,3\n')).toEqual([
      ['a', 'b', 'c'],
      ['1', '2', '3'],
    ]);
  });

  it('handles quoted fields: delimiters, escaped quotes, embedded line breaks', () => {
    expect(fields('"a,b","c""d","line1\nline2\r\nline3",""""\n')).toEqual([
      ['a,b', 'c"d', 'line1\nline2\r\nline3', '"'],
    ]);
  });

  it('accepts CRLF, LF and CR record ends', () => {
    expect(fields('a,b\r\nc,d\re,f\ng,h')).toEqual([
      ['a', 'b'],
      ['c', 'd'],
      ['e', 'f'],
      ['g', 'h'],
    ]);
  });

  it('gives the same rows with and without a trailing line break', () => {
    const expected = [['a', 'b'], ['c', 'd']];
    expect(fields('a,b\nc,d')).toEqual(expected);
    expect(fields('a,b\nc,d\n')).toEqual(expected);
    expect(fields('a,b\r\nc,d\r\n')).toEqual(expected);
    expect(fields('a,b\rc,d\r')).toEqual(expected);
  });

  it('skips blank lines but counts them in the record index', () => {
    const records = parsePieces(['a\n\n\r\nb\n\n']);
    expect(records).toEqual([
      { index: 0, fields: ['a'] },
      { index: 3, fields: ['b'] },
    ]);
  });

  it('keeps a quoted empty value as a record', () => {
    expect(fields('""\n')).toEqual([['']]);
    expect(fields('""')).toEqual([['']]);
  });

  it('keeps ragged rows as they are', () => {
    expect(fields('a,b,c\n1\n1,2,3,4\n1,2,\n,\n')).toEqual([
      ['a', 'b', 'c'],
      ['1'],
      ['1', '2', '3', '4'],
      ['1', '2', ''],
      ['', ''],
    ]);
  });

  it('treats quotes that do not start a field as data', () => {
    expect(fields('a"b,c "d" e,"ab"cd\n')).toEqual([['a"b', 'c "d" e', 'abcd']]);
  });

  it('supports other delimiters and turning quoting off', () => {
    expect(fields('a;"b;c"\n', { delimiter: ';' })).toEqual([['a', 'b;c']]);
    expect(fields('a\t"b\tc"\n', { delimiter: '\t' })).toEqual([['a', 'b\tc']]);
    expect(fields('a|b\n', { delimiter: '|' })).toEqual([['a', 'b']]);
    expect(fields('"a,b"\n', { quote: '' })).toEqual([['"a', 'b"']]);
    expect(fields("'a,b',c\n", { quote: "'" })).toEqual([['a,b', 'c']]);
  });

  it('flags an unterminated quote at the end of input', () => {
    expect(parsePieces(['a,b\nc,"d\ne'])).toEqual([
      { index: 0, fields: ['a', 'b'] },
      { index: 1, fields: ['c', 'd\ne'], error: 'unterminatedQuote' },
    ]);
  });

  it('gives the same result for every split position', () => {
    const text =
      'id,name,note\r\n' +
      '1,"Doe, Jane","said ""hi"""\r\n' +
      '\r\n' +
      '2,Žluťoučký kůň 🐎,"multi\r\nline"\r' +
      '3,"",""""\n' +
      '4,"a""","b"\n' +
      '5,last,"x"';
    const expected = parsePieces([text]);
    expect(expected.map((r) => r.fields)).toEqual([
      ['id', 'name', 'note'],
      ['1', 'Doe, Jane', 'said "hi"'],
      ['2', 'Žluťoučký kůň 🐎', 'multi\r\nline'],
      ['3', '', '"'],
      ['4', 'a"', 'b'],
      ['5', 'last', 'x'],
    ]);

    for (let i = 0; i <= text.length; i++) {
      expect(parsePieces([text.slice(0, i), text.slice(i)]), `split at ${i}`).toEqual(expected);
    }
    for (let i = 0; i < text.length; i++) {
      for (const j of [i + 1, i + 2]) {
        expect(
          parsePieces([text.slice(0, i), text.slice(i, j), text.slice(j)]),
          `split at ${i} and ${j}`,
        ).toEqual(expected);
      }
    }
    expect(parsePieces(Array.from(text)), 'one UTF-16 unit at a time').toEqual(expected);
  });

  it('stops on a record over the size cap (unbalanced quote)', () => {
    const parser = new CsvParser({ maxRecordChars: 20 });
    const out: CsvRawRecord[] = [];
    parser.push('a,b\n"never closed,', out);
    expect(() => parser.push('x'.repeat(30), out)).toThrowError(ImportError);
    expect(out).toHaveLength(1);
  });

  it('rejects invalid dialects', () => {
    expect(() => new CsvParser({ delimiter: '' })).toThrowError(ImportError);
    expect(() => new CsvParser({ delimiter: '::' })).toThrowError(ImportError);
    expect(() => new CsvParser({ delimiter: '"' })).toThrowError(ImportError);
  });
});

describe('detectDelimiter', () => {
  it.each([
    [',', 'a,b,c\n1,2,3\n4,5,6\n'],
    [';', 'a;b;c\n1;2;3\n4;5;6\n'],
    ['\t', 'a\tb\tc\n1\t2\t3\n'],
    ['|', 'a|b|c\n1|2|3\n'],
  ])('detects %j', (delimiter, sample) => {
    expect(detectDelimiter(sample)).toBe(delimiter);
  });

  it('prefers the consistent delimiter over one that appears inside values', () => {
    expect(detectDelimiter('name;amount\n"Doe, Jane";1,5\nSmith;2\nNovak, Petr;3,25\n')).toBe(';');
    expect(detectDelimiter('email,note\na@b.co,"x;y;z"\nc@d.co,plain\n')).toBe(',');
  });

  it('ignores the cut-off last record of an incomplete sample', () => {
    expect(detectDelimiter('a;b;c\n1;2;3\n4;5')).toBe(';');
    expect(detectDelimiter('a,b;c,d\n1,2;3,4\n5;6', { complete: false })).toBe(',');
  });

  it('uses a single unfinished record when that is all there is', () => {
    expect(detectDelimiter('a;b;c')).toBe(';');
  });

  it('returns null for a single column or empty sample', () => {
    expect(detectDelimiter('email\na@b.co\nc@d.co\n')).toBeNull();
    expect(detectDelimiter('')).toBeNull();
  });
});

describe('normalizeHeaders', () => {
  it('trims, names empty headers and makes duplicates unique', () => {
    expect(normalizeHeaders([' Email ', '', 'Email', 'Email', 'Column 2'])).toEqual([
      'Email',
      'Column 2',
      'Email (2)',
      'Email (3)',
      'Column 2 (2)',
    ]);
  });
});
