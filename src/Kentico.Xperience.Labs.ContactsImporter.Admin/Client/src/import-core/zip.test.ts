import { describe, expect, it } from 'vitest';

import { buildZip, tableXlsx } from './format-test-helpers';
import type { ZipTestEntry } from './format-test-helpers';
import { openRecordSource } from './open';
import { collectRows, makeFile } from './test-helpers';
import { ZipArchive, isZipJunk } from './zip';

const CSV = 'Email,Name\na@b.co,Ann\nc@d.co,Cid\n';
const CSV_ROWS = [
  { rowNumber: 1, values: ['a@b.co', 'Ann'] },
  { rowNumber: 2, values: ['c@d.co', 'Cid'] },
];

async function readZip(entries: ZipTestEntry[], options: Parameters<typeof buildZip>[1] = {}, chunkSize = 0) {
  const bytes = await buildZip(entries, options);
  const source = await openRecordSource(makeFile(bytes, 'contacts.zip', chunkSize), { sampleBytes: 4 });
  const rows = await collectRows(source);

  return { source, rows, bytes };
}

describe('ZipArchive', () => {
  it('lists entries and streams stored and deflated data in pieces', async () => {
    const big = 'x'.repeat(10_000) + 'end';
    const bytes = await buildZip([
      { name: 'a.txt', data: 'stored text' },
      { name: 'dir/b.txt', data: big, method: 8 },
    ]);
    const archive = await ZipArchive.open(makeFile(bytes, 'x.zip'));
    expect(archive.entries.map((entry) => [entry.name, entry.method, entry.uncompressedSize])).toEqual([
      ['a.txt', 0, 11],
      ['dir/b.txt', 8, big.length],
    ]);

    let read = 0;
    const entry = archive.find('DIR/B.TXT')!;
    const reader = (await archive.stream(entry, (n) => (read += n), 7)).getReader();
    let text = '';
    const decoder = new TextDecoder();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      text += decoder.decode(value, { stream: true });
    }
    expect(text).toBe(big);
    expect(read).toBe(entry.compressedSize);
    expect(new TextDecoder().decode(await archive.readBytes(archive.find('a.txt')!))).toBe('stored text');
  });

  it('reads ZIP64 size, offset and end records', async () => {
    const bytes = await buildZip(
      [
        { name: 'one.csv', data: CSV, method: 8 },
        { name: 'two.csv', data: CSV },
      ],
      { zip64: true },
    );
    const archive = await ZipArchive.open(makeFile(bytes, 'x.zip'));
    expect(archive.entries.map((entry) => [entry.name, entry.uncompressedSize])).toEqual([
      ['one.csv', CSV.length],
      ['two.csv', CSV.length],
    ]);
    expect(archive.entries[1].localHeaderOffset).toBeGreaterThan(0);
    expect(new TextDecoder().decode(await archive.readBytes(archive.entries[1]))).toBe(CSV);
  });

  it('finds the end record behind an archive comment', async () => {
    const bytes = await buildZip([{ name: 'a.csv', data: CSV }], { comment: 'PK made by a test' });
    const archive = await ZipArchive.open(makeFile(bytes, 'x.zip'));
    expect(archive.entries).toHaveLength(1);
  });

  it('fails on a damaged file', async () => {
    await expect(ZipArchive.open(makeFile('PK\x03\x04 not really a zip file at all', 'x.zip'))).rejects.toMatchObject({
      code: 'invalidZip',
    });
  });

  it('knows the files archivers add on their own', () => {
    expect(['dir/', '__MACOSX/._a.csv', 'x/.DS_Store', '._a.csv', 'Thumbs.db'].every(isZipJunk)).toBe(true);
    expect(isZipJunk('data/contacts.csv')).toBe(false);
  });
});

describe('openRecordSource: zip', () => {
  it('reads the one data file in its own format (stored and deflate)', async () => {
    for (const method of [0, 8]) {
      const { source, rows } = await readZip([{ name: 'export/contacts.csv', data: CSV, method }]);
      expect(source.format).toBe('csv');
      expect(source.info).toMatchObject({ zipEntry: 'export/contacts.csv', delimiter: ',' });
      expect(rows).toEqual(CSV_ROWS);
    }

    const { source, rows } = await readZip([{ name: 'c.json', data: '[{"a":1},{"a":2}]', method: 8 }]);
    expect(source.format).toBe('json');
    expect(rows.map((row) => row.values)).toEqual([['1'], ['2']]);
  });

  it('ignores directories and archiver metadata', async () => {
    const { rows } = await readZip([
      { name: 'export/', data: '' },
      { name: '__MACOSX/export/._contacts.csv', data: 'junk' },
      { name: 'export/.DS_Store', data: 'junk' },
      { name: 'export/contacts.csv', data: CSV, method: 8 },
    ]);
    expect(rows).toEqual(CSV_ROWS);
  });

  it('reports progress as compressed bytes of the entry, scaled to the zip size', async () => {
    const lines = ['Email'];
    for (let i = 0; i < 20_000; i++) {
      lines.push(`user${i}@example.com`);
    }
    const bytes = await buildZip([{ name: 'big.csv', data: lines.join('\n'), method: 8 }]);
    const source = await openRecordSource(makeFile(bytes, 'big.zip'));
    expect(source.totalBytes).toBe(bytes.length);
    expect(source.bytesRead).toBeLessThanOrEqual(bytes.length);
    expect(await collectRows(source)).toHaveLength(20_000);
    expect(source.bytesRead).toBe(bytes.length);
  });

  it('applies a format override to the file inside', async () => {
    const bytes = await buildZip([{ name: 'data.txt', data: '{"a":"1"}\n{"a":"2"}\n' }]);
    const source = await openRecordSource(makeFile(bytes, 'data.zip'), { format: 'ndjson' });
    expect(source.format).toBe('ndjson');
    expect((await collectRows(source)).map((row) => row.values)).toEqual([['1'], ['2']]);
  });

  it('reads an XLSX workbook inside a zip (stored and deflate)', async () => {
    const workbook = await tableXlsx([
      ['Email', 'Name'],
      ['a@b.co', 'Ann'],
    ]);
    for (const method of [0, 8]) {
      const { source, rows, bytes } = await readZip([{ name: 'contacts.xlsx', data: workbook, method }]);
      expect(source.format).toBe('xlsx');
      expect(source.info).toMatchObject({ zipEntry: 'contacts.xlsx', sheet: 'Sheet1' });
      expect(source.columns).toEqual(['Email', 'Name']);
      expect(rows).toEqual([{ rowNumber: 1, values: ['a@b.co', 'Ann'] }]);
      expect(source.totalBytes).toBe(bytes.length);
      expect(source.bytesRead).toBe(bytes.length);
    }
  });

  it('fails when the zip holds no data file or more than one', async () => {
    await expect(readZip([{ name: 'dir/', data: '' }])).rejects.toMatchObject({ code: 'zipEmpty' });
    await expect(
      readZip([
        { name: 'a.csv', data: CSV },
        { name: 'b.csv', data: CSV },
      ]),
    ).rejects.toMatchObject({ code: 'zipMultipleEntries', params: { count: 2 } });
  });

  it('fails on encrypted entries and unsupported compression', async () => {
    await expect(readZip([{ name: 'a.csv', data: CSV, flags: 1 }])).rejects.toMatchObject({ code: 'zipEncrypted' });
    await expect(readZip([{ name: 'a.csv', data: CSV, method: 12 }])).rejects.toMatchObject({
      code: 'unsupportedCompression',
      params: { method: 12 },
    });
  });

  it('fails on damaged deflate data', async () => {
    const bytes = await buildZip([{ name: 'a.csv', data: CSV.repeat(50), method: 8 }]);
    // overwrite the start of the compressed data (after the 30-byte header + name)
    bytes.fill(0xff, 30 + 'a.csv'.length, 30 + 'a.csv'.length + 20);
    await expect(openRecordSource(makeFile(bytes, 'a.zip')).then(collectRows)).rejects.toMatchObject({ code: 'invalidZip' });
  });

  it('refuses nested archives and unknown file types inside', async () => {
    await expect(readZip([{ name: 'a.csv.gz', data: 'x' }])).rejects.toMatchObject({ code: 'unsupportedFormat' });
    await expect(readZip([{ name: 'a.xls', data: 'x' }])).rejects.toMatchObject({
      code: 'unsupportedFormat',
      params: { extension: 'xls' },
    });
  });
});
