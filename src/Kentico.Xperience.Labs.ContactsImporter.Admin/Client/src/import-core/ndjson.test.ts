import { describe, expect, it } from 'vitest';

import { openRecordSource } from './open';
import { collectRows, gzipBytes, makeFile, utf8 } from './test-helpers';

async function read(content: string | Uint8Array, name = 'test.ndjson', chunkSize = 0) {
  const source = await openRecordSource(makeFile(content, name, chunkSize), { sampleBytes: 4 });
  const rows = await collectRows(source);

  return { source, rows };
}

describe('openRecordSource: NDJSON', () => {
  it('reads one object per line', async () => {
    const { source, rows } = await read(
      '{"email":"a@b.co","name":"Ann"}\n{"email":"c@d.co","name":"Cid"}\n',
    );
    expect(source.format).toBe('ndjson');
    expect(source.dynamicColumns).toBe(true);
    expect(source.columns).toEqual(['email', 'name']);
    expect(rows).toEqual([
      { rowNumber: 1, values: ['a@b.co', 'Ann'] },
      { rowNumber: 2, values: ['c@d.co', 'Cid'] },
    ]);
  });

  it('converts values to text and flattens nested objects', async () => {
    const { source, rows } = await read(
      '{"id":7,"ok":true,"none":null,"address":{"city":"Brno","geo":{"lat":49.2}},"tags":["a","b"]}',
      'test.jsonl',
    );
    expect(source.columns).toEqual(['id', 'ok', 'none', 'address.city', 'address.geo.lat', 'tags']);
    expect(rows).toEqual([{ rowNumber: 1, values: ['7', 'true', '', 'Brno', '49.2', '["a","b"]'] }]);
  });

  it('appends columns first seen in later rows', async () => {
    const { source, rows } = await read('{"a":"1"}\n{"b":"2"}\n{"c":"3","a":"4"}\n');
    expect(source.columns).toEqual(['a', 'b', 'c']);
    expect(rows.map((row) => row.values)).toEqual([['1'], ['', '2'], ['4', '', '3']]);
  });

  it('uses line numbers as row numbers, skips blank lines, accepts CRLF and no final line break', async () => {
    const { rows } = await read('{"a":"1"}\r\n\r\n  \n{"a":"2"}\r\n{"a":"3"}');
    expect(rows).toEqual([
      { rowNumber: 1, values: ['1'] },
      { rowNumber: 4, values: ['2'] },
      { rowNumber: 5, values: ['3'] },
    ]);
  });

  it('flags lines that are not JSON objects', async () => {
    const { rows } = await read('{"a":"1"}\n{oops}\n[1,2]\n"text"\nnull\n{"a":"2"}\n');
    expect(rows).toEqual([
      { rowNumber: 1, values: ['1'] },
      { rowNumber: 2, values: [], error: 'invalidJson' },
      { rowNumber: 3, values: [], error: 'notAnObject' },
      { rowNumber: 4, values: [], error: 'notAnObject' },
      { rowNumber: 5, values: [], error: 'notAnObject' },
      { rowNumber: 6, values: ['2'] },
    ]);
  });

  it('gives the same rows whatever the stream chunk size (multi-byte text, BOM)', async () => {
    const text = '{"email":"žluťoučký@example.com","name":"Kůň 🐎\\n\\"Šemík\\""}\r\n{"email":"日本@example.jp"}\n';
    const bytes = new Uint8Array([0xef, 0xbb, 0xbf, ...utf8(text)]);
    const expected = [
      { rowNumber: 1, values: ['žluťoučký@example.com', 'Kůň 🐎\n"Šemík"'] },
      // values are padded to the columns known so far
      { rowNumber: 2, values: ['日本@example.jp', ''] },
    ];
    for (const chunkSize of [0, 1, 2, 3, 5, 16]) {
      expect((await read(bytes, 'test.ndjson', chunkSize)).rows, `chunk size ${chunkSize}`).toEqual(expected);
    }
  });

  it('reads gzipped NDJSON', async () => {
    const lines: string[] = [];
    for (let i = 0; i < 1000; i++) {
      lines.push(JSON.stringify({ email: `user${i}@example.com`, n: i }));
    }
    const { source, rows } = await read(await gzipBytes(utf8(lines.join('\n'))), 'x.ndjson.gz', 256);
    expect(source.info.compressed).toBe(true);
    expect(rows).toHaveLength(1000);
    expect(rows[999]).toEqual({ rowNumber: 1000, values: ['user999@example.com', '999'] });
  });

  it('fails on a line over the size cap', async () => {
    const source = await openRecordSource(makeFile(`{"a":"1"}\n{"a":"${'x'.repeat(5000)}`, 'big.ndjson', 100), {
      maxRecordChars: 1000,
      sampleBytes: 4,
    });
    await expect(collectRows(source)).rejects.toMatchObject({ code: 'recordTooLarge' });
  });
});
