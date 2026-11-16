import { describe, expect, it } from 'vitest';

import { detectFormat, isGzip, openRecordSource } from './open';
import type { OpenSourceOptions } from './open';
import { ImportError } from './source';
import { collectRows, gzipBytes, makeFile, utf8 } from './test-helpers';
import { detectEncoding } from './text-source';

async function read(
  content: string | Uint8Array,
  options: OpenSourceOptions = {},
  name = 'test.csv',
  chunkSize = 0,
) {
  const source = await openRecordSource(makeFile(content, name, chunkSize), options);
  const rows = await collectRows(source);

  return { source, rows, values: rows.map((row) => row.values) };
}

describe('openRecordSource: CSV', () => {
  it('reads header and rows with 1-based row numbers', async () => {
    const { source, rows } = await read('Email,Name\na@b.co,Ann\nc@d.co,Cid\n');
    expect(source.format).toBe('csv');
    expect(source.columns).toEqual(['Email', 'Name']);
    expect(source.dynamicColumns).toBe(false);
    expect(rows).toEqual([
      { rowNumber: 1, values: ['a@b.co', 'Ann'] },
      { rowNumber: 2, values: ['c@d.co', 'Cid'] },
    ]);
    expect(source.info).toEqual({
      encoding: 'utf-8',
      compressed: false,
      delimiter: ',',
      delimiterDetected: true,
      hasHeader: true,
    });
    expect(source.bytesRead).toBe(source.totalBytes);
  });

  it('strips the UTF-8 BOM, also when it is split across stream chunks', async () => {
    const bytes = new Uint8Array([0xef, 0xbb, 0xbf, ...utf8('"Email",Name\na@b.co,Ann\n')]);
    for (const chunkSize of [0, 1, 2]) {
      const { source, values } = await read(bytes, { sampleBytes: 4 }, 'bom.csv', chunkSize);
      expect(source.columns).toEqual(['Email', 'Name']);
      expect(values).toEqual([['a@b.co', 'Ann']]);
    }
  });

  it('counts blank lines in row numbers and skips blank lines before the header', async () => {
    const { source, rows } = await read('\n\nEmail,Name\n\na@b.co,Ann\n\n\nc@d.co,Cid');
    expect(source.columns).toEqual(['Email', 'Name']);
    expect(rows.map((row) => row.rowNumber)).toEqual([2, 5]);
  });

  it('reads files without a header', async () => {
    const { source, rows } = await read('a@b.co,Ann\nc@d.co,Cid\n', { hasHeader: false });
    expect(source.columns).toEqual(['Column 1', 'Column 2']);
    expect(rows).toEqual([
      { rowNumber: 1, values: ['a@b.co', 'Ann'] },
      { rowNumber: 2, values: ['c@d.co', 'Cid'] },
    ]);
  });

  it.each([
    [';', 'x.csv'],
    ['\t', 'x.tsv'],
    ['|', 'x.txt'],
  ])('detects delimiter %j', async (delimiter, name) => {
    const text = ['Email', 'Name', 'City'].join(delimiter) + '\n' + ['a@b.co', 'Ann', 'Brno'].join(delimiter) + '\n';
    const { source, values } = await read(text, {}, name);
    expect(source.info.delimiter).toBe(delimiter);
    expect(source.info.delimiterDetected).toBe(true);
    expect(values).toEqual([['a@b.co', 'Ann', 'Brno']]);
  });

  it('lets the delimiter be overridden', async () => {
    const { source, values } = await read('a;b,c;d\n1;2,3;4\n', { delimiter: ',' });
    expect(source.columns).toEqual(['a;b', 'c;d']);
    expect(source.info.delimiterDetected).toBe(false);
    expect(values).toEqual([['1;2', '3;4']]);
  });

  it('falls back to comma, or tab for .tsv, for single-column files', async () => {
    expect((await read('Email\na@b.co\n')).source.info).toMatchObject({
      delimiter: ',',
      delimiterDetected: false,
    });
    expect((await read('Email\na@b.co\n', {}, 'one.tsv')).source.info.delimiter).toBe('\t');
  });

  it('handles CRLF, CR, quoted line breaks and a missing final line break', async () => {
    const { values } = await read('Email,Note\r\na@b.co,"two\r\nlines"\rc@d.co,"x,y"\ne@f.co,""""');
    expect(values).toEqual([
      ['a@b.co', 'two\r\nlines'],
      ['c@d.co', 'x,y'],
      ['e@f.co', '"'],
    ]);
  });

  it('yields ragged rows unchanged', async () => {
    const { values } = await read('a,b,c\n1\n1,2,3,4\n');
    expect(values).toEqual([['1'], ['1', '2', '3', '4']]);
  });

  it('flags a row with an unterminated quote', async () => {
    const { rows } = await read('a,b\n1,"open\n2,3\n');
    expect(rows).toEqual([{ rowNumber: 1, values: ['1', 'open\n2,3\n'], error: 'unterminatedQuote' }]);
  });

  it('decodes multi-byte UTF-8 split across stream chunks at any byte', async () => {
    const text = 'Email,Name\nžluťoučký@example.com,"Kůň 🐎, ""Šemík"""\r\n日本@example.jp,語\n';
    const expected = [
      ['žluťoučký@example.com', 'Kůň 🐎, "Šemík"'],
      ['日本@example.jp', '語'],
    ];
    for (const chunkSize of [1, 2, 3, 5, 7, 11]) {
      const { source, values } = await read(text, { sampleBytes: 4 }, 'utf8.csv', chunkSize);
      expect(source.columns, `chunk size ${chunkSize}`).toEqual(['Email', 'Name']);
      expect(values, `chunk size ${chunkSize}`).toEqual(expected);
    }
  });

  it('handles a quote pair and CRLF split across stream chunks (delimiter override)', async () => {
    const text = 'a,b\r\n"x""y","1\r\n2"\r\n"",z\r\n';
    for (let chunkSize = 1; chunkSize <= text.length; chunkSize++) {
      const { values } = await read(text, { delimiter: ',', sampleBytes: 4 }, 'q.csv', chunkSize);
      expect(values, `chunk size ${chunkSize}`).toEqual([
        ['x"y', '1\r\n2'],
        ['', 'z'],
      ]);
    }
  });

  it('decodes windows-1252 with the encoding override', async () => {
    // "Name;City\nJosé;Zürich\n" in windows-1252
    const bytes = new Uint8Array([
      ...utf8('Name;City\nJos'), 0xe9, ...utf8(';Z'), 0xfc, ...utf8('rich\n'),
    ]);
    const { source, values } = await read(bytes, { encoding: 'windows-1252' });
    expect(source.info.encoding).toBe('windows-1252');
    expect(values).toEqual([['José', 'Zürich']]);
  });

  it('falls back to windows-1252 when the bytes are not UTF-8', async () => {
    const bytes = new Uint8Array([...utf8('Name\nJos'), 0xe9, ...utf8('\n')]);
    const { source, values } = await read(bytes);
    expect(source.info.encoding).toBe('windows-1252');
    expect(values).toEqual([['José']]);
  });

  it('decodes UTF-16 with a BOM', async () => {
    const text = 'Email,Name\na@b.co,Žofie\n';
    const le = new Uint8Array(2 + text.length * 2);
    const be = new Uint8Array(2 + text.length * 2);
    le.set([0xff, 0xfe]);
    be.set([0xfe, 0xff]);
    for (let i = 0; i < text.length; i++) {
      const code = text.charCodeAt(i);
      le.set([code & 0xff, code >> 8], 2 + i * 2);
      be.set([code >> 8, code & 0xff], 2 + i * 2);
    }

    for (const [bytes, encoding] of [[le, 'utf-16le'], [be, 'utf-16be']] as const) {
      const { source, values } = await read(bytes, { sampleBytes: 4 }, 'utf16.csv', 3);
      expect(source.info.encoding).toBe(encoding);
      expect(source.columns).toEqual(['Email', 'Name']);
      expect(values).toEqual([['a@b.co', 'Žofie']]);
    }
  });

  it('rejects an unknown encoding', async () => {
    await expect(read('a,b\n', { encoding: 'klingon-8' })).rejects.toMatchObject({
      code: 'unsupportedEncoding',
    });
  });

  it('reads gzipped files and reports compressed byte progress', async () => {
    const lines = ['Email,Name'];
    for (let i = 0; i < 2000; i++) {
      lines.push(`user${i}@example.com,Name ${i}`);
    }
    const gz = await gzipBytes(utf8(lines.join('\n')));
    const { source, rows } = await read(gz, {}, 'contacts.csv.gz', 512);
    expect(source.info.compressed).toBe(true);
    expect(source.totalBytes).toBe(gz.byteLength);
    expect(source.bytesRead).toBe(gz.byteLength);
    expect(rows).toHaveLength(2000);
    expect(rows[1999]).toEqual({ rowNumber: 2000, values: ['user1999@example.com', 'Name 1999'] });
  });

  it('detects gzip by content, not by name', async () => {
    const gz = await gzipBytes(utf8('Email\na@b.co\n'));
    expect(await isGzip(makeFile(gz, 'plain.csv'))).toBe(true);
    expect(await isGzip(makeFile('Email\n', 'fake.csv.gz'))).toBe(false);
    expect((await read(gz, {}, 'plain.csv')).values).toEqual([['a@b.co']]);
    expect((await read('Email\na@b.co\n', {}, 'fake.csv.gz')).values).toEqual([['a@b.co']]);
  });

  it('handles an empty file and a header-only file', async () => {
    const empty = await read('');
    expect(empty.source.columns).toEqual([]);
    expect(empty.rows).toEqual([]);

    const headerOnly = await read('Email,Name');
    expect(headerOnly.source.columns).toEqual(['Email', 'Name']);
    expect(headerOnly.rows).toEqual([]);
  });

  it('fails on a record over the size cap instead of buffering the file', async () => {
    const text = `a,b\n1,"never closed ${'x'.repeat(5000)}`;
    const source = await openRecordSource(makeFile(text, 'big.csv', 100), {
      maxRecordChars: 1000,
      sampleBytes: 4,
    });
    await expect(collectRows(source)).rejects.toMatchObject({ code: 'recordTooLarge' });
  });

  it('reads lazily: only what the consumer asks for', async () => {
    const lines = ['Email,Name'];
    for (let i = 0; i < 5000; i++) {
      lines.push(`user${i}@example.com,Name ${i}`);
    }
    const file = makeFile(lines.join('\n'), 'lazy.csv', 1024);
    const totalChunks = Math.ceil(file.size / 1024);
    expect(totalChunks).toBeGreaterThan(100);

    const source = await openRecordSource(file, { sampleBytes: 2048 });
    expect(source.columns).toEqual(['Email', 'Name']);
    expect(file.pulls).toBeLessThanOrEqual(3);

    const batches = source.batches();
    await batches.next();
    await batches.next();
    expect(file.pulls).toBeLessThanOrEqual(5);
    expect(source.bytesRead).toBeLessThanOrEqual(5 * 1024);

    await batches.return();
    expect(file.cancelled).toBe(true);
    expect(file.pulls).toBeLessThanOrEqual(5);
  });

  it('can be iterated only once', async () => {
    const source = await openRecordSource(makeFile('a,b\n1,2\n'));
    await collectRows(source);
    await expect(collectRows(source)).rejects.toBeInstanceOf(ImportError);
  });
});

describe('format and encoding detection', () => {
  it('maps file names to formats', () => {
    expect(detectFormat('a.csv')).toBe('csv');
    expect(detectFormat('A.TSV')).toBe('csv');
    expect(detectFormat('a.txt.gz')).toBe('csv');
    expect(detectFormat('a.ndjson')).toBe('ndjson');
    expect(detectFormat('a.jsonl.gz')).toBe('ndjson');
    expect(detectFormat('a.xlsx')).toBeNull();
    expect(detectFormat('a.json')).toBeNull();
    expect(detectFormat('noextension')).toBeNull();
  });

  it('rejects formats without a parser unless the format is given', async () => {
    await expect(openRecordSource(makeFile('a,b\n', 'a.xlsx'))).rejects.toMatchObject({
      code: 'unsupportedFormat',
    });
    const source = await openRecordSource(makeFile('a,b\n1,2\n', 'export.dat'), { format: 'csv' });
    expect(await collectRows(source)).toEqual([{ rowNumber: 1, values: ['1', '2'] }]);
  });

  it('detects encodings from the first bytes', () => {
    expect(detectEncoding(new Uint8Array([0xef, 0xbb, 0xbf, 0x41]), false)).toBe('utf-8');
    expect(detectEncoding(new Uint8Array([0xff, 0xfe, 0x41, 0x00]), false)).toBe('utf-16le');
    expect(detectEncoding(new Uint8Array([0xfe, 0xff, 0x00, 0x41]), false)).toBe('utf-16be');
    expect(detectEncoding(utf8('plain ascii'), true)).toBe('utf-8');
    expect(detectEncoding(utf8('žluťoučký'), true)).toBe('utf-8');
    // multi-byte sequence cut off at the end of the sample: still UTF-8 unless it is the whole file
    expect(detectEncoding(utf8('é').subarray(0, 1), false)).toBe('utf-8');
    expect(detectEncoding(utf8('é').subarray(0, 1), true)).toBe('windows-1252');
    expect(detectEncoding(new Uint8Array([0x4a, 0xe9, 0x20]), false)).toBe('windows-1252');
  });
});
